using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Brisa.Localization;
using Brisa.Models;
using Brisa.Security;
using Brisa.Services;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using HAlign = System.Windows.HorizontalAlignment;
using Point = System.Windows.Point;
using ProgressBar = System.Windows.Controls.ProgressBar;

namespace Brisa.Controls;

/// <summary>
/// The notification list. Every string from the feed goes into a TextBlock.Text and nowhere else:
/// no HTML, no embedded browser, no markup parsing. Links open only through NotificationUrlPolicy.
/// </summary>
public partial class NotificationsPopover : UserControl
{
    private INotificationService? _service;
    private NotificationSnapshot _snapshot = NotificationSnapshot.Empty;
    private bool _unreadOnly;
    private bool _indicatorRetryPending;
    private bool _syncingIndicator;
    private ButtonBase? _bell;

    // Motion follows Emil Kowalski's rules: short, ease-out, never in the way of input. Every
    // animation stops on completion (FillBehavior.Stop) after the resting value was already set,
    // so layout, DPI and focus checks always read the settled state.
    private static readonly IEasingFunction EaseOut = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction Swing = Frozen(new SineEase { EasingMode = EasingMode.EaseInOut });

    public NotificationsPopover()
    {
        InitializeComponent();
        AttachButtonPressMotion(AllPill);
        AttachButtonPressMotion(UnreadPill);
        AttachButtonPressMotion(MarkAllButton);
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is not true) return;
            HookBell();
            PlayOpenAnimation();
        };
        // A language switch or the first measure changes the pill widths; the indicator follows.
        // A running slide already ends at the measured target, so a resize mid-slide waits.
        FilterPillsPanel.SizeChanged += (_, _) => { if (!IndicatorTranslate.HasAnimatedProperties) UpdateFilterIndicator(animate: false); };
        Loaded += (_, _) => { HookBell(); UpdateFilterIndicator(animate: false); };
    }

    /// <summary>Motion gate; null follows Windows' "Show animations" setting.</summary>
    public Func<bool>? AllowMotion { get; set; }

    /// <summary>Raised on Escape so the host can close the popup and return focus to the bell.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Raised by Install &amp; restart. The host closes the popup, then restarts through ExitGuard.</summary>
    public event EventHandler? InstallRequested;

    /// <summary>Test seam: receives the launch request instead of starting a browser.</summary>
    public Action<ProcessStartInfo>? LinkLauncher { get; set; }

    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    public void Bind(INotificationService service)
    {
        _service = service;
        Render(service.Snapshot);
        HookBell();
        // The popup's PlacementTarget binding can resolve after the host constructor runs.
        if (_bell is null) Dispatcher.BeginInvoke(HookBell, DispatcherPriority.Loaded);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    // Focus lands on a filter pill, never on Mark all read: Space or Enter on open must not clear the list.
    public void FocusFirst() => Keyboard.Focus(AllPill);

    public void Render(NotificationSnapshot snapshot)
    {
        RenderList(snapshot);
        UpdateFilterIndicator(animate: false);
    }

    private void RenderList(NotificationSnapshot snapshot)
    {
        _snapshot = snapshot;
        var loc = LocalizationManager.Current;
        var focused = (Keyboard.FocusedElement as FrameworkElement)?.Tag as string;
        ListHost.Children.Clear();
        MarkAllButton.Visibility = snapshot.UnreadApiCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        AllPill.IsChecked = !_unreadOnly;
        UnreadPill.IsChecked = _unreadOnly;

        if (snapshot.PinnedUpdate is { } update) ListHost.Children.Add(BuildUpdateCard(update));

        // A row that was unread when the popover opened stays in New until it closes, so a click
        // never makes the list jump under the pointer.
        var fresh = snapshot.Items.Where(item => item.IsUnread || item.IsSessionFresh).ToList();
        var earlier = _unreadOnly ? [] : snapshot.Items.Where(item => !item.IsUnread && !item.IsSessionFresh).ToList();
        if (fresh.Count > 0)
        {
            ListHost.Children.Add(BuildHeading(loc["NotificationsSectionNew"]));
            foreach (var item in fresh) ListHost.Children.Add(BuildRow(item));
        }
        if (earlier.Count > 0)
        {
            ListHost.Children.Add(BuildHeading(loc["NotificationsSectionEarlier"]));
            foreach (var item in earlier) ListHost.Children.Add(BuildRow(item));
        }
        if (snapshot.BadgeCount == 0) ListHost.Children.Add(BuildCaughtUp(loc["NotificationsCaughtUp"]));

        if (focused is not null)
        {
            UpdateLayout();
            foreach (var element in Descendants(ListHost).OfType<FrameworkElement>())
                if (element.Tag as string == focused && element.Focusable) { Keyboard.Focus(element); break; }
        }
    }

    private void MarkAll_Click(object sender, RoutedEventArgs e) => _service?.MarkAllRead();

    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        _unreadOnly = ReferenceEquals(sender, UnreadPill);
        // RenderList, not Render: Render would snap the indicator to the new pill first and
        // leave nothing to slide from.
        RenderList(_snapshot);
        UpdateFilterIndicator(animate: true);
        PlayFilterListTransition();
    }

    private void UpdateFilterIndicator(bool animate)
    {
        if (_syncingIndicator) return;
        _syncingIndicator = true;
        try { SyncFilterIndicator(animate); }
        finally { _syncingIndicator = false; }
    }

    private void SyncFilterIndicator(bool animate)
    {
        // Where the indicator is now, before anything moves.
        var fromX = IndicatorTranslate.X;
        var fromWidth = ActiveFilterIndicator.Width > 0 && !double.IsNaN(ActiveFilterIndicator.Width) ? ActiveFilterIndicator.Width : 0;
        // The checked pill turns SemiBold, which changes both pill widths. Measure after that.
        if (FilterPillsPanel.IsVisible) FilterPillsPanel.UpdateLayout();
        var target = _unreadOnly ? UnreadPill : AllPill;
        if (target.ActualWidth <= 0)
        {
            // Not measured yet (closed popup or first layout pass): align once, after layout.
            if (!_indicatorRetryPending)
            {
                _indicatorRetryPending = true;
                Dispatcher.BeginInvoke(() =>
                {
                    _indicatorRetryPending = false;
                    if ((_unreadOnly ? UnreadPill : AllPill).ActualWidth > 0) UpdateFilterIndicator(animate: false);
                }, DispatcherPriority.Loaded);
            }
            return;
        }
        double targetX;
        try { targetX = target.TranslatePoint(new Point(0, 0), FilterPillsPanel).X; }
        catch (InvalidOperationException) { targetX = ReferenceEquals(target, AllPill) ? 0 : AllPill.ActualWidth + UnreadPill.Margin.Left; }
        var targetWidth = target.ActualWidth;
        if (fromWidth <= 0) fromWidth = targetWidth;

        Stop(IndicatorTranslate, TranslateTransform.XProperty);
        Stop(ActiveFilterIndicator, WidthProperty);
        Stop(IndicatorScale, ScaleTransform.ScaleXProperty);
        IndicatorTranslate.X = targetX;
        ActiveFilterIndicator.Width = targetWidth;
        IndicatorScale.ScaleX = 1;

        if (!animate || !StatusMotion.Allowed(AllowMotion) || fromWidth <= 0) return;
        if (Math.Abs(fromX - targetX) < 0.5 && Math.Abs(fromWidth - targetWidth) < 0.5) return;
        Animate(IndicatorTranslate, TranslateTransform.XProperty, Ease(fromX, targetX, 220));
        Animate(ActiveFilterIndicator, WidthProperty, Ease(fromWidth, targetWidth, 220));
        var stretch = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(220), FillBehavior = FillBehavior.Stop };
        stretch.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        stretch.KeyFrames.Add(new EasingDoubleKeyFrame(1.05, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90)), EaseOut));
        stretch.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220)), EaseOut));
        Animate(IndicatorScale, ScaleTransform.ScaleXProperty, stretch);
    }

    private void PlayFilterListTransition()
    {
        Stop(ListHost, OpacityProperty);
        Stop(ListTranslate, TranslateTransform.YProperty);
        ListHost.Opacity = 1;
        ListTranslate.Y = 0;
        if (!StatusMotion.Allowed(AllowMotion)) return;
        Animate(ListHost, OpacityProperty, Ease(0.25, 1, 160));
        Animate(ListTranslate, TranslateTransform.YProperty, Ease(5, 0, 160));
    }

    private void PlayOpenAnimation()
    {
        Stop(CardBorder, OpacityProperty);
        Stop(CardScale, ScaleTransform.ScaleXProperty);
        Stop(CardScale, ScaleTransform.ScaleYProperty);
        Stop(CardTranslate, TranslateTransform.YProperty);
        CardBorder.Opacity = 1;
        CardScale.ScaleX = CardScale.ScaleY = 1;
        CardTranslate.Y = 0;
        if (!StatusMotion.Allowed(AllowMotion)) return;
        Animate(CardBorder, OpacityProperty, Ease(0, 1, 200));
        Animate(CardScale, ScaleTransform.ScaleXProperty, Ease(0.95, 1, 200));
        Animate(CardScale, ScaleTransform.ScaleYProperty, Ease(0.95, 1, 200));
        Animate(CardTranslate, TranslateTransform.YProperty, Ease(-6, 0, 200));
        // The host renders the fresh snapshot in Popup.Opened, which can run after this. Stagger
        // the rows once that list is in place, or the animation would land on discarded rows.
        Dispatcher.BeginInvoke(StaggerRows, DispatcherPriority.Loaded);
    }

    private void StaggerRows()
    {
        if (!IsVisible || !StatusMotion.Allowed(AllowMotion)) return;
        var index = 0;
        foreach (UIElement child in ListHost.Children)
        {
            if (index == 6) break;
            if (child.Visibility != Visibility.Visible) continue;
            // A delayed BeginTime would show the row at full opacity until its turn and then
            // blink to 0. Holding 0 in a key frame from time zero keeps the stagger clean.
            var delay = TimeSpan.FromMilliseconds(index * 28);
            Animate(child, OpacityProperty, Held(0, 1, delay, 170));
            var shift = child.RenderTransform as TranslateTransform is { IsFrozen: false } existing ? existing : new TranslateTransform();
            child.RenderTransform = shift;
            Animate(shift, TranslateTransform.YProperty, Held(6, 0, delay, 170));
            index++;
        }
    }

    private void AttachButtonPressMotion(ButtonBase button)
    {
        button.RenderTransformOrigin = new Point(0.5, 0.5);
        // Click, not mouse down: it also covers Space and Enter, and never delays the click itself.
        button.Click += (_, _) => Press(button, 0.96, 140);
    }

    private void Press(UIElement element, double from, int milliseconds)
    {
        var scale = EnsureScale(element);
        Stop(scale, ScaleTransform.ScaleXProperty);
        Stop(scale, ScaleTransform.ScaleYProperty);
        scale.ScaleX = scale.ScaleY = 1;
        if (!StatusMotion.Allowed(AllowMotion)) return;
        Animate(scale, ScaleTransform.ScaleXProperty, Ease(from, 1, milliseconds));
        Animate(scale, ScaleTransform.ScaleYProperty, Ease(from, 1, milliseconds));
    }

    private void HookBell()
    {
        if (_bell is not null || (Parent as Popup)?.PlacementTarget is not ButtonBase bell) return;
        _bell = bell;
        bell.RenderTransformOrigin = new Point(0.5, 0.5);
        bell.Click += Bell_Click;
    }

    private void Bell_Click(object sender, RoutedEventArgs e)
    {
        if (_bell is null) return;
        Press(_bell, 0.92, 160);
        if (_bell is not ContentControl { Content: TextBlock glyph }) return;
        glyph.RenderTransformOrigin = new Point(0.5, 0.15);
        var rotate = glyph.RenderTransform as RotateTransform is { IsFrozen: false } existing ? existing : new RotateTransform();
        glyph.RenderTransform = rotate;
        Stop(rotate, RotateTransform.AngleProperty);
        rotate.Angle = 0;
        if (!StatusMotion.Allowed(AllowMotion)) return;
        var ring = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(260), FillBehavior = FillBehavior.Stop };
        foreach (var (angle, at) in new[] { (0.0, 0), (-14.0, 65), (10.0, 130), (-4.0, 195), (0.0, 260) })
            ring.KeyFrames.Add(new EasingDoubleKeyFrame(angle, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(at)), Swing));
        Animate(rotate, RotateTransform.AngleProperty, ring);
    }

    private static ScaleTransform EnsureScale(UIElement element)
    {
        switch (element.RenderTransform)
        {
            case ScaleTransform { IsFrozen: false } scale: return scale;
            case TransformGroup { IsFrozen: false } group when group.Children.OfType<ScaleTransform>().FirstOrDefault(t => !t.IsFrozen) is { } found: return found;
        }
        var created = new ScaleTransform(1, 1);
        var current = element.RenderTransform;
        // Keep any transform someone else set; only the shared, frozen Identity is replaced.
        element.RenderTransform = current is null || ReferenceEquals(current, Transform.Identity)
            ? created
            : new TransformGroup { Children = { current, created } };
        return created;
    }

    // FillBehavior.Stop only stops the clock; WPF keeps it attached to the property, so
    // HasAnimatedProperties would stay true forever. Each run detaches its own clock when it
    // completes, unless a newer run on the same property already replaced it.
    private readonly Dictionary<(DependencyObject Target, DependencyProperty Property), AnimationTimeline> _running = new();

    private void Animate(IAnimatable target, DependencyProperty property, AnimationTimeline animation)
    {
        var key = ((DependencyObject)target, property);
        _running[key] = animation;
        animation.Completed += (_, _) =>
        {
            if (!_running.TryGetValue(key, out var current) || !ReferenceEquals(current, animation)) return;
            _running.Remove(key);
            target.BeginAnimation(property, null);
        };
        target.BeginAnimation(property, animation);
    }

    private void Stop(IAnimatable target, DependencyProperty property)
    {
        _running.Remove(((DependencyObject)target, property));
        target.BeginAnimation(property, null);
    }

    private static DoubleAnimation Ease(double from, double to, int milliseconds) =>
        new(from, to, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop };

    private static DoubleAnimationUsingKeyFrames Held(double from, double to, TimeSpan delay, int milliseconds)
    {
        var animation = new DoubleAnimationUsingKeyFrames { Duration = delay + TimeSpan.FromMilliseconds(milliseconds), FillBehavior = FillBehavior.Stop };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(delay)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(delay + TimeSpan.FromMilliseconds(milliseconds)), EaseOut));
        return animation;
    }

    private static IEasingFunction Frozen(EasingFunctionBase easing) { easing.Freeze(); return easing; }

    private static TextBlock BuildHeading(string text) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(12, 10, 12, 2),
        Foreground = (Brush)Application.Current.FindResource("NativeSecondary"),
    };

    private static UIElement BuildCaughtUp(string text) => new TextBlock
    {
        Text = text,
        FontSize = 12,
        Margin = new Thickness(12, 14, 12, 14),
        HorizontalAlignment = HAlign.Center,
        Foreground = (Brush)Application.Current.FindResource("NativeSecondary"),
    };

    private UIElement BuildRow(NotificationDisplayItem item)
    {
        var loc = LocalizationManager.Current;
        var secondary = (Brush)FindResource("NativeSecondary");
        var kind = KindLabel(item.Kind);
        var age = Age(item.PublishedAt);

        var dot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Margin = new Thickness(0, 5, 10, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Fill = (Brush)FindResource("AccentButtonBackground"),
            Visibility = item.IsUnread ? Visibility.Visible : Visibility.Hidden,
        };
        var meta = new TextBlock
        {
            Text = kind + " · " + age,
            FontSize = 11,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = item.Kind == NotificationKind.Security ? (Brush)FindResource("NativeDanger") : secondary,
        };
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = item.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = item.Body, FontSize = 12, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap, Foreground = secondary });
        text.Children.Add(meta);
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition());
        content.Children.Add(dot);
        Grid.SetColumn(text, 1);
        content.Children.Add(text);

        var row = new Button
        {
            Style = (Style)FindResource("NotificationRowButton"),
            Content = content,
            Tag = "row:" + item.Id,
        };
        AutomationProperties.SetName(row, item.Title + ", " + kind + ", " + age);
        AutomationProperties.SetHelpText(row, item.IsUnread ? loc["NotificationsMarkRead"] : "");
        AttachButtonPressMotion(row);
        row.Click += (_, _) => _service?.MarkRead(item.Id);

        var panel = new StackPanel();
        panel.Children.Add(row);
        if (item.ActionUrl is { } url && NotificationUrlPolicy.IsAllowed(url))
        {
            var link = new Button
            {
                Style = (Style)FindResource("NotificationTextButton"),
                Content = item.ActionLabel ?? loc["NotificationsOpenLink"],
                Margin = new Thickness(22, -6, 12, 6),
                Tag = "link:" + item.Id,
            };
            AttachButtonPressMotion(link);
            // The link is checked again at click time, then the item counts as read.
            link.Click += (_, _) => { NotificationUrlPolicy.TryOpen(url, LinkLauncher); _service?.MarkRead(item.Id); };
            panel.Children.Add(link);
        }
        return new Border
        {
            Child = panel,
            BorderBrush = (Brush)FindResource("NativeStroke"),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

    private UIElement BuildUpdateCard(PinnedUpdateItem update)
    {
        var loc = LocalizationManager.Current;
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = update.Phase == UpdatePhase.Downloading ? loc["NotificationsUpdateDownloading"] : loc["NotificationsUpdateReady"],
            FontWeight = FontWeights.SemiBold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = update.StatusMessage,
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("NativeSecondary"),
        });
        if (update.Phase == UpdatePhase.Downloading && update.Progress is { } progress)
            panel.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = Math.Clamp(progress, 0, 100), Height = 4, Margin = new Thickness(0, 8, 0, 0) });
        if (update.CanInstallAndRestart)
        {
            var install = new Button
            {
                Style = (Style)FindResource("PrimaryAction"),
                Content = loc["NotificationsInstallRestart"],
                HorizontalAlignment = HAlign.Left,
                MinHeight = 32,
                Margin = new Thickness(0, 10, 0, 0),
                Tag = "install",
            };
            AttachButtonPressMotion(install);
            install.Click += (_, _) => InstallRequested?.Invoke(this, EventArgs.Empty);
            panel.Children.Add(install);
        }
        return new Border
        {
            Child = panel,
            Margin = new Thickness(12, 10, 12, 4),
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(6),
            Background = (Brush)FindResource("NativeIconSurface"),
            BorderBrush = (Brush)FindResource("NativeStroke"),
            BorderThickness = new Thickness(1),
        };
    }

    private static string KindLabel(NotificationKind kind) => LocalizationManager.Current[kind switch
    {
        NotificationKind.Maintenance => "NotificationsKindMaintenance",
        NotificationKind.Release => "NotificationsKindRelease",
        NotificationKind.Security => "NotificationsKindSecurity",
        _ => "NotificationsKindAnnouncement",
    }];

    private string Age(DateTimeOffset published)
    {
        var loc = LocalizationManager.Current;
        var span = Clock() - published;
        if (span < TimeSpan.FromMinutes(1)) return loc["NotificationsJustNow"];
        if (span < TimeSpan.FromHours(1)) return string.Format(loc["NotificationsMinutesAgo"], (int)span.TotalMinutes);
        if (span < TimeSpan.FromDays(1)) return string.Format(loc["NotificationsHoursAgo"], (int)span.TotalHours);
        if (span < TimeSpan.FromDays(30)) return string.Format(loc["NotificationsDaysAgo"], (int)span.TotalDays);
        return published.ToLocalTime().ToString("d", CultureInfo.CurrentCulture);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}

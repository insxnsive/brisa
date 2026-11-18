using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Brisa.Localization;
using Brisa.Models;
using Brisa.Security;
using Brisa.Services;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using HAlign = System.Windows.HorizontalAlignment;
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

    public NotificationsPopover() => InitializeComponent();

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
        Render(_snapshot);
    }

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

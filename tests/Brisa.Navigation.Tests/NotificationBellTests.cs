using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Brisa;
using Brisa.Controls;
using Brisa.Models;
using Brisa.Services;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

/// <summary>
/// The top-left bell on Home: where it sits next to Back, what the badge says, how the popup opens
/// and closes, and what it does with feed text. Runs inside the Navigation harness, so the window,
/// the popup and the visual tree are real; only the service behind them is a fake.
/// </summary>
internal static class NotificationBellTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    public static void Run(SettingsStore store, Action<bool, string> check)
    {
        var loc = Brisa.Localization.LocalizationManager.Current;
        var savedLanguage = loc.Preference;
        loc.Apply(Brisa.Localization.LocalizationManager.English);
        try
        {
            WithoutService(store, check);
            WithService(store, check);
            RestartFromThePopover(store, check);
            TextAndLinks(store, check);
            Sections(store, check);
            Motion(store, check, allowMotion: true);
            Motion(store, check, allowMotion: false);
        }
        finally { loc.Apply(savedLanguage); }
    }

    private static UiTestBackendClient SignedIn() => new(new(false, false, true, true, "fixture", null));

    private static void WithoutService(SettingsStore store, Action<bool, string> check)
    {
        var main = new MainWindow(SignedIn(), store);
        main.Show(); main.InitializeAsync().GetAwaiter().GetResult();
        var host = (FrameworkElement)main.FindName("NotificationsHost");
        var popup = (Popup)main.FindName("NotificationsPopup");
        check(host.Visibility == Visibility.Collapsed && !host.IsVisible, "no notification service means no bell");
        Click((Button)main.FindName("NotificationsButton"));
        check(!popup.IsOpen, "clicking a bell that is not there does nothing and does not crash");
        var page = (ContentControl)main.FindName("PageHost");
        Click((Button)main.FindName("SettingsButton")); Pump(() => page.Content is UserControl);
        Click((Button)main.FindName("BackButton")); Pump(() => page.Content is null);
        check(host.Visibility == Visibility.Collapsed, "Home without a service still has no bell after a round trip");
        main.RequestExit(); Pump(() => !main.IsVisible);
    }

    private static void WithService(SettingsStore store, Action<bool, string> check)
    {
        var fake = new FakeNotifications();
        var main = new MainWindow(SignedIn(), store, notifications: fake);
        main.Show(); main.InitializeAsync().GetAwaiter().GetResult();
        var host = (FrameworkElement)main.FindName("NotificationsHost");
        var bell = (Button)main.FindName("NotificationsButton");
        var back = (Button)main.FindName("BackButton");
        var badge = (FrameworkElement)main.FindName("NotificationsBadge");
        var badgeText = (TextBlock)main.FindName("NotificationsBadgeText");
        var popup = (Popup)main.FindName("NotificationsPopup");
        var panel = (NotificationsPopover)main.FindName("NotificationsPanel");
        var page = (ContentControl)main.FindName("PageHost");

        check(host.IsVisible && !back.IsVisible, "Home shows the bell and hides Back in the same slot");
        check(Grid.GetColumn(host) == 0 && Grid.GetColumn(back) == 0, "the bell shares column 0 with Back");
        check(((TextBlock)bell.Content).Text == "\uEA8F" && ((TextBlock)bell.Content).FontFamily.Source == "Segoe Fluent Icons", "the bell is the Segoe Fluent Icons ringer");
        check(bell.Width == 36 && bell.Height == 36, "the bell has the toolbar size");
        check(!badge.IsVisible, "no badge while nothing is unread");

        fake.Set(Snapshot(badge: 3, unread: 3));
        check(badge.IsVisible && badgeText.Text == "3", "the badge shows the unread count");
        check(System.Windows.Automation.AutomationProperties.GetHelpText(bell) == "3 unread", "the bell announces the count");
        fake.Set(Snapshot(badge: 9, unread: 9));
        check(badgeText.Text == "9", "nine shows as 9");
        fake.Set(Snapshot(badge: 12, unread: 12));
        check(badgeText.Text == "9+", "ten or more shows as 9+");
        fake.Set(Snapshot(badge: 0, unread: 0));
        check(!badge.IsVisible && System.Windows.Automation.AutomationProperties.GetHelpText(bell) == "", "the badge clears at zero");

        foreach (var name in new[] { "Settings", "Account", "Settings" })
        {
            Click((Button)main.FindName(name + "Button")); Pump(() => page.Content is UserControl);
            check(!host.IsVisible && back.IsVisible, "on " + name + " the bell gives the slot to Back");
            Click(back); Pump(() => page.Content is null);
            check(host.IsVisible && !back.IsVisible, "back on Home the bell returns and Back leaves");
        }

        // Open, then close with the bell again.
        Click(bell); Pump(() => popup.IsOpen && fake.Begin == 1);
        check(panel.IsVisible, "the popover is shown in the popup");
        Click(bell); Pump(() => !popup.IsOpen && fake.End == 1);
        check(fake.Begin == fake.End, "every open is paired with a close");

        // Escape closes it from inside.
        Click(bell); Pump(() => popup.IsOpen);
        panel.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(panel), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        Pump(() => !popup.IsOpen);
        check(!popup.IsOpen && fake.Begin == fake.End, "Escape closes the popup and ends the session");

        // Opening a page closes it.
        Click(bell); Pump(() => popup.IsOpen);
        Click((Button)main.FindName("SettingsButton")); Pump(() => page.Content is UserControl);
        check(!popup.IsOpen, "navigating closes the popup");
        Click(back); Pump(() => page.Content is null);
        var bellWhileOnPage = bell.IsEnabled;
        Click(bell); Pump(() => popup.IsOpen); Click(bell); Pump(() => !popup.IsOpen);
        check(bellWhileOnPage, "the bell stays usable after returning Home");

        // Starting Connect closes it, and the mutation still runs.
        var backend = (UiTestBackendClient)typeof(MainWindow).GetField("_backend", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(main)!;
        var primary = (Button)main.FindName("PrimaryButton");
        Click(bell); Pump(() => popup.IsOpen);
        check(primary.IsEnabled, "Connect is available");
        Click(primary);
        check(!popup.IsOpen, "starting Connect closes the popup before the work begins");
        Pump(() => backend.Commands.Contains("connect"));
        Spin(300);

        // Closing the window disposes the service.
        var disposedBefore = fake.Disposed;
        main.RequestExit(); Pump(() => !main.IsVisible);
        check(fake.Disposed > disposedBefore, "closing the window disposes the notification service");
    }

    private static void RestartFromThePopover(SettingsStore store, Action<bool, string> check)
    {
        var fake = new FakeNotifications();
        var updates = new PendingUpdates();
        var main = new MainWindow(SignedIn(), store, updates: updates, notifications: fake);
        main.Show(); main.InitializeAsync().GetAwaiter().GetResult();
        var popup = (Popup)main.FindName("NotificationsPopup");
        var panel = (NotificationsPopover)main.FindName("NotificationsPanel");
        var badgeText = (TextBlock)main.FindName("NotificationsBadgeText");
        fake.Set(new NotificationSnapshot(0, 1, new PinnedUpdateItem(UpdatePhase.Ready, "1.0.4", null, "Brisa 1.0.4 will install when you exit.", true), []));
        check(badgeText.Text == "1", "a ready update counts once on the badge");
        Click((Button)main.FindName("NotificationsButton")); Pump(() => popup.IsOpen);
        var install = Flatten(panel).OfType<Button>().Single(b => b.Tag as string == "install");
        check((string)install.Content == "Install & restart", "the update card offers Install & restart");
        check(Flatten(panel).OfType<TextBlock>().Any(t => t.Text == "Update ready") && Flatten(panel).OfType<TextBlock>().Any(t => t.Text.Contains("1.0.4")), "the update card names the version");
        Click(install);
        check(!popup.IsOpen, "Install & restart closes the popup first");
        Pump(() => !main.IsVisible);
        check(updates.PrepareApplyCalls == 1 && updates.LastRestart, "it restarts through the normal exit path and applies the update");

        var downloading = new MainWindow(SignedIn(), store, notifications: fake);
        downloading.Show(); downloading.InitializeAsync().GetAwaiter().GetResult();
        var downloadingPanel = (NotificationsPopover)downloading.FindName("NotificationsPanel");
        fake.Set(new NotificationSnapshot(0, 0, new PinnedUpdateItem(UpdatePhase.Downloading, "1.0.4", 40, "Downloading 40%", false), []));
        OpenPopup(downloading);
        check(!Flatten(downloadingPanel).OfType<Button>().Any(b => b.Tag as string == "install"), "no install button until the update is ready");
        check(Flatten(downloadingPanel).OfType<ProgressBar>().Any(p => p.Value == 40), "download progress is shown");
        downloading.RequestExit(); Pump(() => !downloading.IsVisible);
    }

    private static void TextAndLinks(SettingsStore store, Action<bool, string> check)
    {
        var fake = new FakeNotifications();
        var main = new MainWindow(SignedIn(), store, notifications: fake);
        main.Show(); main.InitializeAsync().GetAwaiter().GetResult();
        var panel = (NotificationsPopover)main.FindName("NotificationsPanel");
        var launched = new List<ProcessStartInfo>();
        panel.LinkLauncher = launched.Add;
        OpenPopup(main);

        const string Markup = "<b>Bold</b> <script>alert(1)</script>";
        const string Script = "javascript:alert(1) <a href=\"http://evil.example\">click</a>";
        var items = new[]
        {
            Item("markup-item", Markup, Script, NotificationKind.Security, unread: true),
            Item("good-link-1", "Release notes", "Read what changed.", NotificationKind.Release, unread: true, url: "https://github.com/insxnsive/brisa/releases", label: "Release notes"),
            Item("bad-link-1", "Unsafe", "Never linked.", NotificationKind.Announcement, unread: true, url: "http://evil.example/x", label: "Open"),
            Item("file-link-1", "File", "Never linked.", NotificationKind.Announcement, unread: true, url: "file:///C:/Windows/win.ini", label: "Open"),
        };
        fake.Set(new NotificationSnapshot(4, 4, null, items));
        panel.UpdateLayout();
        var texts = Flatten(panel).OfType<TextBlock>().Select(t => t.Text).ToList();
        check(texts.Contains(Markup) && texts.Contains(Script), "feed text appears exactly as written, as plain text");
        check(!Flatten(panel).Any(e => e.GetType().Name.Contains("WebView") || e.GetType().Name.Contains("WebBrowser") || e is RichTextBox), "no browser or rich text control renders feed content");
        var tags = Flatten(panel).OfType<FrameworkElement>().Select(e => e.Tag as string).Where(t => t is not null).ToList();
        check(tags.Contains("link:good-link-1") && !tags.Contains("link:bad-link-1") && !tags.Contains("link:file-link-1"), "only a link that passes the URL policy gets a button");

        var link = Flatten(panel).OfType<Button>().Single(b => b.Tag as string == "link:good-link-1");
        check((string)link.Content == "Release notes", "the link uses the item's own label");
        Click(link);
        check(launched.Count == 1 && launched[0].FileName == "https://github.com/insxnsive/brisa/releases" && launched[0].UseShellExecute, "the link opens the checked address");
        check(fake.Read.SequenceEqual(["good-link-1"]), "opening a link marks the item read");

        var row = Flatten(panel).OfType<Button>().Single(b => b.Tag as string == "row:markup-item");
        Click(row);
        check(fake.Read.SequenceEqual(["good-link-1", "markup-item"]), "clicking a row marks only that row read");
        var markAll = (Button)panel.FindName("MarkAllButton");
        check(markAll.IsVisible, "Mark all read shows while something is unread");
        Click(markAll);
        check(fake.ReadAll == 1, "Mark all read asks the service once");
        fake.Set(new NotificationSnapshot(0, 0, null, items.Select(i => i with { IsUnread = false }).ToList()));
        check(!markAll.IsVisible, "Mark all read hides when nothing is unread");
        main.RequestExit(); Pump(() => !main.IsVisible);
    }

    private static void Sections(SettingsStore store, Action<bool, string> check)
    {
        var fake = new FakeNotifications();
        var main = new MainWindow(SignedIn(), store, notifications: fake);
        main.Show(); main.InitializeAsync().GetAwaiter().GetResult();
        var panel = (NotificationsPopover)main.FindName("NotificationsPanel");
        var loc = Brisa.Localization.LocalizationManager.Current;
        var items = new[]
        {
            Item("fresh-unread", "Fresh unread", "Body.", NotificationKind.Release, unread: true),
            Item("fresh-read-1", "Read this session", "Body.", NotificationKind.Maintenance, unread: false, fresh: true),
            Item("earlier-one", "Earlier one", "Body.", NotificationKind.Announcement, unread: false),
        };
        fake.Set(new NotificationSnapshot(1, 1, null, items));
        OpenPopup(main);
        List<string> Texts() { panel.UpdateLayout(); return Flatten(panel).OfType<TextBlock>().Select(t => t.Text).ToList(); }
        check(Texts().Contains("New") && Texts().Contains("Earlier"), "New and Earlier sections");
        var order = Flatten(panel).OfType<TextBlock>().Select(t => t.Text).ToList();
        check(order.IndexOf("New") < order.IndexOf("Fresh unread") && order.IndexOf("Read this session") < order.IndexOf("Earlier") && order.IndexOf("Earlier") < order.IndexOf("Earlier one"),
            "a row read this session stays under New, and only older read rows sit under Earlier");
        check(!Texts().Contains("All caught up"), "no caught-up line while something is unread");

        ((ToggleButton)panel.FindName("UnreadPill")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        check(!Texts().Contains("Earlier") && !Texts().Contains("Earlier one") && Texts().Contains("Read this session"), "Unread hides older rows but keeps this session's rows in place");
        ((ToggleButton)panel.FindName("AllPill")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        check(Texts().Contains("Earlier one"), "All shows everything again");

        fake.Set(new NotificationSnapshot(0, 0, null, items.Select(i => i with { IsUnread = false, IsSessionFresh = false }).ToList()));
        check(Texts().Contains("All caught up"), "the caught-up line shows when nothing is unread");
        fake.Set(NotificationSnapshot.Empty);
        check(Texts().Contains("All caught up") && !Texts().Contains("New"), "an empty feed is also caught up");

        fake.Set(new NotificationSnapshot(1, 1, null, items));
        var bell = (DependencyObject)main.FindName("NotificationsButton");
        check(System.Windows.Automation.AutomationProperties.GetHelpText(bell) == "1 unread", "the bell announces the count in English");
        loc.Apply(Brisa.Localization.LocalizationManager.PortugueseBrazil);
        check(System.Windows.Automation.AutomationProperties.GetHelpText(bell) == "1 n\u00E3o lidas", "the bell's spoken count follows a live language switch without a new snapshot");
        fake.Set(new NotificationSnapshot(1, 1, null, items));
        check(Texts().Contains("Notifica\u00E7\u00F5es") && Texts().Contains("Novas") && Texts().Contains("Anteriores"), "the popover follows the language");
        check(((ContentControl)panel.FindName("AllPill")).Content as string == "Todas" && ((ContentControl)panel.FindName("MarkAllButton")).Content as string == "Marcar todas como lidas", "filter and action labels follow the language");
        check(System.Windows.Automation.AutomationProperties.GetName((DependencyObject)main.FindName("NotificationsButton")) == "Notifica\u00E7\u00F5es", "the bell's accessible name follows the language");
        check(Texts().Any(t => t.StartsWith("Vers\u00E3o \u00B7 ")), "the kind and age are localized");
        loc.Apply(Brisa.Localization.LocalizationManager.English);
        main.RequestExit(); Pump(() => !main.IsVisible);
    }

    // The sliding filter pill, the press feedback and the bell ring. Every check reads settled
    // values, and the same flow runs with motion on and off so neither path can break the other.
    private static void Motion(SettingsStore store, Action<bool, string> check, bool allowMotion)
    {
        var label = allowMotion ? "with motion" : "without motion";
        var fake = new FakeNotifications();
        var main = new MainWindow(SignedIn(), store, notifications: fake);
        main.Show(); main.InitializeAsync().GetAwaiter().GetResult();
        var panel = (NotificationsPopover)main.FindName("NotificationsPanel");
        panel.AllowMotion = () => allowMotion;
        var popup = (Popup)main.FindName("NotificationsPopup");
        var bell = (Button)main.FindName("NotificationsButton");
        var items = new[]
        {
            Item("motion-unread", "Motion unread", "Body.", NotificationKind.Release, unread: true),
            Item("motion-older", "Motion older", "Body.", NotificationKind.Announcement, unread: false),
        };
        fake.Set(new NotificationSnapshot(1, 1, null, items));
        OpenPopup(main);
        Spin(320);
        var indicator = (Border)panel.FindName("ActiveFilterIndicator");
        var slide = (TranslateTransform)panel.FindName("IndicatorTranslate");
        var pills = (UIElement)panel.FindName("FilterPillsPanel");
        var all = (ToggleButton)panel.FindName("AllPill");
        var unread = (ToggleButton)panel.FindName("UnreadPill");
        List<string> Texts() { panel.UpdateLayout(); return Flatten(panel).OfType<TextBlock>().Select(t => t.Text).ToList(); }

        check(all.ActualWidth > 0 && Math.Abs(slide.X) < 0.5 && Math.Abs(indicator.Width - all.ActualWidth) < 0.5, label + ": the filter indicator starts under All");
        unread.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        check(slide.HasAnimatedProperties == allowMotion, label + ": the indicator slides only when motion is allowed");
        Spin(320);
        var unreadX = unread.TranslatePoint(new Point(0, 0), pills).X;
        check(slide.X > 0 && Math.Abs(slide.X - unreadX) < 0.5 && Math.Abs(indicator.Width - unread.ActualWidth) < 0.5, label + ": the indicator rests under Unread, got x " + slide.X + " width " + indicator.Width);
        check(!Texts().Contains("Motion older") && Texts().Contains("Motion unread"), label + ": Unread still filters the list");
        check(!slide.HasAnimatedProperties && !indicator.HasAnimatedProperties, label + ": the slide stops once it lands");
        all.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Spin(320);
        check(Math.Abs(slide.X) < 0.5 && Math.Abs(indicator.Width - all.ActualWidth) < 0.5 && Texts().Contains("Motion older"), label + ": All brings the indicator and the older rows back");

        panel.UpdateLayout();
        check(Math.Abs(panel.ActualWidth - 340) < 0.5 && panel.ActualHeight <= 390.5, label + ": the popover keeps 340 by at most 390, got " + panel.ActualWidth + "x" + panel.ActualHeight);
        var markAll = (Button)panel.FindName("MarkAllButton");
        Click(markAll);
        check(fake.ReadAll == 1, label + ": Mark all read still reaches the service once");
        check(markAll.RenderTransform is ScaleTransform && (((ScaleTransform)markAll.RenderTransform).HasAnimatedProperties == allowMotion), label + ": Mark all read presses only when motion is allowed");
        Spin(200);
        check(markAll.RenderTransform is ScaleTransform { ScaleX: 1, ScaleY: 1, HasAnimatedProperties: false }, label + ": the press settles at full size");

        panel.FocusFirst();
        var focusDeadline = DateTime.UtcNow.AddSeconds(2);
        while (!all.IsKeyboardFocused && DateTime.UtcNow < focusDeadline) Spin(30);
        check(all.IsKeyboardFocused, label + ": FocusFirst still lands on All");

        Click(bell);
        Pump(() => !popup.IsOpen);
        var glyph = (TextBlock)bell.Content;
        check(glyph.RenderTransform is RotateTransform ring && ring.HasAnimatedProperties == allowMotion, label + ": the bell rings only when motion is allowed");
        check(bell.RenderTransform is ScaleTransform press && press.HasAnimatedProperties == allowMotion, label + ": the bell presses only when motion is allowed");
        Spin(320);
        check(glyph.RenderTransform is RotateTransform { Angle: 0, HasAnimatedProperties: false } && bell.RenderTransform is ScaleTransform { ScaleX: 1, HasAnimatedProperties: false }, label + ": the bell settles upright at full size");
        check(bell.ActualWidth == 36 && bell.ActualHeight == 36, label + ": the bell keeps its toolbar size");

        // A second open must not stack a second bell handler: one click, one press.
        OpenPopup(main);
        Spin(320);
        check(popup.IsOpen && Math.Abs(slide.X) < 0.5, label + ": reopening keeps the indicator under the active filter");
        panel.Bind(fake);
        Click(bell);
        Pump(() => !popup.IsOpen);
        check(!popup.IsOpen, label + ": binding twice still lets one bell click close the popup");
        main.RequestExit(); Pump(() => !main.IsVisible);
    }

    // A closed popup has no visual tree yet, so layout checks open it first.
    private static void OpenPopup(MainWindow window)
    {
        var popup = (Popup)window.FindName("NotificationsPopup");
        Click((Button)window.FindName("NotificationsButton"));
        Pump(() => popup.IsOpen && ((FrameworkElement)window.FindName("NotificationsPanel")).IsVisible);
    }

    private static NotificationSnapshot Snapshot(int badge, int unread) => new(unread, badge, null, []);

    private static NotificationDisplayItem Item(string id, string title, string body, NotificationKind kind, bool unread, bool fresh = false, string? url = null, string? label = null) =>
        new(id, kind, title, body, Now.AddHours(-2), url is null ? null : new Uri(url), label, unread, fresh);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static void Spin(int milliseconds)
    {
        var end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < end)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static void Pump(Func<bool> completed)
    {
        var end = DateTime.UtcNow.AddSeconds(8);
        while (!completed())
        {
            if (DateTime.UtcNow >= end) throw new TimeoutException("The notification check did not finish");
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static IEnumerable<DependencyObject> Flatten(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Flatten(child)) yield return nested;
        }
    }

    private sealed class FakeNotifications : INotificationService
    {
        public NotificationSnapshot Snapshot { get; private set; } = NotificationSnapshot.Empty;
        public event EventHandler<NotificationSnapshot>? SnapshotChanged;
        public int Begin { get; private set; }
        public int End { get; private set; }
        public int ReadAll { get; private set; }
        public int Disposed { get; private set; }
        public List<string> Read { get; } = [];

        public void Set(NotificationSnapshot snapshot) { Snapshot = snapshot; SnapshotChanged?.Invoke(this, snapshot); }
        public void Start() { }
        public Task RefreshNowAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void BeginPopoverSession() => Begin++;
        public void EndPopoverSession() => End++;
        public void MarkRead(string id) => Read.Add(id);
        public void MarkAllRead() => ReadAll++;
        public void Dispose() => Disposed++;
        public ValueTask DisposeAsync() { Disposed++; return ValueTask.CompletedTask; }
    }

    private sealed class PendingUpdates : IUpdateService
    {
        public UpdateStatus Status { get; } = new(UpdatePhase.Ready, "UpdateReadyVersion", "1.0.4");
        public bool HasPendingUpdate => true;
        public int PrepareApplyCalls { get; private set; }
        public bool LastRestart { get; private set; }
        public event EventHandler<UpdateStatus>? StatusChanged { add { } remove { } }
        public void Start() { }
        public Task CheckNowAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void PrepareApply(bool restart) { PrepareApplyCalls++; LastRestart = restart; }
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

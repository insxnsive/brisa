using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Brisa;
using Brisa.Controls;
using Brisa.Localization;
using Brisa.Models;
using Brisa.Services;

/// <summary>
/// The bell, the badge and the popover in a real window at the smallest size Brisa allows,
/// in both themes and both languages. Pictures are written only when BRISA_APPEARANCE_OUTPUT is set.
/// </summary>
internal static class NotificationAppearanceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    public static void Run(SettingsStore store, Action<bool, string> check, Action<Window, string> capture, Action<int> spin)
    {
        var loc = LocalizationManager.Current;
        var savedLanguage = loc.Preference;
        var savedTheme = store.Current.Theme;
        try
        {
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            foreach (var language in new[] { LocalizationManager.English, LocalizationManager.PortugueseBrazil })
            {
                var label = theme + "-" + language;
                store.Save(new(theme));
                loc.Apply(language);
                var fake = new FakeNotifications(Busy());
                var main = new MainWindow(new UiTestBackendClient(new(false, false, true, true, "fixture", null)), store, notifications: fake);
                main.Width = main.MinWidth;
                main.Height = main.MinHeight;
                main.Show();
                main.InitializeAsync().GetAwaiter().GetResult();
                Settle(main);
                check(main.ActualWidth <= 440.5 && main.ActualHeight <= 520.5 + 40, label + ": the window is at its minimum size");

                var host = (FrameworkElement)main.FindName("NotificationsHost");
                var bell = (FrameworkElement)main.FindName("NotificationsButton");
                var badge = (FrameworkElement)main.FindName("NotificationsBadge");
                var badgeText = (TextBlock)main.FindName("NotificationsBadgeText");
                var account = (FrameworkElement)main.FindName("AccountButton");
                var settings = (FrameworkElement)main.FindName("SettingsButton");
                var popup = (Popup)main.FindName("NotificationsPopup");
                var panel = (NotificationsPopover)main.FindName("NotificationsPanel");

                var bellBox = Box(bell, main);
                var accountBox = Box(account, main);
                check(host.IsVisible && bell.ActualWidth == 36 && bell.ActualHeight == 36, label + ": the bell has the toolbar size");
                check(bellBox.Left >= 24 - 0.5 && bellBox.Top >= 20 - 0.5, label + ": the bell sits at the top-left margin");
                check(!bellBox.IntersectsWith(accountBox) && bellBox.Right < accountBox.Left, label + ": the bell does not touch the account button");
                check(Box(badge, main) is var badgeBox && badgeBox.Left >= bellBox.Left - 0.5 && badgeBox.Right <= bellBox.Right + 0.5 && badgeBox.Top >= bellBox.Top - 0.5 && badgeBox.Bottom <= bellBox.Bottom + 0.5,
                    label + ": the badge stays inside the bell's box");
                check(badgeText.Text == "9+" && badge.ActualWidth >= 16 && badge.ActualHeight == 16, label + ": a busy badge reads 9+ in a 16 px pill");
                check(Contrast(Brush(main, "AccentButtonForeground"), Brush(main, "AccentButtonBackground")) >= 4.5, label + ": badge text meets 4.5 to 1 on the accent");
                check(Brush(main, "AccentButtonBackground") != Brush(main, "NativeSurface"), label + ": the unread dot and badge use the accent");
                check(Contrast(Brush(main, "NativeForeground"), Brush(main, "NativeSurface")) >= 7
                    && Contrast(Brush(main, "NativeSecondary"), Brush(main, "NativeSurface")) >= 4.5
                    && Contrast(Brush(main, "NativeDanger"), Brush(main, "NativeSurface")) >= 4.5, label + ": popover text meets contrast on its surface");
                capture(main, label + "-Notifications-Home");

                main.UpdateLayout();
                ((Button)bell).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!(popup.IsOpen && panel.IsVisible) && DateTime.UtcNow < deadline) spin(30);
                spin(100);
                panel.UpdateLayout();
                check(popup.IsOpen && panel.IsVisible, label + ": the popup opens");
                // 340 wide and at most 390 tall, shadow margin included, so the card is 324 by 374 at most.
                check(Math.Abs(panel.ActualWidth - 340) < 0.5 && panel.ActualHeight <= 390.5 && panel.ActualHeight > 100, label + ": the popover is 340 wide and at most 390 tall, got " + panel.ActualWidth + "x" + panel.ActualHeight);
                var left = panel.PointToScreen(new Point(0, 0)).X;
                var right = left + panel.ActualWidth;
                var windowLeft = main.PointToScreen(new Point(0, 0)).X;
                var windowRight = main.PointToScreen(new Point(main.ActualWidth, 0)).X;
                check(left >= windowLeft - 0.5 && right <= windowRight + 0.5, label + ": the popup stays inside the window width");
                var texts = Descendants(panel).OfType<TextBlock>().ToList();
                check(texts.Count >= 12 && texts.All(t => t.ActualWidth <= panel.ActualWidth - 16 + 0.5), label + ": no text is wider than the card");
                check(Descendants(panel).OfType<ScrollViewer>().Any(v => v.ScrollableHeight > 0), label + ": a long list scrolls instead of growing past 390");
                check(Descendants(panel).OfType<Button>().All(b => b.ActualHeight >= 24 || !b.IsVisible), label + ": every control keeps a usable height");
                check(Descendants(panel).OfType<ToggleButton>().All(t => t.ActualHeight >= 28), label + ": filter pills keep their height");
                popup.IsOpen = false;
                spin(60);
                main.RequestExit();
                while (main.IsVisible) spin(30);

                // The popup lives in its own window, so the picture of the card is taken from a host.
                var card = new NotificationsPopover();
                card.Clock = () => Now;
                card.Bind(new FakeNotifications(Busy()));
                var frame = new Window
                {
                    Width = 440,
                    Height = 520,
                    Content = card,
                    Background = (Brush)Application.Current.FindResource("NativeBackground"),
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = 10,
                    Top = 10,
                };
                frame.Show();
                Settle(frame);
                spin(60);
                capture(frame, label + "-Notifications-Popover");
                check(card.ActualWidth == 340 && card.ActualHeight <= 390.5, label + ": the card keeps its size in a 440 by 520 frame");
                frame.Close();
            }
        }
        finally
        {
            loc.Apply(savedLanguage);
            store.Save(new(savedTheme));
        }
    }

    private static NotificationSnapshot Busy()
    {
        NotificationDisplayItem Make(string id, NotificationKind kind, string title, string body, TimeSpan age, bool unread, bool fresh = false, string? url = null, string? label = null) =>
            new(id, kind, title, body, Now - age, url is null ? null : new Uri(url), label, unread, fresh);
        var longTitle = new string('W', 40) + " " + new string('i', 40) + " " + new string('m', 18);
        var longBody = string.Join(" ", Enumerable.Repeat("A long line that has to wrap inside the card without clipping.", 6));
        var items = new[]
        {
            Make("release-notes-1", NotificationKind.Release, "Brisa 1.0.4 is out", "Faster startup and a clearer route check.", TimeSpan.FromMinutes(5), true, url: "https://github.com/insxnsive/brisa/releases", label: "Release notes"),
            Make("security-note-1", NotificationKind.Security, longTitle, longBody, TimeSpan.FromHours(3), true),
            Make("maintenance-1", NotificationKind.Maintenance, "Route maintenance tonight", "One route will be offline at 04:00 UTC.", TimeSpan.FromDays(1), false, fresh: true),
            Make("announcement-1", NotificationKind.Announcement, "Discord route check improved", longBody, TimeSpan.FromDays(9), false),
            Make("announcement-2", NotificationKind.Announcement, "Older note", "Shown under Earlier.", TimeSpan.FromDays(45), false),
        };
        return new NotificationSnapshot(
            11, 12,
            new PinnedUpdateItem(UpdatePhase.Ready, "1.0.4", null, "Brisa 1.0.4 will install when you exit.", true),
            items);
    }

    private static Rect Box(FrameworkElement element, Visual ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    private static Color Brush(FrameworkElement scope, string key) => ((SolidColorBrush)scope.FindResource(key)).Color;

    private static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            var x = value / 255.0;
            return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    private static double Contrast(Color a, Color b)
    {
        var (high, low) = (Luminance(a), Luminance(b));
        if (high < low) (high, low) = (low, high);
        return (high + 0.05) / (low + 0.05);
    }

    private static void Settle(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
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

    private sealed class FakeNotifications(NotificationSnapshot snapshot) : INotificationService
    {
        public NotificationSnapshot Snapshot { get; } = snapshot;
        public event EventHandler<NotificationSnapshot>? SnapshotChanged { add { } remove { } }
        public void Start() { }
        public Task RefreshNowAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void BeginPopoverSession() { }
        public void EndPopoverSession() { }
        public void MarkRead(string id) { }
        public void MarkAllRead() { }
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

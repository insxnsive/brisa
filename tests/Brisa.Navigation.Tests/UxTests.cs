using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using Brisa;
using Brisa.Models;
using Brisa.Services;

/// <summary>
/// Phase B1 onboarding paths: the .conf shortcut from Account, the Settings draft that survives
/// Advanced without saving, the SDK banner gate, and the static Discord restart note.
/// </summary>
internal static class UxTests
{
    public static void Run(SettingsStore store, Action<bool, string> check)
    {
        var loc = Brisa.Localization.LocalizationManager.Current;
        var savedLanguage = loc.Preference;
        loc.Apply(Brisa.Localization.LocalizationManager.English);
        try
        {
            AccountAndAdvanced(store, check);
            SettingsDraft(store, check);
            SdkBanner(store, check);
            DiscordNote(store, check);
            TrayMenu(store, check);
            TrayLimits(store, check);
            RouteWindowChoices(store, check);
            ScansUseTheSavedCountry(store, check);
            RouteClickRemembers(store, check);
        }
        finally { loc.Apply(savedLanguage); }
    }

    private static void AccountAndAdvanced(SettingsStore store, Action<bool, string> check)
    {
        var backend = new UxBackend(new(false, false, true, false, "", null));
        var main = new MainWindow(backend, store, exitWarning: _ => { });
        main.Show(); main.InitializeAsync().GetAwaiter().GetResult();
        var page = (ContentControl)main.FindName("PageHost");

        Click((Button)main.FindName("AccountButton")); Pump(() => page.Content is AccountView);
        var account = (AccountView)page.Content;
        var link = (Button)account.FindName("UseConfFile");
        check(link.Content as string == "Use a WireGuard .conf file instead", "the account page offers the .conf shortcut");
        check(link.IsEnabled, "the .conf shortcut is enabled while no account action is pending");
        Click(link); Pump(() => page.Content is AdvancedView);
        check(page.Content is AdvancedView, "the .conf shortcut opens Advanced as a page");

        Click((Button)main.FindName("BackButton")); Pump(() => page.Content is AccountView);
        check(page.Content is AccountView, "Back returns to Account while sign-in is still required");

        account = (AccountView)page.Content;
        Click((Button)account.FindName("UseConfFile")); Pump(() => page.Content is AdvancedView);
        var imported = main.ImportConfigAsync(@"C:\picked\route.conf").GetAwaiter().GetResult();
        check(imported.Success, "the fixture import succeeds");
        Click((Button)main.FindName("BackButton")); Pump(() => page.Content is null);
        check(page.Content is null, "Back after a successful import returns Home, not Account");
        main.RequestExit(); Pump(() => !main.IsVisible);

        var signedInBackend = new UxBackend(new(false, false, true, true, "fixture", null));
        var signedInMain = new MainWindow(signedInBackend, store, exitWarning: _ => { });
        signedInMain.Show(); signedInMain.InitializeAsync().GetAwaiter().GetResult();
        var signedInPage = (ContentControl)signedInMain.FindName("PageHost");
        Click((Button)signedInMain.FindName("AccountButton")); Pump(() => signedInPage.Content is AccountView);
        var signedInAccount = (AccountView)signedInPage.Content;
        var hiddenLink = (Button)signedInAccount.FindName("UseConfFile");
        check(hiddenLink.Visibility == Visibility.Collapsed && !hiddenLink.IsEnabled, "a signed-in account hides the .conf shortcut");
        signedInMain.RequestExit(); Pump(() => !signedInMain.IsVisible);
    }

    private static void SettingsDraft(SettingsStore store, Action<bool, string> check)
    {
        store.Save(new(AppTheme.System, false, true, "system", false));
        var backend = new UxBackend(new(false, false, true, true, "fixture", null));
        var main = new MainWindow(backend, store, exitWarning: _ => { });
        main.Show(); main.InitializeAsync().GetAwaiter().GetResult();
        var page = (ContentControl)main.FindName("PageHost");

        Click((Button)main.FindName("SettingsButton")); Pump(() => page.Content is SettingsView);
        var settings = (SettingsView)page.Content;
        ((CheckBox)settings.FindName("DetailsBox")).IsChecked = false;
        Click((Button)settings.FindName("AdvancedButton")); Pump(() => page.Content is AdvancedView);
        check(page.Content is AdvancedView, "Settings opens Advanced as a page");
        check(store.Current.ShowDetails, "opening Advanced does not save the draft");

        Click((Button)main.FindName("BackButton")); Pump(() => page.Content is SettingsView);
        settings = (SettingsView)page.Content;
        check(((CheckBox)settings.FindName("DetailsBox")).IsChecked == false, "Back restores the unsaved draft");
        check(store.Current.ShowDetails, "Back does not save the draft either");

        Click((Button)settings.FindName("CancelButton")); Pump(() => page.Content is null);
        Click((Button)main.FindName("SettingsButton")); Pump(() => page.Content is SettingsView);
        settings = (SettingsView)page.Content;
        check(((CheckBox)settings.FindName("DetailsBox")).IsChecked == true, "Cancel discards the draft");
        main.RequestExit(); Pump(() => !main.IsVisible);
    }

    private static void SdkBanner(SettingsStore store, Action<bool, string> check)
    {
        store.Save(new(AppTheme.System, false, true, "system", false));
        var backend = new UxBackend(new(false, false, true, true, "fixture", null, SdkMissing: true))
        { ConnectResult = new(false, "WIRESOCK_SETUP_REQUIRED", "setup") };
        var main = new MainWindow(backend, store, exitWarning: _ => { }) { CommandFailureNotice = (_, _) => { } };
        main.Show(); main.InitializeAsync().GetAwaiter().GetResult();
        var banner = (TextBlock)main.FindName("SdkBanner");
        check(!banner.IsVisible, "a launch that sees SdkMissing true shows no SDK banner yet");

        Click((Button)main.FindName("PrimaryButton"));
        Pump(() => banner.IsVisible);
        check(backend.Commands.Contains("connect"), "the banner follows the connect attempt");
        check(banner.IsVisible, "a failed automatic SDK install shows the banner");
        static string TextOf(Inline inline) => inline switch
        {
            Run run => run.Text,
            Span span => string.Concat(span.Inlines.OfType<Inline>().Select(TextOf)),
            _ => "",
        };
        var bannerText = string.Concat(banner.Inlines.OfType<Inline>().Select(TextOf));
        check(bannerText.Contains("or install WireSock SDK 3.4.8.1 manually."),
            "the SDK banner has one call to action with a clean space: [" + bannerText + "]");
        check(!bannerText.Contains("Click here"), "the SDK banner carries no duplicate link text");

        backend.State = backend.State with { SdkMissing = false };
        main.InitializeAsync(openAccount: false).GetAwaiter().GetResult();
        check(!banner.IsVisible, "a snapshot that proves the SDK present clears the banner");
        main.RequestExit(); Pump(() => !main.IsVisible);
    }

    private static void DiscordNote(SettingsStore store, Action<bool, string> check)
    {
        var backend = new UxBackend(new(false, false, true, true, "fixture", null));
        var main = new MainWindow(backend, store, exitWarning: _ => { });
        var note = (TextBlock)main.FindName("DiscordRestartNote");
        main.Show();
        check(!note.IsVisible, "the Discord note is hidden while the first snapshot is loading");
        main.InitializeAsync().GetAwaiter().GetResult();
        check(note.IsVisible, "the Discord note shows on a disconnected Home");

        backend.State = backend.State with { ExternalTunnel = true };
        main.InitializeAsync(openAccount: false).GetAwaiter().GetResult();
        check(!note.IsVisible, "the Discord note hides on a blocked Home");

        backend.State = backend.State with { ExternalTunnel = false, Connected = true, TunnelActive = true, OwnedTunnelPresent = true };
        main.InitializeAsync(openAccount: false).GetAwaiter().GetResult();
        check(note.IsVisible, "the Discord note stays visible on a connected Home");
        main.RequestExit(); Pump(() => !main.IsVisible);
    }

    private static NativeSnapshot SignedInIdle() => new(false, false, true, true, "fixture", null);

    private static void TrayMenu(SettingsStore store, Action<bool, string> check)
    {
        var loc = Brisa.Localization.LocalizationManager.Current;
        var backend = new UxBackend(SignedInIdle());
        var main = new MainWindow(backend, store, exitWarning: _ => { });
        main.Show(); main.InitializeAsync().GetAwaiter().GetResult();
        var page = (ContentControl)main.FindName("PageHost");
        var tray = new TrayService(main, store, showNotifyIcon: false);
        var items = tray.MenuItems;
        check(!tray.IconVisible, "no icon appears on the desktop when the test seam is off");
        check(items.Count == 5 && items[3] is System.Windows.Forms.ToolStripSeparator, "status, Open, main action, separator, Exit");
        check(!items[0].Enabled && items[0].Text == "Brisa: " + main.CurrentHomeState.StatusText, "the status line is read only: " + items[0].Text);
        check(items[1].Text == "Open" && items[4].Text == "Exit", "Open and Exit keep their labels");
        check(items[2].Text == "Connect" && items[2].Enabled && items[2].Available, "the main action mirrors Home");
        check(tray.TooltipText == items[0].Text, "the tooltip carries the status line");

        // The menu follows Home: connected shows the server and offers Disconnect.
        backend.State = backend.State with { Connected = true, TunnelActive = true, OwnedTunnelPresent = true, Route = new RouteInfo("SV#43", "SV", 40) };
        main.InitializeAsync(openAccount: false).GetAwaiter().GetResult();
        check(tray.MenuItems[0].Text == "Brisa: Connected (SV#43)" && tray.MenuItems[2].Text == "Disconnect" && tray.MenuItems[2].Enabled, "connected: " + tray.MenuItems[0].Text);
        check(tray.TooltipText == "Brisa: Connected (SV#43)", "the tooltip follows too");
        backend.State = backend.State with { Route = new RouteInfo(new string('S', 100), "SV", 40) };
        main.InitializeAsync(openAccount: false).GetAwaiter().GetResult();
        check(tray.TooltipText.Length <= 63 && tray.TooltipText.EndsWith("...") && tray.MenuItems[0].Text.Contains(new string('S', 100)), "a 100 character server name is cut in the tooltip only: " + tray.TooltipText.Length);
        backend.State = backend.State with { Route = new RouteInfo("  ", "SV", 40) };
        main.InitializeAsync(openAccount: false).GetAwaiter().GetResult();
        check(tray.MenuItems[0].Text == "Brisa: Connected", "a blank server adds no brackets");

        // A page being open, or busy work, switches the action off without hiding it.
        Click((Button)main.FindName("SettingsButton")); Pump(() => page.Content is SettingsView);
        tray.Apply(main.CurrentHomeState);
        check(!main.CanRunPrimaryFromTray && !tray.MenuItems[2].Enabled && tray.MenuItems[2].Available, "a page being open disables the main action");
        Click((Button)main.FindName("BackButton")); Pump(() => page.Content is null);
        tray.Apply(main.CurrentHomeState);
        check(main.CanRunPrimaryFromTray && tray.MenuItems[2].Enabled, "back on Home it is available again");

        // The language is applied in place.
        loc.Apply(Brisa.Localization.LocalizationManager.PortugueseBrazil);
        check(tray.MenuItems[1].Text == "Abrir" && tray.MenuItems[4].Text == "Sair", "Open and Exit follow the language");
        check(tray.MenuItems[2].Text == main.CurrentHomeState.PrimaryLabel && tray.MenuItems[0].Text.StartsWith("Brisa: ") && tray.MenuItems[0].Text.Contains(main.CurrentHomeState.StatusText), "the status and the main action follow it too");
        loc.Apply(Brisa.Localization.LocalizationManager.English);
        check(tray.MenuItems[1].Text == "Open", "and back");

        // Dispose leaves nothing subscribed to the window.
        var eventField = typeof(MainWindow).GetField("HomeStateChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        check(eventField.GetValue(main) is not null, "the tray listens to Home while it lives");
        tray.Dispose();
        tray.Dispose();
        check(eventField.GetValue(main) is null, "Dispose unsubscribes from Home");
        backend.State = backend.State with { Connected = false, TunnelActive = false, OwnedTunnelPresent = false };
        main.InitializeAsync(openAccount: false).GetAwaiter().GetResult();
        main.RequestExit(); Pump(() => !main.IsVisible);

        // Busy: one connect, however many times the tray is used.
        var gated = new UxBackend(SignedInIdle()) { ConnectGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var busy = new MainWindow(gated, store, exitWarning: _ => { });
        busy.Show(); busy.InitializeAsync().GetAwaiter().GetResult();
        var busyTray = new TrayService(busy, store, showNotifyIcon: false);
        check(busy.CanRunPrimaryFromTray, "idle Home allows the tray action");
        busy.RunPrimaryFromTray();
        Pump(() => gated.Commands.Contains("connect"));
        busyTray.Apply(busy.CurrentHomeState);
        check(!busy.CanRunPrimaryFromTray && !busyTray.MenuItems[2].Enabled, "busy work disables the main action");
        busy.RunPrimaryFromTray(); busy.RunPrimaryFromTray(); busyTray.ClickMenuItem(busyTray.MenuItems[2]);
        Spin(150);
        check(gated.Commands.Count(command => command == "connect") == 1, "exactly one connect command");
        gated.ConnectGate.SetResult(new CommandResult(true));
        Pump(() => busy.CurrentHomeState.Phase == ConnectionPhase.Connected && busy.CanRunPrimaryFromTray);
        check(gated.Commands.Count(command => command == "connect") == 1 && busy.CurrentHomeState.PrimaryLabel == "Disconnect", "it finished once and now offers Disconnect");
        busyTray.Dispose();
        busy.RequestExit(); Pump(() => !busy.IsVisible);

        // Home can look idle while another operation holds the UI, for example a profile import. The
        // phase is still Disconnected, so only the busy check keeps the tray from starting a connect.
        var importing = new UxBackend(SignedInIdle()) { ImportGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var importer = new MainWindow(importing, store, exitWarning: _ => { });
        importer.Show(); importer.InitializeAsync().GetAwaiter().GetResult();
        var importerTray = new TrayService(importer, store, showNotifyIcon: false);
        var pendingImport = importer.ImportConfigAsync(@"C:\pickedoute.conf");
        Pump(() => importing.Commands.Contains("importConfig"));
        importerTray.Apply(importer.CurrentHomeState);
        check(importer.CurrentHomeState.PrimaryEnabled && importer.CurrentHomeState.Phase == ConnectionPhase.Disconnected, "Home still looks idle during the import");
        check(!importer.CanRunPrimaryFromTray && !importerTray.MenuItems[2].Enabled, "an operation that owns the UI keeps the tray action off");
        importer.RunPrimaryFromTray(); importerTray.ClickMenuItem(importerTray.MenuItems[2]);
        Spin(100);
        check(!importing.Commands.Contains("connect"), "the tray starts nothing while that operation runs");
        importing.ImportGate.SetResult(new CommandResult(true));
        Pump(() => pendingImport.IsCompleted);
        check(pendingImport.IsCompletedSuccessfully, "the import finished once released");
        importerTray.Dispose();
        importer.RequestExit(); Pump(() => !importer.IsVisible);

        // Signed out: the action is hidden and cannot run.
        var signedOut = new MainWindow(new UxBackend(new(false, false, true, false, "", null)), store, exitWarning: _ => { });
        signedOut.Show(); signedOut.InitializeAsync(openAccount: false).GetAwaiter().GetResult();
        var signedOutTray = new TrayService(signedOut, store, showNotifyIcon: false);
        check(signedOut.CurrentHomeState.RequiresSignIn && !signedOut.CanRunPrimaryFromTray && !signedOutTray.MenuItems[2].Available, "signed out hides the main action");
        signedOut.RunPrimaryFromTray();
        Spin(100);
        check(((ContentControl)signedOut.FindName("PageHost")).Content is null, "and the tray cannot open the Account page");
        signedOutTray.Dispose();
        signedOut.RequestExit(); Pump(() => !signedOut.IsVisible);

        // Limited: Restart as administrator is available when it can run, never beside a tunnel.
        var restarts = 0;
        var limited = new MainWindow(new UxBackend(new(false, false, false, true, "u", null, RequiresElevation: true)), store, exitWarning: _ => { })
        { RestartElevatedOverride = () => { restarts++; return Task.CompletedTask; } };
        limited.Show(); limited.InitializeAsync(openAccount: false).GetAwaiter().GetResult();
        var limitedTray = new TrayService(limited, store, showNotifyIcon: false);
        check(limited.CurrentHomeState.CanRestartElevated && limited.CanRunPrimaryFromTray && limitedTray.MenuItems[2].Text == "Restart as administrator" && limitedTray.MenuItems[2].Enabled, "limited offers Restart as administrator");
        limitedTray.ClickMenuItem(limitedTray.MenuItems[2]);
        Pump(() => restarts == 1);
        Spin(100);
        check(restarts == 1 && limited.IsVisible, "the tray runs the same restart path, once");
        limitedTray.Dispose();
        limited.RequestExit(); Pump(() => !limited.IsVisible);
        var running = new MainWindow(new UxBackend(new(false, false, true, true, "u", null, OwnedTunnelPresent: true, RequiresElevation: true)), store, exitWarning: _ => { });
        running.Show(); running.InitializeAsync(openAccount: false).GetAwaiter().GetResult();
        check(running.CurrentHomeState.Phase == ConnectionPhase.Limited && !running.CanRunPrimaryFromTray, "limited beside a running tunnel stays off");
        running.RequestExit(); Pump(() => !running.IsVisible);

        // A failed start offers no main action from the tray.
        var failed = new MainWindow(new UnavailableBackendClient(), store, exitWarning: _ => { });
        failed.Show(); failed.InitializeAsync(openAccount: false).GetAwaiter().GetResult();
        var failedTray = new TrayService(failed, store, showNotifyIcon: false);
        check(failed.CurrentHomeState.StartupFailed && !failedTray.MenuItems[2].Available && !failed.CanRunPrimaryFromTray, "a failed start hides the main action");
        failedTray.Dispose();
        failed.RequestExit(); Pump(() => !failed.IsVisible);
    }

    private static void TrayLimits(SettingsStore store, Action<bool, string> check)
    {
        check(TrayService.Truncate("short", 63) == "short" && TrayService.Truncate(new string('a', 63), 63).Length == 63, "text at the limit is kept");
        var cut = TrayService.Truncate(new string('a', 64), 63);
        check(cut.Length == 63 && cut.EndsWith("..."), "one over is cut to 63 with three dots");
        var pair = char.ConvertFromUtf32(0x1F600);
        var split = TrayService.Truncate(new string('a', 59) + pair + "zzzz", 63);
        check(split.Length <= 63 && !split.Any(char.IsSurrogate), "a surrogate pair is never cut in half");
        check(TrayService.Truncate(new string('x', 5000), 63).Length == 63, "very long text is still cut to 63");
        check(NativeMethods.RegisterWindowMessage("TaskbarCreated") != 0 && TrayService.AllowTaskbarCreated(), "the TaskbarCreated message can be allowed");
    }

    private static void RouteWindowChoices(SettingsStore store, Action<bool, string> check)
    {
        var main = new MainWindow(new UxBackend(SignedInIdle()), store, exitWarning: _ => { });
        // A constructed window stays in Application.Windows until it is closed, so every probe closes its window.
        static string Tag(string? preferred = null, bool argument = true)
        {
            var window = argument ? new RouteWindow(null!, preferred) : new RouteWindow(null!);
            try { return (string)((ListBoxItem)((ListBox)window.FindName("CountryList")).SelectedItem).Tag; }
            finally { window.Close(); }
        }
        static (Visibility Visible, string Text, double HelperGap) Caption(RouteInfo? current, string? preferred = null)
        {
            var window = new RouteWindow(null!, preferred, current);
            try
            {
                var text = (TextBlock)window.FindName("CurrentRouteText");
                return (text.Visibility, text.Text, ((TextBlock)window.FindName("HelperText")).Margin.Bottom);
            }
            finally { window.Close(); }
        }
        check(Tag(argument: false) == "", "the one argument form still starts on Automatic");
        check(Tag("JP") == "JP" && Tag("jp") == "JP", "a saved country that is offered is preselected, in any case");
        check(Tag("SV") == "" && Tag(null) == "" && Tag("") == "" && Tag("  ") == "", "an unknown or empty one falls back to Automatic");

        var loc = Brisa.Localization.LocalizationManager.Current;
        var none = Caption(null);
        check(none.Visible == Visibility.Collapsed && none.HelperGap == 20, "no route, no caption, and the layout is unchanged");
        var withPing = Caption(new RouteInfo("JP#28", "JP", 34), "NL");
        check(withPing.Visible == Visibility.Visible && withPing.Text == "Current route: Japan JP#28, 34 ms", "caption with a ping: " + withPing.Text);
        check(withPing.HelperGap == 8, "the caption takes its room from the gap above the list");
        check(Caption(new RouteInfo("JP#28", "JP")).Text == "Current route: Japan JP#28", "caption without a ping");
        check(Caption(new RouteInfo("JP#28", "")).Text == "Current route: JP#28", "caption without a country has no double space");
        check(Caption(new RouteInfo("", "")).Visible == Visibility.Collapsed, "an empty route shows nothing");
        loc.Apply(Brisa.Localization.LocalizationManager.PortugueseBrazil);
        var portuguese = Caption(new RouteInfo("JP#28", "JP", 34));
        check(portuguese.Text == "Rota atual: " + Brisa.Localization.CountryNames.Name("JP", "pt-BR") + " JP#28, 34 ms", "caption in Portuguese: " + portuguese.Text);
        loc.Apply(Brisa.Localization.LocalizationManager.English);
        main.RequestExit(); Pump(() => !main.IsVisible);
    }

    private static void ScansUseTheSavedCountry(SettingsStore store, Action<bool, string> check)
    {
        try
        {
            foreach (var (country, expected) in new (string?, string)[] { ("NL", "{\"country\":\"NL\"}"), (null, "{}"), ("", "{}") })
            {
                store.Save(store.Current with { PreferredRouteCountry = country, AutoConnect = false });
                var backend = new UxBackend(SignedInIdle());
                var main = new MainWindow(backend, store, exitWarning: _ => { }, autoRouteScan: true);
                main.Show(); main.InitializeAsync(openAccount: false).GetAwaiter().GetResult();
                Pump(() => backend.Calls.Any(call => call.Command == "optimize"));
                check(backend.Calls.First(call => call.Command == "optimize").Payload == expected, "the launch scan sends " + expected + " for [" + country + "]: " + backend.Calls.FirstOrDefault(call => call.Command == "optimize").Payload);
                main.RequestExit(); Pump(() => !main.IsVisible);
            }
            foreach (var (country, expected) in new (string?, string)[] { ("DE", "{\"country\":\"DE\"}"), (null, "{}") })
            {
                store.Save(store.Current with { PreferredRouteCountry = country, AutoConnect = true });
                var backend = new UxBackend(SignedInIdle());
                var main = new MainWindow(backend, store, exitWarning: _ => { }, autoRouteScan: true, autoConnect: true);
                main.Show(); main.InitializeAsync(openAccount: false).GetAwaiter().GetResult();
                Pump(() => backend.Commands.Contains("connect"));
                check(backend.Calls.First(call => call.Command == "optimize").Payload == expected, "the scan before an automatic connect sends " + expected + " for [" + country + "]");
                check(backend.Commands.IndexOf("optimize") < backend.Commands.IndexOf("connect") && backend.Commands.Count(command => command == "optimize") == 1, "one scan, then the connect");
                main.RequestExit(); Pump(() => !main.IsVisible);
            }
        }
        finally { store.Save(store.Current with { PreferredRouteCountry = null, AutoConnect = false }); }
    }

    private static void RouteClickRemembers(SettingsStore store, Action<bool, string> check)
    {
        store.Save(store.Current with { PreferredRouteCountry = null, AutoConnect = false });
        var backend = new UxBackend(SignedInIdle());
        var main = new MainWindow(backend, store, exitWarning: _ => { });
        main.Show(); main.InitializeAsync(openAccount: false).GetAwaiter().GetResult();
        string? preselected = null;
        string? caption = null;
        void ClickRoute(string wanted)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            timer.Tick += (_, _) =>
            {
                var window = Application.Current.Windows.OfType<RouteWindow>().FirstOrDefault(candidate => candidate.IsVisible);
                if (window is null) return;
                timer.Stop();
                var list = (ListBox)window.FindName("CountryList");
                preselected = (string)((ListBoxItem)list.SelectedItem).Tag;
                var text = (TextBlock)window.FindName("CurrentRouteText");
                caption = text.Visibility == Visibility.Visible ? text.Text : null;
                list.SelectedItem = list.Items.OfType<ListBoxItem>().First(item => (string)item.Tag == wanted);
                ((Button)window.FindName("UseRouteButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            timer.Start();
            Click((Button)main.FindName("RouteButton"));
            timer.Stop();
        }

        ClickRoute("DE");
        Pump(() => store.Current.PreferredRouteCountry == "DE");
        check(preselected == "" && caption is null, "first time: Automatic is preselected and there is no route to show");
        check(backend.Calls.Last(call => call.Command == "optimize").Payload.Contains("\"country\":\"DE\""), "the manual route is sent: " + backend.Calls.Last(call => call.Command == "optimize").Payload);
        check(new SettingsStore(store.DataDirectory, false).Current.PreferredRouteCountry == "DE", "the choice is written to settings.json");

        Pump(() => ((Button)main.FindName("RouteButton")).IsEnabled);
        backend.State = backend.State with { Route = new RouteInfo("DE#11", "DE", 28) };
        main.InitializeAsync(openAccount: false).GetAwaiter().GetResult();
        ClickRoute("");
        Pump(() => store.Current.PreferredRouteCountry is null);
        check(preselected == "DE" && caption == "Current route: Germany DE#11, 28 ms", "second time: the saved country is preselected and the route is shown: " + caption);
        check(new SettingsStore(store.DataDirectory, false).Current.PreferredRouteCountry is null, "Automatic is saved as no preference");
        main.RequestExit(); Pump(() => !main.IsVisible);
    }

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

    private sealed class UxBackend(NativeSnapshot state) : IBackendClient
    {
        public NativeSnapshot State { get; set; } = state;
        public CommandResult ConnectResult { get; set; } = new(true);
        public List<string> Commands { get; } = [];
        // Every command with its payload as JSON, so a test can see exactly what was sent.
        public List<(string Command, string Payload)> Calls { get; } = [];
        public TaskCompletionSource<CommandResult>? ConnectGate { get; set; }
        public TaskCompletionSource<CommandResult>? ImportGate { get; set; }
        public CommandResult OptimizeResult { get; set; } = new(true);
        public Task<NativeSnapshot> SnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(State);
        public async Task<CommandResult> CommandAsync(string command, object payload, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            Calls.Add((command, System.Text.Json.JsonSerializer.Serialize(payload)));
            if (command == "optimize") return OptimizeResult;
            if (command == "connect")
            {
                if (ConnectGate is not null) await ConnectGate.Task;
                if (ConnectResult.Success)
                    State = State with { Connected = true, TunnelActive = true, OwnedTunnelPresent = true };
                return ConnectResult;
            }
            if (command == "disconnect")
            {
                State = State with { Connected = false, TunnelActive = false, OwnedTunnelPresent = false };
                return new CommandResult(true);
            }
            if (command == "importConfig")
            {
                if (ImportGate is not null) await ImportGate.Task;
                State = State with { Mode = "custom", SignedIn = false, Connected = false, TunnelActive = false, OwnedTunnelPresent = false };
                return new CommandResult(true);
            }
            return new CommandResult(true);
        }
        public Task CancelAsync() => Task.CompletedTask;
        public Task<string> DiagnosticsAsync(CancellationToken cancellationToken = default) => Task.FromResult("fixture");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void Pump(Func<bool> completed)
    {
        var end = DateTime.UtcNow.AddSeconds(5);
        while (!completed() && DateTime.UtcNow < end)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }
}

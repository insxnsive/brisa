using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using Brisa.Models;
using System.Windows.Threading;
using Brisa;
using Brisa.Security;
using Brisa.Services;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
#pragma warning disable WPF0001
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, ThemeMode = ThemeMode.Dark };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
#pragma warning restore WPF0001
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Brisa;component/Themes/NativeTheme.xaml") });
        var root = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "native-navigation-" + Guid.NewGuid().ToString("N"));
        var count = 0;
        try
        {
            var motionProbe = new Border { Width = 100, Height = 100 };
            PageTransition.Show(motionProbe, () => false);
            Check(!motionProbe.HasAnimatedProperties && motionProbe.Opacity == 1,
                "reduced-motion policy shows navigation content immediately");
            PageTransition.Show(motionProbe, () => true);
            Check(motionProbe.HasAnimatedProperties,
                "enabled navigation has a finite entrance animation");
            PageTransition.Stop(motionProbe);
            Check(!motionProbe.HasAnimatedProperties && motionProbe.Opacity == 1,
                "stopping navigation motion leaves content fully visible");
            foreach (var name in new[] { "AccountView", "SettingsView" })
            {
                var type = typeof(MainWindow).Assembly.GetType("Brisa." + name);
                Check(type is not null && typeof(UserControl).IsAssignableFrom(type), name + " must be an inline UserControl, not a Window");
            }
            // Legacy installations persisted CloseToTray=false. X must still only hide.
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "settings.json"), "{\"Theme\":2,\"CloseToTray\":false}");
            var store = new SettingsStore(root, false);
            Check(!store.Current.AutoConnect, "legacy settings default AutoConnect to false");
            LanguageSwitchWhileConnected(store, Check);
            var startupFailure = new StartupProbeBackend { StartupCode = "INIT_DATA_DIR_INVALID" };
            startupFailure.Release.TrySetException(new BackendUnavailableException());
            var startupFailureMain = new MainWindow(startupFailure, store);
            startupFailureMain.Show(); startupFailureMain.InitializeAsync().GetAwaiter().GetResult();
            Check(((TextBlock)startupFailureMain.FindName("DetailText")).Text.Contains("INIT_DATA_DIR_INVALID"),
                "startup failure detail appends its fixed diagnostic code");
            startupFailureMain.RequestExit(); Pump(() => app.Windows.Count == 0);
            var unavailableOrder = new List<string>();
            var unavailable = new MainWindow(new UnavailableBackendClient(), store,
                updates: new OrderedUpdateService(unavailableOrder));
            unavailable.Show(); unavailable.InitializeAsync().GetAwaiter().GetResult();
            Check(!((TextBlock)unavailable.FindName("DetailText")).Text.Contains("INIT_UNKNOWN"),
                "an unknown startup code stays out of the user-facing error detail");
            Check(new UnavailableBackendClient().DiagnosticsAsync().GetAwaiter().GetResult().Contains("Startup code: INIT_UNKNOWN"),
                "unknown startup codes remain available in the generated support log");
            Click((Button)unavailable.FindName("SettingsButton"));
            var failedHost = (ContentControl)unavailable.FindName("PageHost");
            Click((Button)((SettingsView)failedHost.Content).FindName("SaveButton"));
            Pump(() => failedHost.Content is null);
            Check(((TextBlock)unavailable.FindName("DetailText")).Text.Contains("native service"), "returning from Settings preserves actionable terminal-failure details");
            unavailable.RequestExit(); Pump(() => app.Windows.Count == 0);
            Check(!unavailableOrder.Contains("update-apply"), "failed-service exit cannot apply an update without verified ownership");
            var logLanguage = Brisa.Localization.LocalizationManager.Current.Language;
            foreach (var language in new[] { "en", "pt-BR" })
            {
                Brisa.Localization.LocalizationManager.Current.Apply(language);
                var failedLog = new AdvancedView(new CountingBackend { FailDiagnostics = true }, null);
                Click((Button)failedLog.FindName("LogButton"));
                Check(((TextBox)failedLog.FindName("LogBox")).Text == (language == "en" ? "The log could not be generated." : "Não foi possível gerar o log."),
                    "log-generation failures use the selected UI language");
                failedLog.DisposeAsync().GetAwaiter().GetResult();
            }
            Brisa.Localization.LocalizationManager.Current.Apply(logLanguage);
            var failedClose = new MainWindow(new UnavailableBackendClient(), store);
            failedClose.Show(); failedClose.InitializeAsync().GetAwaiter().GetResult();
            failedClose.Close(); Pump(() => !failedClose.IsVisible);
            Check(!app.Windows.Cast<Window>().Contains(failedClose), "X exits a terminal idle startup failure instead of hiding it in the tray");
            var unknownStartup = new StartupProbeBackend();
            var cancelledStartup = new StartupProbeBackend();
            cancelledStartup.Release.TrySetCanceled();
            var cancelledStartupMain = new MainWindow(cancelledStartup, store);
            cancelledStartupMain.Show(); cancelledStartupMain.InitializeAsync().GetAwaiter().GetResult();
            Check(((TextBlock)cancelledStartupMain.FindName("DetailText")).Text == Brisa.Localization.LocalizationManager.Current["ServiceTimeout"],
                "first-load cancellation renders the localized service timeout");
            cancelledStartupMain.RequestExit(); Pump(() => app.Windows.Count == 0);
            unknownStartup.Release.TrySetException(new TimeoutException("fixture initial status timeout"));
            var unknownMain = new MainWindow(unknownStartup, store);
            unknownMain.Show(); unknownMain.InitializeAsync().GetAwaiter().GetResult();
            unknownMain.Close(); Pump(() => !unknownMain.IsVisible);
            Check(app.Windows.Cast<Window>().Contains(unknownMain), "an unknown initial status is not proof that the backend stopped; X still hides");
            unknownMain.RequestExit(); Pump(() => app.Windows.Count == 0);
            var startup = new StartupProbeBackend();
            var startupMain = new MainWindow(startup, store);
            startupMain.Show(); var initialization = startupMain.InitializeAsync();
            startupMain.RequestExit(); Pump(() => app.Windows.Count == 0);
            startup.Release.TrySetResult(new(false, false, true, true, "fixture", null));
            Pump(() => initialization.IsCompleted);
            var startupTimer = (DispatcherTimer)typeof(MainWindow).GetField("_statusTimer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(startupMain)!;
            Check(!startupTimer.IsEnabled, "late startup completion never restarts polling after close");
            Check(startup.StartupToken.CanBeCanceled, "initial status request has a cancellable lifetime");

            var crashed = new CrashDuringConnectBackend();
            var exitWarnings = new List<string>();
            var crashedMain = new MainWindow(crashed, store, exitWarning: exitWarnings.Add);
            crashedMain.Show(); crashedMain.InitializeAsync().GetAwaiter().GetResult();
            Click((Button)crashedMain.FindName("PrimaryButton"));
            Pump(() => ((TextBlock)crashedMain.FindName("StatusText")).Text == "Couldn’t load status");
            Check(!((TextBlock)crashedMain.FindName("DetailText")).Text.Contains("You can exit"), "uncertain connection failure cannot promise a safe exit");
            crashedMain.RequestExit(); Pump(() => exitWarnings.Count > 0 || app.Windows.Count == 0);
            Check(exitWarnings.Count == 1 && app.Windows.Cast<Window>().Contains(crashedMain), "a failed Connect cannot erase possible tunnel ownership and silently exit");
            crashed.Recovered = true;
            crashedMain.RequestExit(); Pump(() => app.Windows.Count == 0);

            var closeBackend = new CloseProbeBackend();
            var closeMain = new MainWindow(closeBackend, store);
            closeMain.Show(); closeMain.InitializeAsync().GetAwaiter().GetResult();
            closeMain.Close(); Pump(() => !closeMain.IsVisible);
            Check(closeBackend.Commands.Count == 0 && closeBackend.CancelCalls == 0 && closeBackend.DisposeCalls == 0,
                "X preserves the backend and connection, even with legacy CloseToTray=false");
            Check(app.Windows.Cast<Window>().Contains(closeMain), "X hides rather than destroys the window");
            closeMain.Show(); closeMain.Activate();
            Check(((TextBlock)closeMain.FindName("StatusText")).Text == "Connected", "reopening retains the connection state");
            closeMain.Close(); Pump(() => !closeMain.IsVisible);
            closeMain.RequestExit(); Pump(() => app.Windows.Count == 0);
            Check(closeBackend.Commands.SequenceEqual(new[] { "disconnect" }) && closeBackend.DisposeCalls == 1,
                "explicit Exit still disconnects and disposes exactly once");
            store.Save(new(AppTheme.Dark));
            var firstRunBackend = new UiTestBackendClient();
            var firstRun = new MainWindow(firstRunBackend, store);
            firstRun.Show(); firstRun.InitializeAsync().GetAwaiter().GetResult();
            var firstRunHost = (ContentControl)firstRun.FindName("PageHost");
            Check(firstRunHost.Content is AccountView && !((FrameworkElement)firstRun.FindName("HomeContent")).IsVisible,
                "signed-out Proton startup immediately opens inline sign-in");
            Check(app.Windows.Count == 1, "onboarding does not create another window");
            var signup = ((AccountView)firstRunHost.Content).FindName("SignUpLink") as System.Windows.Documents.Hyperlink;
            Check(signup?.NavigateUri?.AbsoluteUri == "https://account.protonvpn.com/signup?plan=free",
                "sign-in offers the official free-account registration link");
            Uri? opened = null;
            var linkPage = new AccountView(firstRunBackend, firstRun,
                new NativeSnapshot(false, false, true, false, "", null), uri => opened = uri);
            var link = (System.Windows.Documents.Hyperlink)linkPage.FindName("SignUpLink");
            var navigate = new System.Windows.Navigation.RequestNavigateEventArgs(link.NavigateUri, "_blank")
                { RoutedEvent = System.Windows.Documents.Hyperlink.RequestNavigateEvent };
            link.RaiseEvent(navigate);
            Check(navigate.Handled && opened == link.NavigateUri && firstRunBackend.Commands.Count == 0,
                "signup click opens the public registration page without backend or authentication work");
            linkPage.DisposeAsync().GetAwaiter().GetResult();
            var failedLinkPage = new AccountView(firstRunBackend, firstRun,
                new NativeSnapshot(false, false, true, false, "", null), _ => throw new InvalidOperationException("fixture"));
            ((System.Windows.Documents.Hyperlink)failedLinkPage.FindName("SignUpLink")).RaiseEvent(
                new System.Windows.Navigation.RequestNavigateEventArgs(link.NavigateUri, "_blank")
                { RoutedEvent = System.Windows.Documents.Hyperlink.RequestNavigateEvent });
            Check(((TextBlock)failedLinkPage.FindName("RegistrationHelp")).Text.Contains("account.protonvpn.com/signup?plan=free"),
                "browser launch failure shows a usable URL instead of crashing");
            failedLinkPage.DisposeAsync().GetAwaiter().GetResult();
            Click((Button)firstRun.FindName("BackButton")); Pump(() => firstRunHost.Content is null);
            Check(System.Windows.Automation.AutomationProperties.GetName((Button)firstRun.FindName("PrimaryButton")) == "Sign In",
                "accessible primary label matches the signed-out action");
            Check(!((Button)firstRun.FindName("RouteButton")).IsEnabled, "route changes wait for Proton sign-in");
            Check(((Button)firstRun.FindName("PrimaryButton")).Content as string == "Sign In",
                "signed-out Home makes the required next action explicit");
            Click((Button)firstRun.FindName("PrimaryButton")); Pump(() => firstRunHost.Content is AccountView);
            Check(firstRunBackend.Commands.Count == 0, "signed-out primary action navigates instead of attempting Connect");
            firstRun.RequestExit(); Pump(() => app.Windows.Count == 0);
            var custom = new MainWindow(new UiTestBackendClient(new(false, false, true, false, "", null, Mode: "custom")), store);
            custom.Show(); custom.InitializeAsync().GetAwaiter().GetResult();
            Check(((ContentControl)custom.FindName("PageHost")).Content is null, "custom configuration does not require a Proton account");
            custom.RequestExit(); Pump(() => app.Windows.Count == 0);
            var backend = new UiTestBackendClient(new(false, false, true, true, "fixture", null));
            var main = new MainWindow(backend, store); main.Show(); main.InitializeAsync().GetAwaiter().GetResult();
            Check(((ContentControl)main.FindName("PageHost")).Content is null, "signed-in startup stays on Home");
            var hwnd = new WindowInteropHelper(main).Handle;
            var bounds = new Rect(main.Left, main.Top, main.Width, main.Height);
            var host = (ContentControl)main.FindName("PageHost");
            var home = (FrameworkElement)main.FindName("HomeContent");
            var back = (Button)main.FindName("BackButton");
            foreach (var name in new[] { "Settings", "Account", "Settings", "Account" })
            {
                Click((Button)main.FindName(name + "Button"));
                Pump(() => host.Content is UserControl);
                var page = (UserControl)host.Content;
                Check(page.GetType().Name == name + "View", "correct inline page");
                Check(!home.IsVisible && back.IsVisible, "Home is replaced, not stacked behind another window");
                Check(app.Windows.Count == 1 && new WindowInteropHelper(main).Handle == hwnd, "same single HWND");
                Check(new Rect(main.Left, main.Top, main.Width, main.Height) == bounds, "navigation keeps window geometry");
                Check(host.RenderTransform is System.Windows.Media.TranslateTransform or System.Windows.Media.MatrixTransform,
                    "inline page entrance uses a render transform without layout motion");
                if (name == "Settings")
                    ((RadioButton)page.FindName("LightTheme")).IsChecked = true;
                Click(back); Click(back); Pump(() => host.Content is null);
                Check(home.IsVisible && !back.IsVisible, "Back restores Connection");
                Check(host.Opacity == 1 && host.RenderTransform is System.Windows.Media.MatrixTransform,
                    "rapid repeated Back clears the outgoing page animation");
                Check(store.Current.Theme == AppTheme.Dark, "Back discards unsaved Settings");
            }
            Click((Button)main.FindName("SettingsButton")); Pump(() => host.Content is UserControl);
            var settings = (UserControl)host.Content;
            ((RadioButton)settings.FindName("LightTheme")).IsChecked = true;
            Click((Button)settings.FindName("SaveButton")); Pump(() => host.Content is null);
            Check(store.Current.Theme == AppTheme.Light && home.IsVisible, "Save applies settings and returns Home");
            Click((Button)main.FindName("SettingsButton")); Pump(() => host.Content is UserControl);
            // Advanced é página agora, não diálogo: abre na mesma janela e o Voltar retorna a Settings.
            var settingsPage = (UserControl)host.Content;
            Click((Button)settingsPage.FindName("AdvancedButton"));
            Pump(() => host.Content is AdvancedView);
            Check(!((Button)main.FindName("BackButton")).IsKeyboardFocused, "opening a page does not focus Back");
            Check(host.Content is AdvancedView, "Advanced opens as a page from the Settings page");
            Check(Application.Current.Windows.Count == 1, "Advanced opens in the same window");
            // O foco de entrada vai para a página, não para um controle: ela não pode desenhar
            // retângulo de foco, senão a região inteira aparece emoldurada na navegação por mouse.
            var advancedPage = (AdvancedView)host.Content;
            Check(advancedPage.FocusVisualStyle is null, "the page that takes entry focus paints no focus rectangle");
            Check(((TextBlock)advancedPage.FindName("VersionText")).Text.Contains(AppVersion.Current), "Advanced shows the packaged Brisa version");
            Check(!((Button)advancedPage.FindName("CheckUpdateButton")).IsEnabled, "isolated navigation never enables network update checks");
            var credits = (Expander)advancedPage.FindName("CreditsExpander");
            Check(credits.Header as string == "License and credits", "the licence sits behind one link");
            credits.IsExpanded = true; advancedPage.UpdateLayout();
            var creditText = Flatten(credits).OfType<TextBlock>().Select(t => t.Text).ToList();
            Check(creditText.Any(t => t.Contains("GPL-3.0")), "the GPL notice is still displayed");
            Check(creditText.Any(t => t.Contains("GoLiveBypass")), "the upstream attribution is still displayed");
            var homeFlashed = false;
            home.IsVisibleChanged += (_, e) => homeFlashed |= e.NewValue is true;
            Click((Button)main.FindName("BackButton"));
            Pump(() => host.Content is SettingsView);
            Check(host.Content is SettingsView, "going back from Advanced returns to Settings");
            Check(!homeFlashed, "Advanced back to Settings never shows Home");
            Click((Button)main.FindName("SettingsButton")); Pump(() => host.Content is UserControl);
            var cancelled = (UserControl)host.Content;
            ((RadioButton)cancelled.FindName("DarkTheme")).IsChecked = true;
            Click((Button)cancelled.FindName("CancelButton")); Pump(() => host.Content is null);
            Check(store.Current.Theme == AppTheme.Light && main.IsVisible, "Cancel discards changes without closing the app");
            Check(backend.Commands.Count == 0, "navigation never mutates a tunnel or account");
            Click((Button)main.FindName("AccountButton")); Pump(() => host.Content is UserControl);
            main.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(main), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Pump(() => host.Content is null);
            Check(home.IsVisible, "Escape returns to Connection without closing the window");
            main.RequestExit(); Pump(() => app.Windows.Count == 0);

            var healthBackend = new UiTestBackendClient(new(true, false, true, true, "fixture", null));
            var health = new MainWindow(healthBackend, store); health.Show(); health.InitializeAsync().GetAwaiter().GetResult();
            var timer = typeof(MainWindow).GetField("_statusTimer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(health) as DispatcherTimer;
            var hasRefresh = timer is not null;
            if (timer is not null)
            {
                timer.Interval = TimeSpan.FromMilliseconds(20);
                healthBackend.CommandAsync("disconnect", new { }).GetAwaiter().GetResult();
                Pump(() => ((TextBlock)health.FindName("StatusText")).Text == "Disconnected");
            }
            health.RequestExit(); Pump(() => app.Windows.Count == 0);
            Check(hasRefresh, "connection health is refreshed without requiring navigation");

            var connectBackend = new PendingConnectBackend();
            var connecting = new MainWindow(connectBackend, store); connecting.Show(); connecting.InitializeAsync().GetAwaiter().GetResult();
            Click((Button)connecting.FindName("PrimaryButton")); Pump(() => connectBackend.Started);
            connecting.UpdateLayout();
            // 8b: o traço salmão virou um anel neutro em volta do selo, que gira enquanto há
            // trabalho a caminho e para quando o estado assenta.
            var indicator = connecting.FindName("StatusRing") as System.Windows.Shapes.Ellipse;
            var rotation = connecting.FindName("StatusRingRotation") as System.Windows.Media.RotateTransform;
            // O anel só gira onde o Windows permite animação. O runner de CI roda com as animações
            // desligadas, então exigir o giro ali mediria a configuração do sistema, não o app: o
            // que o teste cobra é o anel visível durante o trabalho e parado depois, e o giro
            // quando o sistema o permite.
            var motionAllowed = StatusMotion.Allowed(null);
            if (motionAllowed && rotation is not null) Pump(() => rotation.HasAnimatedProperties);
            var ringShown = indicator is { IsVisible: true, ActualWidth: > 0, ActualHeight: > 0 };
            var progressVisible = ringShown && (!motionAllowed || rotation is { HasAnimatedProperties: true });
            var wasDisabled = !((Button)connecting.FindName("PrimaryButton")).IsEnabled;
            connecting.Close(); Pump(() => !connecting.IsVisible);
            Check(!connectBackend.Release.Task.IsCompleted && app.Windows.Cast<Window>().Contains(connecting),
                "X during Connect hides the window without cancelling the operation");
            connectBackend.Release.TrySetResult(); Pump(() => ((Button)connecting.FindName("PrimaryButton")).IsEnabled);
            connecting.Show();
            var stoppedAnimating = indicator is { Visibility: Visibility.Collapsed } && (!motionAllowed || rotation is { HasAnimatedProperties: false });
            connecting.RequestExit(); Pump(() => app.Windows.Count == 0);
            Check(progressVisible && wasDisabled, "connecting shows visible animated progress and prevents double submission");
            Check(stoppedAnimating, "progress stops after completion");

            var pendingBackend = new PendingAccountBackend();
            var pendingMain = new MainWindow(pendingBackend, store); pendingMain.Show(); pendingMain.InitializeAsync().GetAwaiter().GetResult();
            Click((Button)pendingMain.FindName("AccountButton"));
            var pendingHost = (ContentControl)pendingMain.FindName("PageHost");
            var account = (UserControl)pendingHost.Content;
            // Synthetic in-memory values only. No vault or saved account access.
            var password = (PasswordBox)account.FindName("PasswordInput"); password.Password = Guid.NewGuid().ToString("N");
            var code = (PasswordBox)account.FindName("TwoFactorInput"); code.Password = Guid.NewGuid().ToString("N");
            Click((Button)account.FindName("ActionButton")); Pump(() => pendingBackend.LoginStarted);
            Click((Button)pendingMain.FindName("BackButton")); Pump(() => pendingBackend.CancelStarted);
            Check(password.Password.Length == 0 && code.Password.Length == 0, "Back clears account secrets immediately");
            Check(pendingHost.Content == account, "Back joins cancellation before exposing Home");
            Check(!((Button)pendingMain.FindName("BackButton")).IsEnabled && !((Button)pendingMain.FindName("PrimaryButton")).IsEnabled, "navigation and Connect stay disabled during cancellation");
            pendingBackend.ReleaseCancel.TrySetResult(); Pump(() => pendingHost.Content is null);
            Check(pendingBackend.CancelCalls == 1, "pending account operation is cancelled exactly once");
            Check(((FrameworkElement)pendingMain.FindName("HomeContent")).IsVisible, "Home returns after cancellation acknowledgement");
            pendingMain.RequestExit(); Pump(() => app.Windows.Count == 0);

            var exitOrder = new List<string>();
            var orderedBackend = new OrderedExitBackend(exitOrder);
            var orderedUpdater = new OrderedUpdateService(exitOrder);
            var updateExit = new MainWindow(orderedBackend, store, updates: orderedUpdater);
            updateExit.Show(); updateExit.InitializeAsync().GetAwaiter().GetResult();
            updateExit.RequestUpdateRestart(); Pump(() => app.Windows.Count == 0);
            Check(exitOrder.IndexOf("backend-disposed") >= 0 && exitOrder.IndexOf("backend-disposed") < exitOrder.IndexOf("update-apply"), "Update apply starts only after ExitGuard and backend disposal");
            Check(exitOrder.IndexOf("update-stopped") >= 0 && exitOrder.IndexOf("update-stopped") < exitOrder.IndexOf("update-apply"), "Update service disposal precedes update apply");

            Check(exitOrder.Contains("update-restart"), "explicit Restart to Update relaunches after cleanup");

            var exitBackend = new PendingAccountBackend();
            var exiting = new MainWindow(exitBackend, store); exiting.Show(); exiting.InitializeAsync().GetAwaiter().GetResult();
            Click((Button)exiting.FindName("AccountButton"));
            var exitAccount = (UserControl)((ContentControl)exiting.FindName("PageHost")).Content;
            Click((Button)exitAccount.FindName("ActionButton")); Pump(() => exitBackend.LoginStarted);
            exiting.RequestExit(); Pump(() => exitBackend.CancelStarted);
            exitBackend.ReleaseCancel.TrySetResult(); Pump(() => app.Windows.Count == 0);
            Check(exitBackend.PeakCancellations == 1, "Exit joins account cancellation before the exit guard confirms cancellation");

            store.Save(new(AppTheme.Light));
            var trayBackend = new PendingAccountBackend();
            var trayOrder = new List<string>();
            var trayUpdater = new OrderedUpdateService(trayOrder);
            var tray = new MainWindow(trayBackend, store, updates: trayUpdater); tray.Show(); tray.InitializeAsync().GetAwaiter().GetResult();
            Click((Button)tray.FindName("AccountButton"));
            var trayHost = (ContentControl)tray.FindName("PageHost");
            Click((Button)((UserControl)trayHost.Content).FindName("ActionButton")); Pump(() => trayBackend.LoginStarted);
            Click((Button)tray.FindName("BackButton")); Pump(() => trayBackend.CancelStarted);
            tray.Close();
            var waitedForPendingNavigation = tray.IsVisible;
            trayBackend.ReleaseCancel.TrySetResult(); Pump(() => trayHost.Content is null && !tray.IsVisible);
            Check(!trayOrder.Contains("update-apply"), "Close-to-Tray never applies a staged update");
            tray.RequestExit(); Pump(() => app.Windows.Count == 0);
            Check(trayOrder.Contains("update-apply"), "ordinary explicit exit applies a staged update after safe shutdown");
            Check(!trayOrder.Contains("update-restart"), "ordinary Exit never relaunches the app");
            Check(waitedForPendingNavigation, "Close-to-Tray joins an already-running Back before hiding");
            // Item 3: an unelevated launch is a usable limited-access screen, not a dead end.
            var limited = new NativeSnapshot(false, false, false, true, "fixture", null, RequiresElevation: true);
            var limitedBackend = new SnapshotBackend(limited);
            var limitedMain = new MainWindow(limitedBackend, store);
            limitedMain.Show(); limitedMain.InitializeAsync().GetAwaiter().GetResult();
            var limitedPrimary = (Button)limitedMain.FindName("PrimaryButton");
            Check(limitedPrimary.IsEnabled && (string)limitedPrimary.Content == "Restart as administrator", "limited access offers Restart as administrator");
            Check(((TextBlock)limitedMain.FindName("DetailText")).Text.Contains("administrator"), "limited access explains the administrator requirement");
            var limitedAccount = new AccountView(limitedBackend, limitedMain, limited);
            Check(((Button)limitedAccount.FindName("ActionButton")).IsEnabled, "signing out stays available while unelevated");
            var limitedAdvanced = new AdvancedView(limitedBackend, limited);
            Check(((Button)limitedAdvanced.FindName("ImportButton")).IsEnabled, "importing a profile stays available while unelevated");
            // This test process is not Brisa.exe, so the real relaunch refuses before any prompt can appear.
            Click(limitedPrimary); Spin(300);
            Check(limitedMain.IsVisible && limitedPrimary.IsEnabled && (string)limitedPrimary.Content == "Restart as administrator", "outside Brisa.exe the restart does nothing and the window stays on Limited");
            limitedMain.Close(); Pump(() => !limitedMain.IsVisible);
            // A refused administrator prompt shows its notice and leaves the window open on Limited.
            var restartCalls = 0;
            var restartNotices = new List<string>();
            var restartMain = new MainWindow(new SnapshotBackend(limited), store, notice: restartNotices.Add)
            {
                RestartElevatedOverride = () =>
                {
                    restartCalls++;
                    restartNotices.Add(Brisa.Localization.LocalizationManager.Current["ElevationCanceled"]);
                    return Task.CompletedTask;
                },
            };
            restartMain.Show(); restartMain.InitializeAsync().GetAwaiter().GetResult();
            var restartPrimary = (Button)restartMain.FindName("PrimaryButton");
            Click(restartPrimary); Pump(() => restartCalls == 1);
            Spin(200);
            Check(restartCalls == 1 && restartMain.IsVisible && restartPrimary.IsEnabled && (string)restartPrimary.Content == "Restart as administrator", "a refused restart keeps the window open on Limited");
            Check(restartNotices.Count == 1 && restartNotices[0].Contains("did not grant administrator rights"), "a refused restart explains itself");
            restartMain.Close(); Pump(() => !restartMain.IsVisible);

            // A logon launch stays on Home even when the account is signed out.
            var signedOut = new NativeSnapshot(false, false, true, false, "", null);
            var startupHome = new MainWindow(new SnapshotBackend(signedOut), store);
            startupHome.Show(); startupHome.InitializeAsync(openAccount: false).GetAwaiter().GetResult();
            Check(((ContentControl)startupHome.FindName("PageHost")).Content is null, "--startup does not jump into Account");
            var startupAccount = new MainWindow(new SnapshotBackend(signedOut), store);
            startupAccount.Show(); startupAccount.InitializeAsync().GetAwaiter().GetResult();
            Check(((ContentControl)startupAccount.FindName("PageHost")).Content is AccountView, "an interactive launch still opens Account when signed out");
            startupHome.Close(); startupAccount.Close();
            Pump(() => !startupHome.IsVisible && !startupAccount.IsVisible);

            // A1: one snapshot commit path. A response issued before an operation must not
            // repaint it, and handlers raised directly must refuse while it runs.
            var deferred = new DeferredMutationBackend();
            var a1Main = new MainWindow(deferred, store);
            a1Main.Show(); a1Main.InitializeAsync().GetAwaiter().GetResult();
            Check(((Button)a1Main.FindName("PrimaryButton")).IsEnabled, "an idle Home can start a connection");
            var refreshStatus = typeof(MainWindow).GetMethod("RefreshStatus", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            deferred.HoldNextSnapshot();
            refreshStatus.Invoke(a1Main, [null, EventArgs.Empty]);
            Pump(() => deferred.HeldSnapshot is not null);
            Click((Button)a1Main.FindName("PrimaryButton"));
            Pump(() => ((TextBlock)a1Main.FindName("StatusText")).Text == "Connecting…");
            Check(!((Button)a1Main.FindName("PrimaryButton")).IsEnabled, "primary is disabled while connecting");
            Check(!((Button)a1Main.FindName("SettingsButton")).IsEnabled, "Settings is disabled while connecting");
            // A snapshot requested before the connect must not overwrite the connecting state.
            deferred.CompleteHeld(new NativeSnapshot(false, false, true, true, "fixture", null));
            Spin(300);
            Check(((TextBlock)a1Main.FindName("StatusText")).Text == "Connecting…", "a stale snapshot cannot overwrite Connecting");
            // Handlers raised directly, not through a disabled control, must also refuse.
            Click((Button)a1Main.FindName("SettingsButton"));
            Click((Button)a1Main.FindName("AccountButton"));
            Click((Button)a1Main.FindName("RouteButton"));
            Spin(200);
            Check(((ContentControl)a1Main.FindName("PageHost")).Content is null, "no page opens while an operation runs");
            Check(deferred.Commands.Count(command => command is "login" or "optimize" or "importConfig") == 0, "no extra command is issued while an operation runs");
            // Import through the shared gate: refused while busy, allowed when idle.
            var busyImport = a1Main.ImportConfigAsync(@"C:\picked\route.conf").GetAwaiter().GetResult();
            Check(!busyImport.Success && busyImport.Code == "UI_BUSY" && busyImport.Message!.Contains("Another operation is still running"), "import is refused while another operation runs");
            deferred.Current = new(true, false, true, true, "fixture", null);
            deferred.ConnectGate.TrySetResult(new CommandResult(true));
            Pump(() => ((TextBlock)a1Main.FindName("StatusText")).Text == "Connected");
            Check(((Button)a1Main.FindName("PrimaryButton")).IsEnabled && ((Button)a1Main.FindName("SettingsButton")).IsEnabled, "controls come back after the operation releases the gate");
            var idleImport = a1Main.ImportConfigAsync(@"C:\picked\route.conf").GetAwaiter().GetResult();
            Check(idleImport.Success && deferred.Commands.Contains("importConfig"), "import runs through the gate when the app is idle");
            a1Main.Close(); Pump(() => !a1Main.IsVisible);

            // A3: the already-fixed null selection. Clearing the list and confirming must not
            // throw, and Automatic must stay null rather than becoming an invalid country.
            string? chosen = "unset";
            var routeView = new RouteView(null, null, null);
            var routeHost = new Window { Content = routeView, Width = 440, Height = 520 };
            routeView.Chosen += (_, args) => chosen = args.Country;
            routeView.Loaded += (_, _) =>
            {
                ((ListBox)routeView.FindName("CountryList")).SelectedIndex = -1;
                ((Button)routeView.FindName("UseRouteButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            routeHost.Show();
            Pump(() => chosen != "unset");
            Check(chosen is null, "clearing the country list selects Automatic without throwing");
            routeHost.Close();

            // A4: a verification window that cannot start is reported, not silently cancelled.
            var verification = new MainWindow(new CaptchaBackend(), store)
            { VerificationDialogFactory = (_, _) => Task.FromException<VerificationAnswer?>(new VerificationUnavailableException()) };
            verification.Show();
            var unavailableResult = verification.ExecuteWithVerificationAsync("login", (_, _) => new { }).GetAwaiter().GetResult();
            Check(!unavailableResult.Success && unavailableResult.Code == "VERIFICATION_UNAVAILABLE" && unavailableResult.Message!.Contains("WebView2"),
                "a verification window that cannot start is reported to the user");
            verification.Close(); Pump(() => !verification.IsVisible);

            // A user cancel stays a silent cancellation, never a runtime-install message.
            var cancelledMain = new MainWindow(new CaptchaBackend(), store)
            { VerificationDialogFactory = (_, _) => Task.FromResult<VerificationAnswer?>(null) };
            cancelledMain.Show();
            var cancelledSilently = false;
            try { cancelledMain.ExecuteWithVerificationAsync("login", (_, _) => new { }).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { cancelledSilently = true; }
            Check(cancelledSilently, "a cancelled verification stays a cancellation");
            cancelledMain.Close(); Pump(() => !cancelledMain.IsVisible);

            // T2: a minimized window is still IsVisible to WPF, so the status timer must check
            // WindowState itself. Restoring from minimized refreshes once; maximize does not.
            var t2Backend = new SnapshotCountingBackend();
            var t2Main = new MainWindow(t2Backend, store);
            t2Main.Show(); t2Main.InitializeAsync().GetAwaiter().GetResult();
            // Stop the real 10 s timer so only the calls below can request a snapshot.
            ((DispatcherTimer)typeof(MainWindow).GetField("_statusTimer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(t2Main)!).Stop();
            var t2Refresh = typeof(MainWindow).GetMethod("RefreshStatus", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            Spin(200);
            var t2Baseline = t2Backend.Snapshots;
            t2Main.WindowState = WindowState.Minimized;
            Spin(200);
            Check(t2Main.IsVisible, "a minimized window still reports IsVisible");
            t2Refresh.Invoke(t2Main, [null, EventArgs.Empty]);
            Spin(200);
            Check(t2Backend.Snapshots == t2Baseline, "a minimized window skips the status refresh");
            t2Main.WindowState = WindowState.Normal;
            Spin(300);
            Check(t2Backend.Snapshots == t2Baseline + 1, "restoring from minimized refreshes status exactly once");
            t2Main.WindowState = WindowState.Maximized;
            Spin(200);
            t2Main.WindowState = WindowState.Normal;
            Spin(300);
            Check(t2Backend.Snapshots == t2Baseline + 1, "maximizing and restoring does not refresh status");
            t2Main.Close(); Pump(() => !t2Main.IsVisible);
            // F1: one quiet route scan at launch, and any user mutation preempts it.
            var idleProton = new NativeSnapshot(false, false, true, true, "fixture", null);
            MainWindow ScanWindow(RouteScanBackend backend)
            {
                var window = new MainWindow(backend, store, exitWarning: _ => { }, autoRouteScan: true);
                window.Show(); window.InitializeAsync(openAccount: false).GetAwaiter().GetResult();
                Spin(200);
                return window;
            }

            var scanned = new RouteScanBackend(idleProton);
            var scannedMain = ScanWindow(scanned);
            Check(scanned.Commands.SequenceEqual(new[] { "optimize" }), "a signed-in, disconnected proton launch scans the route once");
            Check(scanned.SnapshotsAfterScan >= 1, "a finished scan refreshes the route snapshot");
            scannedMain.RequestExit(); Pump(() => !scannedMain.IsVisible);

            foreach (var (label, snapshot) in new[]
            {
                ("signed out", idleProton with { SignedIn = false }),
                ("an owned tunnel", idleProton with { OwnedTunnelPresent = true }),
                ("custom mode", idleProton with { Mode = "custom" }),
                ("an unreadable inspection", idleProton with { Reliable = false }),
            })
            {
                var quiet = new RouteScanBackend(snapshot);
                var quietMain = ScanWindow(quiet);
                Check(!quiet.Commands.Contains("optimize"), "no route scan with " + label);
                quiet.Settle();
                quietMain.RequestExit(); Pump(() => !quietMain.IsVisible);
            }

            var racing = new RouteScanBackend(idleProton) { HoldOptimize = true };
            var racingMain = ScanWindow(racing);
            Check(racing.OptimizeInFlight, "the launch scan is still pinging");
            Click((Button)racingMain.FindName("PrimaryButton"));
            Pump(() => racing.Commands.Contains("connect"));
            Spin(200);
            Check(racing.Commands.Take(3).SequenceEqual(new[] { "optimize", "cancel", "connect" }), "a user Connect cancels the scan before the connect is sent");
            Check(!racing.AnsweredBusy, "the user's Connect is never answered MUTATION_BUSY");
            racingMain.RequestExit(); Pump(() => !racingMain.IsVisible);

            var early = new RouteScanBackend(idleProton);
            var earlyMain = new MainWindow(early, store, exitWarning: _ => { }, autoRouteScan: true);
            earlyMain.Show();
            Check(earlyMain.ImportConfigAsync(@"C:\picked\route.conf").GetAwaiter().GetResult().Success, "import runs before the launch scan starts");
            earlyMain.InitializeAsync(openAccount: false).GetAwaiter().GetResult();
            Spin(200);
            Check(!early.Commands.Contains("optimize"), "a user action before the scan starts skips it for this launch");
            earlyMain.RequestExit(); Pump(() => !earlyMain.IsVisible);

            // F2: Connect when Brisa opens, persisted in Settings, coordinated with the Proton route scan.
            var settingsProbe = new CountingBackend();
            var settingsMain = new MainWindow(settingsProbe, store);
            settingsMain.Show(); settingsMain.InitializeAsync().GetAwaiter().GetResult();
            var settingsHost = (ContentControl)settingsMain.FindName("PageHost");
            Click((Button)settingsMain.FindName("SettingsButton")); Pump(() => settingsHost.Content is SettingsView);
            ((CheckBox)((SettingsView)settingsHost.Content).FindName("AutoConnectBox")).IsChecked = true;
            Click((Button)((SettingsView)settingsHost.Content).FindName("CancelButton")); Pump(() => settingsHost.Content is null);
            Check(!store.Current.AutoConnect, "Cancel discards an unsaved AutoConnect toggle");
            Click((Button)settingsMain.FindName("SettingsButton")); Pump(() => settingsHost.Content is SettingsView);
            ((CheckBox)((SettingsView)settingsHost.Content).FindName("AutoConnectBox")).IsChecked = true;
            Click((Button)((SettingsView)settingsHost.Content).FindName("SaveButton")); Pump(() => settingsHost.Content is null);
            Check(store.Current.AutoConnect && new SettingsStore(root, false).Current.AutoConnect, "Save persists AutoConnect to settings.json");
            Check(settingsProbe.Commands.Count == 0, "saving AutoConnect in Settings never connects immediately");
            settingsMain.RequestExit(); Pump(() => !settingsMain.IsVisible);

            store.Save(store.Current with { AutoConnect = false });
            var offLaunch = new AutoConnectBackend(idleProton);
            var offMain = new MainWindow(offLaunch, store, exitWarning: _ => { }, autoRouteScan: true, autoConnect: true);
            offMain.Show(); offMain.InitializeAsync(openAccount: false).GetAwaiter().GetResult();
            Spin(200);
            Check(offLaunch.Commands.SequenceEqual(new[] { "optimize" }), "with AutoConnect off, launch only runs the quiet route scan");
            offMain.RequestExit(); Pump(() => !offMain.IsVisible);

            store.Save(store.Current with { AutoConnect = true });
            var protonLaunch = new AutoConnectBackend(idleProton) { OptimizedRoute = new("US#12", "US", 24) };
            var protonMain = new MainWindow(protonLaunch, store, exitWarning: _ => { }, autoRouteScan: true, autoConnect: true);
            protonMain.Show(); protonMain.InitializeAsync(openAccount: false).GetAwaiter().GetResult();
            Spin(200);
            Check(protonLaunch.Commands.SequenceEqual(new[] { "optimize", "connect" }) && protonLaunch.Cancels == 0,
                "Proton auto-connect completes the route scan before connecting instead of cancelling it");
            Check(((TextBlock)protonMain.FindName("StatusText")).Text == "Connected", "auto-connect reaches Connected");
            Check(((TextBlock)protonMain.FindName("RouteServerText")).Text == "US#12, 24 ms",
                "auto-connect keeps the freshly optimized route");
            protonMain.RequestExit(); Pump(() => !protonMain.IsVisible);

            var fallbackLaunch = new AutoConnectBackend(idleProton with { Route = new("BR#9", "BR", 18) })
            { OptimizeResult = new(false, "NETWORK_ERROR", "offline") };
            var fallbackNotices = new List<string?>();
            var fallbackMain = new MainWindow(fallbackLaunch, store, exitWarning: _ => { }, autoRouteScan: true, autoConnect: true)
            { CommandFailureNotice = (code, _) => fallbackNotices.Add(code) };
            fallbackMain.Show(); fallbackMain.InitializeAsync(openAccount: false).GetAwaiter().GetResult();
            Pump(() => ((TextBlock)fallbackMain.FindName("StatusText")).Text == "Connected");
            Check(fallbackLaunch.Commands.SequenceEqual(new[] { "optimize", "connect" }) && fallbackNotices.Count == 0,
                "a failed launch route scan falls back to connect without a scan error dialog");
            fallbackMain.RequestExit(); Pump(() => !fallbackMain.IsVisible);

            var customLaunch = new AutoConnectBackend(new(false, false, true, false, "", null, Mode: "custom"));
            var customAutoMain = new MainWindow(customLaunch, store, exitWarning: _ => { }, autoRouteScan: true, autoConnect: true);
            customAutoMain.Show(); customAutoMain.InitializeAsync(openAccount: false).GetAwaiter().GetResult();
            Pump(() => ((TextBlock)customAutoMain.FindName("StatusText")).Text == "Connected");
            Check(customLaunch.Commands.SequenceEqual(new[] { "connect" }),
                "custom-mode auto-connect connects directly without a Proton route scan");
            customAutoMain.RequestExit(); Pump(() => !customAutoMain.IsVisible);

            var failedAuto = new AutoConnectBackend(idleProton) { ConnectResult = new(false, "DISCORD_UNAVAILABLE", "fixture") };
            var failedAutoCodes = new List<string?>();
            var failedAutoMain = new MainWindow(failedAuto, store, exitWarning: _ => { }, autoRouteScan: false, autoConnect: true)
            { CommandFailureNotice = (code, _) => failedAutoCodes.Add(code) };
            failedAutoMain.Show(); failedAutoMain.InitializeAsync(openAccount: false).GetAwaiter().GetResult();
            Pump(() => failedAutoCodes.Count == 1 && ((Button)failedAutoMain.FindName("PrimaryButton")).IsEnabled);
            Check(failedAutoCodes[0] == "DISCORD_UNAVAILABLE" && ((Button)failedAutoMain.FindName("SettingsButton")).IsEnabled,
                "a failed auto-connect reports the failure code and restores Home controls");
            failedAutoMain.RequestExit(); Pump(() => !failedAutoMain.IsVisible);

            // F2/D2: an unverified verdict over a tunnel that already reports Connected
            // must not raise a modal, and the notice must arrive after the window leaves
            // Connecting so the user never sees a frozen busy screen behind it.
            var c1Backend = new AutoConnectBackend(idleProton)
            {
                ConnectResult = new(false, "ROUTE_UNVERIFIED", "unverified"),
                ConnectSnapshot = idleProton with { Connected = true, TunnelActive = true, OwnedTunnelPresent = true },
            };
            var c1Notices = new List<string?>();
            var c1Main = new MainWindow(c1Backend, store, exitWarning: _ => { }, autoRouteScan: false, autoConnect: true)
            { CommandFailureNotice = (code, _) => c1Notices.Add(code) };
            c1Main.Show(); c1Main.InitializeAsync(openAccount: false).GetAwaiter().GetResult();
            Pump(() => ((TextBlock)c1Main.FindName("StatusText")).Text == "Connected");
            Check(c1Notices.Count == 0, "an unverified result over a connected tunnel raises no failure notice");
            Check(((TextBlock)c1Main.FindName("StatusText")).Text == "Connected", "the connected tunnel stays Connected after an unverified result");
            c1Main.RequestExit(); Pump(() => !c1Main.IsVisible);

            MainWindow? c2Main = null;
            string? statusAtNotice = null;
            var c2Notices = new List<string?>();
            var c2Backend = new AutoConnectBackend(idleProton)
            {
                ConnectResult = new(false, "ROUTE_UNVERIFIED", "unverified"),
                ConnectSnapshot = idleProton with { Connected = false, TunnelActive = true, OwnedTunnelPresent = true },
            };
            c2Main = new MainWindow(c2Backend, store, exitWarning: _ => { }, autoRouteScan: false, autoConnect: true)
            {
                CommandFailureNotice = (code, _) =>
                {
                    c2Notices.Add(code);
                    statusAtNotice = ((TextBlock)c2Main!.FindName("StatusText")).Text;
                },
            };
            c2Main.Show(); c2Main.InitializeAsync(openAccount: false).GetAwaiter().GetResult();
            Pump(() => c2Notices.Count == 1);
            Check(c2Notices.Count == 1 && c2Notices[0] == "ROUTE_UNVERIFIED", "an unverified tunnel reports exactly one failure notice");
            Check(statusAtNotice is not null && statusAtNotice != "Connecting…",
                "the window leaves Connecting before the failure notice opens: " + statusAtNotice);
            c2Backend.Settle();
            c2Main.RequestExit(); Pump(() => !c2Main.IsVisible);

            foreach (var (label, snapshot, openAccount) in new[]
            {
                ("signed out interactive", idleProton with { SignedIn = false }, true),
                ("signed out logon", idleProton with { SignedIn = false }, false),
                ("already connected", idleProton with { Connected = true, TunnelActive = true, OwnedTunnelPresent = true }, false),
                ("owned unverified tunnel", idleProton with { TunnelActive = true, OwnedTunnelPresent = true }, false),
                ("unelevated limited launch", idleProton with { Reliable = false, RequiresElevation = true }, false),
                ("external tunnel", idleProton with { ExternalTunnel = true }, false),
                ("unreadable inspection", idleProton with { Reliable = false }, false),
            })
            {
                var guardBackend = new AutoConnectBackend(snapshot);
                var guardNotices = new List<string?>();
                var guardMain = new MainWindow(guardBackend, store, exitWarning: _ => { }, autoRouteScan: true, autoConnect: true)
                { CommandFailureNotice = (code, _) => guardNotices.Add(code) };
                guardMain.Show(); guardMain.InitializeAsync(openAccount: openAccount).GetAwaiter().GetResult();
                Spin(200);
                Check(guardBackend.Commands.Count == 0 && guardNotices.Count == 0, "no auto-connect or disconnect on " + label);
                guardBackend.Settle();
                guardMain.RequestExit(); Pump(() => !guardMain.IsVisible);
            }

            var isolatedDefault = new AutoConnectBackend(idleProton);
            var isolatedMain = new MainWindow(isolatedDefault, store, exitWarning: _ => { });
            isolatedMain.Show(); isolatedMain.InitializeAsync(openAccount: false).GetAwaiter().GetResult();
            Spin(200);
            Check(isolatedDefault.Commands.Count == 0, "default isolated MainWindow never auto-connects even when AutoConnect is saved");
            isolatedMain.RequestExit(); Pump(() => !isolatedMain.IsVisible);

            var earlyAuto = new AutoConnectBackend(idleProton);
            var earlyAutoMain = new MainWindow(earlyAuto, store, exitWarning: _ => { }, autoRouteScan: true, autoConnect: true);
            earlyAutoMain.Show();
            Check(earlyAutoMain.ImportConfigAsync(@"C:\picked\route.conf").GetAwaiter().GetResult().Success, "import runs before auto-connect starts");
            earlyAutoMain.InitializeAsync(openAccount: false).GetAwaiter().GetResult();
            Spin(200);
            Check(earlyAuto.Commands.SequenceEqual(new[] { "importConfig" }), "an early user action skips auto-connect for this launch");
            earlyAutoMain.RequestExit(); Pump(() => !earlyAutoMain.IsVisible);

            var heldScan = new AutoConnectBackend(idleProton) { HoldOptimize = true };
            var heldMain = new MainWindow(heldScan, store, exitWarning: _ => { }, autoRouteScan: true, autoConnect: true);
            heldMain.Show(); heldMain.InitializeAsync(openAccount: false).GetAwaiter().GetResult();
            Check(((TextBlock)heldMain.FindName("StatusText")).Text == "Connecting…"
                && !((Button)heldMain.FindName("PrimaryButton")).IsEnabled
                && !((Button)heldMain.FindName("SettingsButton")).IsEnabled,
                "auto-connect enters Connecting synchronously before InitializeAsync returns");
            heldMain.RequestExit(); Pump(() => !heldMain.IsVisible);
            Check(heldScan.Cancels >= 1 && !heldScan.Commands.Contains("connect"),
                "exiting during the pre-connect route scan cancels cleanly without starting the tunnel");
            store.Save(store.Current with { AutoConnect = false });

            NotificationBellTests.Run(store, Check);
            UxTests.Run(store, Check);

            Console.WriteLine($"{count} inline-navigation assertions passed");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { app.Shutdown(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); count++; }
    }
    private static void LanguageSwitchWhileConnected(SettingsStore store, Action<bool, string> check)
    {
        // Trocar o idioma não pode tocar no túnel: a mesma janela continua aberta e nenhum comando
        // de desconectar ou cancelar sai por causa da troca.
        var backend = new CountingBackend();
        var window = new MainWindow(backend, store);
        window.Show();
        window.InitializeAsync().GetAwaiter().GetResult();
        Click((Button)window.FindName("PrimaryButton"));
        Pump(() => backend.Commands.Contains("connect"));
        var commandsBefore = backend.Commands.ToArray();
        var cancelsBefore = backend.Cancels;
        var languageBefore = Brisa.Localization.LocalizationManager.Current.Language;

        Brisa.Localization.LocalizationManager.Current.Apply(Brisa.Localization.LocalizationManager.PortugueseBrazil);

        check(Brisa.Localization.LocalizationManager.Current.Language == "pt-BR", "language applies in place");
        check(ReferenceEquals(window, window) && window.IsVisible, "switching language keeps the window open");
        check(backend.Cancels == cancelsBefore, "switching language sends no cancel command");
        var added = backend.Commands.Skip(commandsBefore.Length).ToArray();
        check(!added.Any(c => c is "disconnect" or "cancel"), "switching language sends no disconnect or cancel command");
        var loc = Brisa.Localization.LocalizationManager.Current;
        var label = ((Button)window.FindName("PrimaryButton")).Content as string;
        check(label == loc["Connect"] || label == loc["Disconnect"] || label == loc["SignIn"],
            "the primary action is repainted in Portuguese without rebuilding the window");

        Brisa.Localization.LocalizationManager.Current.Apply(languageBefore);
        window.RequestExit();
        Pump(() => !window.IsVisible);
    }

    private sealed class CountingBackend : IBackendClient
    {
        public bool FailDiagnostics { get; set; }
        public List<string> Commands { get; } = new();
        public int Cancels { get; private set; }
        public Task<NativeSnapshot> SnapshotAsync(CancellationToken token = default) =>
            Task.FromResult(new NativeSnapshot(false, false, true, true, "fixture", null));
        public Task<CommandResult> CommandAsync(string command, object payload, CancellationToken token = default)
        {
            lock (Commands) Commands.Add(command);
            return Task.FromResult(new CommandResult(true));
        }
        public Task CancelAsync() { Cancels++; return Task.CompletedTask; }
        public Task<string> DiagnosticsAsync(CancellationToken token = default) => FailDiagnostics
            ? Task.FromException<string>(new IOException("synthetic fixture")) : Task.FromResult("fixture");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StartupProbeBackend : IBackendClient
    {
        public string? StartupCode { get; set; }
        private int reads;
        public CancellationToken StartupToken;
        public TaskCompletionSource<NativeSnapshot> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<NativeSnapshot> SnapshotAsync(CancellationToken token = default)
        {
            if (++reads == 1) { StartupToken = token; return Release.Task; }
            return Task.FromResult(new NativeSnapshot(false, false, true, true, "fixture", null));
        }
        public Task<CommandResult> CommandAsync(string command, object payload, CancellationToken token = default) => Task.FromResult(new CommandResult(true));
        public Task CancelAsync() => Task.CompletedTask;
        public Task<string> DiagnosticsAsync(CancellationToken token = default) => Task.FromResult("fixture");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class CrashDuringConnectBackend : IBackendClient
    {
        private bool crashed;
        public bool Recovered;
        public Task<NativeSnapshot> SnapshotAsync(CancellationToken token = default) => crashed && !Recovered
            ? Task.FromException<NativeSnapshot>(new BackendUnavailableException())
            : Task.FromResult(new NativeSnapshot(false, false, true, true, "fixture", null));
        public Task<CommandResult> CommandAsync(string command, object payload, CancellationToken token = default)
        {
            if (command == "connect") { crashed = true; throw new BackendUnavailableException(); }
            return Task.FromResult(new CommandResult(true));
        }
        public Task CancelAsync() => crashed && !Recovered ? Task.FromException(new BackendUnavailableException()) : Task.CompletedTask;
        public Task<string> DiagnosticsAsync(CancellationToken token = default) => Task.FromResult("fixture");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class CloseProbeBackend : IBackendClient
    {
        private NativeSnapshot _snapshot = new(true, false, true, true, "fixture", null);
        public List<string> Commands { get; } = [];
        public int CancelCalls, DisposeCalls;
        public Task<NativeSnapshot> SnapshotAsync(CancellationToken token = default) => Task.FromResult(_snapshot);
        public Task<CommandResult> CommandAsync(string command, object payload, CancellationToken token = default)
        {
            Commands.Add(command);
            if (command == "disconnect") _snapshot = _snapshot with { Connected = false, TunnelActive = false };
            return Task.FromResult(new CommandResult(true));
        }
        public Task CancelAsync() { CancelCalls++; return Task.CompletedTask; }
        public Task<string> DiagnosticsAsync(CancellationToken token = default) => Task.FromResult("Fixture");
        public ValueTask DisposeAsync() { DisposeCalls++; return ValueTask.CompletedTask; }
    }
    private sealed class PendingConnectBackend : IBackendClient
    {
        public bool Started;
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<NativeSnapshot> SnapshotAsync(CancellationToken token = default) => Task.FromResult(new NativeSnapshot(false, false, true, true, "fixture", null));
        public async Task<CommandResult> CommandAsync(string command, object payload, CancellationToken token = default)
        {
            if (command == "connect") { Started = true; await Release.Task.WaitAsync(token); }
            return new(true);
        }
        public Task CancelAsync() => Task.CompletedTask;
        public Task<string> DiagnosticsAsync(CancellationToken token = default) => Task.FromResult("Fixture");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class PendingAccountBackend : IBackendClient
    {
        public bool LoginStarted, CancelStarted;
        public int CancelCalls, ActiveCancellations, PeakCancellations;
        public TaskCompletionSource ReleaseCancel { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<NativeSnapshot> SnapshotAsync(CancellationToken token = default) => Task.FromResult(new NativeSnapshot(false, false, true, false, "", null));
        public async Task<CommandResult> CommandAsync(string command, object payload, CancellationToken token = default)
        {
            if (command != "login") return new(true);
            LoginStarted = true;
            await Task.Delay(Timeout.Infinite, token);
            return new(false);
        }
        public async Task CancelAsync()
        {
            CancelCalls++; CancelStarted = true; ActiveCancellations++;
            PeakCancellations = Math.Max(PeakCancellations, ActiveCancellations);
            try { await ReleaseCancel.Task; } finally { ActiveCancellations--; }
        }
        public Task<string> DiagnosticsAsync(CancellationToken token = default) => Task.FromResult("Offline navigation fixture");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class OrderedExitBackend(List<string> order) : IBackendClient
    {
        public Task<NativeSnapshot> SnapshotAsync(CancellationToken token = default)
        {
            order.Add("snapshot");
            return Task.FromResult(new NativeSnapshot(false, false, true, false, "", null));
        }
        public Task<CommandResult> CommandAsync(string command, object payload, CancellationToken token = default) => Task.FromResult(new CommandResult(true));
        public Task CancelAsync() { order.Add("cancel"); return Task.CompletedTask; }
        public Task<string> DiagnosticsAsync(CancellationToken token = default) => Task.FromResult("Fixture");
        public ValueTask DisposeAsync() { order.Add("backend-disposed"); return ValueTask.CompletedTask; }
    }
    private sealed class OrderedUpdateService(List<string> order) : IUpdateService
    {
        public UpdateStatus Status => new(UpdatePhase.Ready, "Update ready to install.", "0.1.0-beta.2");
        public bool HasPendingUpdate => true;
        public event EventHandler<UpdateStatus>? StatusChanged { add { } remove { } }
        public void Start() { }
        public Task CheckNowAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void PrepareApply(bool restart) { order.Add("update-apply"); if (restart) order.Add("update-restart"); }
        public void Dispose() { }
        public ValueTask DisposeAsync() { order.Add("update-stopped"); return ValueTask.CompletedTask; }
    }
    private static IEnumerable<DependencyObject> Flatten(DependencyObject root)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Flatten(child)) yield return nested;
        }
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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
    // Stands in for the backend's single mutation slot: an optimize held open answers a second
    // mutation with MUTATION_BUSY until a cancel releases it, as backend.mjs does.
    private sealed class RouteScanBackend(NativeSnapshot initial) : IBackendClient
    {
        private NativeSnapshot _snapshot = initial;
        private TaskCompletionSource<CommandResult>? _optimize;
        private bool _scanRan;
        public bool HoldOptimize { get; init; }
        public List<string> Commands { get; } = [];
        public int Snapshots { get; private set; }
        public int SnapshotsAfterScan { get; private set; }
        public bool AnsweredBusy { get; private set; }
        public bool OptimizeInFlight => _optimize is { Task.IsCompleted: false };
        // ExitGuard refuses to close on an owned or unreadable tunnel, which is correct. The
        // guard cases below are about the scan, so they settle the fixture before exiting.
        public void Settle() => _snapshot = new NativeSnapshot(false, false, true, true, _snapshot.Username, _snapshot.Route);
        public Task<NativeSnapshot> SnapshotAsync(CancellationToken token = default)
        {
            Snapshots++;
            if (_scanRan) SnapshotsAfterScan++;
            return Task.FromResult(_snapshot);
        }
        public Task<CommandResult> CommandAsync(string command, object payload, CancellationToken token = default)
        {
            Commands.Add(command);
            if (command == "optimize")
            {
                if (HoldOptimize)
                {
                    _optimize = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    return _optimize.Task;
                }
                _scanRan = true;
                return Task.FromResult(new CommandResult(true));
            }
            if (OptimizeInFlight) { AnsweredBusy = true; return Task.FromResult(new CommandResult(false, "MUTATION_BUSY", "busy")); }
            return Task.FromResult(new CommandResult(true));
        }
        public Task CancelAsync()
        {
            Commands.Add("cancel");
            _optimize?.TrySetResult(new CommandResult(false, "CANCELLED", "Operation cancelled."));
            return Task.CompletedTask;
        }
        public Task<string> DiagnosticsAsync(CancellationToken token = default) => Task.FromResult("fixture");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class AutoConnectBackend(NativeSnapshot initial) : IBackendClient
    {
        private NativeSnapshot _snapshot = initial;
        private TaskCompletionSource<CommandResult>? _optimize;
        public RouteInfo? OptimizedRoute { get; init; }
        public NativeSnapshot? ConnectSnapshot { get; init; }
        public CommandResult OptimizeResult { get; init; } = new(true);
        public CommandResult ConnectResult { get; init; } = new(true);
        public bool HoldOptimize { get; init; }
        public List<string> Commands { get; } = [];
        public int Cancels { get; private set; }
        public void Settle() => _snapshot = new NativeSnapshot(false, false, true, true, _snapshot.Username, _snapshot.Route);
        public Task<NativeSnapshot> SnapshotAsync(CancellationToken token = default) => Task.FromResult(_snapshot);
        public Task<CommandResult> CommandAsync(string command, object payload, CancellationToken token = default)
        {
            Commands.Add(command);
            if (command == "optimize")
            {
                if (HoldOptimize)
                {
                    _optimize = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    return _optimize.Task.WaitAsync(token);
                }
                if (OptimizeResult.Success && OptimizedRoute is not null)
                {
                    _snapshot = _snapshot with { Route = OptimizedRoute };
                    return Task.FromResult(OptimizeResult with { Route = OptimizedRoute });
                }
                return Task.FromResult(OptimizeResult);
            }
            if (command == "connect")
            {
                if (ConnectSnapshot is not null)
                    _snapshot = ConnectSnapshot;
                else if (ConnectResult.Success)
                    _snapshot = _snapshot with { Connected = true, TunnelActive = true, OwnedTunnelPresent = true };
                return Task.FromResult(ConnectResult);
            }
            if (command == "disconnect")
                _snapshot = _snapshot with { Connected = false, TunnelActive = false, OwnedTunnelPresent = false };
            return Task.FromResult(new CommandResult(true));
        }
        public Task CancelAsync()
        {
            Cancels++;
            _optimize?.TrySetResult(new CommandResult(false, "CAPTCHA_CANCELLED", "Operation cancelled."));
            return Task.CompletedTask;
        }
        public Task<string> DiagnosticsAsync(CancellationToken token = default) => Task.FromResult("fixture");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class DeferredMutationBackend : IBackendClient
    {
        private readonly object _sync = new();
        private TaskCompletionSource<NativeSnapshot>? _held;
        public NativeSnapshot Current { get; set; } = new(false, false, true, true, "fixture", null);
        public List<string> Commands { get; } = [];
        public TaskCompletionSource<CommandResult> ConnectGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<NativeSnapshot>? HeldSnapshot { get { lock (_sync) return _held; } }
        public void HoldNextSnapshot() { lock (_sync) _held = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        public void CompleteHeld(NativeSnapshot snapshot) { lock (_sync) { _held?.TrySetResult(snapshot); _held = null; } }
        public Task<NativeSnapshot> SnapshotAsync(CancellationToken token = default)
        {
            lock (_sync) { if (_held is not null) return _held.Task; return Task.FromResult(Current); }
        }
        public Task<CommandResult> CommandAsync(string command, object payload, CancellationToken token = default)
        {
            lock (_sync) Commands.Add(command);
            return command == "connect" ? ConnectGate.Task : Task.FromResult(new CommandResult(true));
        }
        public Task CancelAsync() => Task.CompletedTask;
        public Task<string> DiagnosticsAsync(CancellationToken token = default) => Task.FromResult("fixture");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class CaptchaBackend : IBackendClient
    {
        public Task<NativeSnapshot> SnapshotAsync(CancellationToken token = default) => Task.FromResult(new NativeSnapshot(false, false, true, false, "", null));
        public Task<CommandResult> CommandAsync(string command, object payload, CancellationToken token = default) =>
            Task.FromResult(new CommandResult(false, "HUMAN_VERIFICATION_REQUIRED", "Verification required")
            { CaptchaUrl = "https://vpn-api.proton.me/core/v4/captcha?Token=fixture-token" });
        public Task CancelAsync() => Task.CompletedTask;
        public Task<string> DiagnosticsAsync(CancellationToken token = default) => Task.FromResult("fixture");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class SnapshotCountingBackend : IBackendClient
    {
        public int Snapshots { get; private set; }
        public Task<NativeSnapshot> SnapshotAsync(CancellationToken token = default)
        {
            Snapshots++;
            return Task.FromResult(new NativeSnapshot(false, false, true, true, "fixture", null));
        }
        public Task<CommandResult> CommandAsync(string command, object payload, CancellationToken token = default) => Task.FromResult(new CommandResult(true));
        public Task CancelAsync() => Task.CompletedTask;
        public Task<string> DiagnosticsAsync(CancellationToken token = default) => Task.FromResult("fixture");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class SnapshotBackend(NativeSnapshot snapshot) : IBackendClient
    {
        public Task<NativeSnapshot> SnapshotAsync(CancellationToken token = default) => Task.FromResult(snapshot);
        public Task<CommandResult> CommandAsync(string command, object payload, CancellationToken token = default) => Task.FromResult(new CommandResult(true));
        public Task CancelAsync() => Task.CompletedTask;
        public Task<string> DiagnosticsAsync(CancellationToken token = default) => Task.FromResult("Navigation fixture");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static void Pump(Func<bool> completed)
    {
        var end = DateTime.UtcNow.AddSeconds(8);
        while (!completed())
        {
            if (DateTime.UtcNow >= end) throw new TimeoutException("Navigation did not finish");
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }
}

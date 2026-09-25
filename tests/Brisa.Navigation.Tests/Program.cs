using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using Brisa.Models;
using System.Windows.Threading;
using Brisa;
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
            foreach (var name in new[] { "AccountView", "SettingsView" })
            {
                var type = typeof(MainWindow).Assembly.GetType("Brisa." + name);
                Check(type is not null && typeof(UserControl).IsAssignableFrom(type), name + " must be an inline UserControl, not a Window");
            }
            // Legacy installations persisted CloseToTray=false. X must still only hide.
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "settings.json"), "{\"Theme\":2,\"CloseToTray\":false}");
            var store = new SettingsStore(root, false);
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
                if (name == "Settings")
                {
                    ((RadioButton)page.FindName("LightTheme")).IsChecked = true;
                    Check(((TextBlock)page.FindName("VersionText")).Text.Contains(AppVersion.Current), "Settings shows the packaged Brisa version");
                    Check(!((Button)page.FindName("CheckUpdateButton")).IsEnabled, "isolated navigation never enables network update checks");
                }
                Click(back); Pump(() => host.Content is null);
                Check(home.IsVisible && !back.IsVisible, "Back restores Connection");
                Check(store.Current.Theme == AppTheme.Dark, "Back discards unsaved Settings");
            }
            Click((Button)main.FindName("SettingsButton")); Pump(() => host.Content is UserControl);
            var settings = (UserControl)host.Content;
            ((RadioButton)settings.FindName("LightTheme")).IsChecked = true;
            Click((Button)settings.FindName("SaveButton")); Pump(() => host.Content is null);
            Check(store.Current.Theme == AppTheme.Light && home.IsVisible, "Save applies settings and returns Home");
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
            var indicator = connecting.FindName("ConnectionProgress") as ProgressBar;
            var progressVisible = indicator is { IsVisible: true, IsIndeterminate: true, ActualWidth: > 0, ActualHeight: > 0 };
            var wasDisabled = !((Button)connecting.FindName("PrimaryButton")).IsEnabled;
            connecting.Close(); Pump(() => !connecting.IsVisible);
            Check(!connectBackend.Release.Task.IsCompleted && app.Windows.Cast<Window>().Contains(connecting),
                "X during Connect hides the window without cancelling the operation");
            connectBackend.Release.TrySetResult(); Pump(() => ((Button)connecting.FindName("PrimaryButton")).IsEnabled);
            connecting.Show();
            var stoppedAnimating = indicator is { IsIndeterminate: false, Visibility: Visibility.Hidden };
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
            Console.WriteLine($"{count} inline-navigation assertions passed");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { app.Shutdown(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); count++; }
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
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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

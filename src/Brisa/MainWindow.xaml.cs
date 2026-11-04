using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brisa.Models;
using Brisa.Security;
using Brisa.Services;

namespace Brisa;

public partial class MainWindow : Window
{
    private readonly IBackendClient _backend;
    private readonly SettingsStore _settings;
    private readonly string? _smokeOutput;
    private readonly IUpdateService? _updates;
    private readonly Action<string> _exitWarning;
    private readonly Action<string> _notice;
    private readonly Func<string, bool> _exitConfirm;
    // Último instante em que o instalador visível do fornecedor estava na tela, em ticks (0 =
    // nenhum). A UI escreve e a tarefa do prazo lê, então o valor é lido e escrito com Volatile:
    // um DateTime? de 16 bytes poderia ser lido pela metade.
    private long _installerStageSeenTicks;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private HomeState _state = new(ConnectionPhase.Loading, null);
    private CancellationTokenSource _lifetime = new();
    private bool _closing, _canClose, _explicitExit, _navigationBusy, _applyUpdateOnExit;
    private UserControl? _currentPage;
    private Task? _returnHomeTask;
    private bool _refreshing;
    private bool _nativeOperationMayBeActive;
    private bool _terminalBackendFailure;
    // Snapshot commit bookkeeping. Every snapshot-derived state write goes through one gate:
    // a stale response must never overwrite a newer operation's state, and an operation-owned
    // final refresh must be distinguishable from a background poll that merely looks current.
    private long _operationEpoch;
    private long _navigationEpoch;
    private long _requestSequence;
    private long _appliedSequence;
    private bool _operationOwnsUi;
    private readonly System.Windows.Threading.DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(10) };

    private readonly record struct SnapshotTicket(long Operation, long Navigation, long Sequence, bool FromOperation);

    // One definition of "a UI mutation is in flight", used by handlers as well as controls:
    // disabling a button does not stop a queued or directly raised event.
    private bool UiMutationBusy => _operationOwnsUi || _operation.CurrentCount == 0
        || _state.Phase is ConnectionPhase.Connecting or ConnectionPhase.Disconnecting or ConnectionPhase.Optimizing;

    private SnapshotTicket IssueTicket(bool fromOperation = false) =>
        new(_operationEpoch, _navigationEpoch, Interlocked.Increment(ref _requestSequence), fromOperation);

    private bool CanCommit(SnapshotTicket ticket)
    {
        if (_closing || _canClose) return false;
        if (ticket.Operation != _operationEpoch || ticket.Navigation != _navigationEpoch) return false;
        return ticket.FromOperation ? _operationOwnsUi : !_operationOwnsUi && _operation.CurrentCount != 0;
    }

    private bool ApplySnapshot(NativeSnapshot snapshot, SnapshotTicket ticket)
    {
        if (!CanCommit(ticket) || ticket.Sequence < _appliedSequence) return false;
        _appliedSequence = ticket.Sequence;
        SetState(HomeState.FromSnapshot(snapshot));
        return true;
    }

    private async Task<bool> RefreshSnapshotAsync(SnapshotTicket ticket, TimeSpan budget)
    {
        using var token = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        token.CancelAfter(budget);
        try
        {
            var snapshot = await _backend.SnapshotAsync(token.Token);
            return ApplySnapshot(snapshot, ticket);
        }
        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
        {
            // This budget expiring is our own deadline, not the window shutting down. The
            // transport sees a cancellable token here, so it reports cancellation rather than
            // TimeoutException; the deadline has to be classified on this side.
            if (CanCommit(ticket)) SetError(new TimeoutException(BackendClient.TimeoutMessage), _state.Snapshot);
            return false;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            // A stale failure must not replace a newer healthy state or stop its timer.
            if (CanCommit(ticket)) SetError(ex, _state.Snapshot);
            return false;
        }
    }

    private void BeginUiMutation()
    {
        _operationOwnsUi = true;
        _operationEpoch++;
    }

    private void EndUiMutation()
    {
        _operationEpoch++;
        _operationOwnsUi = false;
    }

    // A rejected final refresh must not leave the window stuck in a busy phase with every
    // control disabled, so the last known snapshot is republished instead.
    private void LeaveBusyPhase()
    {
        if (_state.Phase is not (ConnectionPhase.Connecting or ConnectionPhase.Disconnecting or ConnectionPhase.Optimizing)) return;
        SetState(_state.Snapshot is { } snapshot ? HomeState.FromSnapshot(snapshot) : new(ConnectionPhase.Disconnected, null));
    }

    public MainWindow(IBackendClient backend, SettingsStore settings, string? smokeOutput = null, IUpdateService? updates = null, Action<string>? exitWarning = null, Action<string>? notice = null, Func<string, bool>? exitConfirm = null)
    {
        InitializeComponent(); _backend = backend; _settings = settings; _smokeOutput = smokeOutput; _updates = updates;
        _exitWarning = exitWarning ?? (message => { MessageBox.Show(this, message, "Brisa", MessageBoxButton.OK, MessageBoxImage.Warning); });
        // Leaving with an unknown installer state is allowed, but only after the user says so:
        // the window may still be showing a vendor installer, and Discord was already closed.
        _exitConfirm = exitConfirm ?? (message => MessageBox.Show(this, message, "Exit Brisa", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);
        _notice = notice ?? (message => { MessageBox.Show(this, message, "Route", MessageBoxButton.OK, MessageBoxImage.Warning); });
        SetState(_state);
        _statusTimer.Tick += RefreshStatus;
        Closed += (_, _) => _statusTimer.Stop();
        Closing += OnClosing;
        // The timer is paused while hidden in the tray, so refresh once when the window comes back.
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) RefreshStatus(this, EventArgs.Empty); };
        PreviewKeyDown += OnNavigationKeyDown;
    }

    public async Task InitializeAsync(bool openAccount = true)
    {
        var ticket = IssueTicket();
        var accepted = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var snapshot = await _backend.SnapshotAsync(timeout.Token).WaitAsync(timeout.Token);
            accepted = ApplySnapshot(snapshot, ticket);
        }
        catch (Exception ex)
        {
            if (CanCommit(ticket)) SetError(ex, null);
        }
        // A logon launch stays on Home: the limited-access state says what is possible
        // instead of jumping the user into a sign-in they did not ask for. Onboarding also
        // waits for an accepted snapshot, so a stale or rejected one cannot open Account.
        if (openAccount && accepted && _state.RequiresSignIn && _currentPage is null && !_closing) OpenAccount();
        if (!_terminalBackendFailure) _statusTimer.Start();
        if (_smokeOutput is not null) await RunSmokeAsync(_smokeOutput);
    }

    private async void RefreshStatus(object? sender, EventArgs e)
    {
        if (_refreshing || _closing || !IsVisible || _currentPage is not null || UiMutationBusy ||
            _state.Phase is ConnectionPhase.Loading) return;
        _refreshing = true;
        try { await RefreshSnapshotAsync(IssueTicket(), TimeSpan.FromSeconds(20)); }
        finally { _refreshing = false; }
    }

    private void SetError(Exception error, NativeSnapshot? snapshot)
    {
        if (error is BackendUnavailableException) { _terminalBackendFailure = true; _statusTimer.Stop(); }
        SetState(new(ConnectionPhase.Error, snapshot, SafeMessage(error)));
    }

    private void SetState(HomeState state)
    {
        if (state.Snapshot?.HasOwnedTunnel == true) _nativeOperationMayBeActive = true;
        else if (_operation.CurrentCount != 0 && state.Phase is ConnectionPhase.Disconnected or ConnectionPhase.Blocked
            && state.Snapshot is { Reliable: true, HasOwnedTunnel: false, Stage: "idle" }) _nativeOperationMayBeActive = false;
        _state = state; StatusText.Text = state.StatusText; DetailText.Text = state.Detail ?? "";
        PrimaryButton.Content = state.PrimaryLabel;
        var busy = state.Phase is ConnectionPhase.Loading or ConnectionPhase.Connecting or ConnectionPhase.Disconnecting or ConnectionPhase.Optimizing;
        ConnectionProgress.IsIndeterminate = busy;
        ConnectionProgress.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
        UpdateActionAvailability();

        RouteText.Text = state.Snapshot?.Route is { } route ? $"{route.Country} · {route.Server}" : "Automatic";
        // Conectado é o único estado com acento: o resto fica neutro, e o Limited usa o escudo de
        // administrador porque um cadeado aberto lê como "livre", o oposto do que ele significa.
        var connected = state.Phase == ConnectionPhase.Connected;
        StatusIcon.Text = connected ? "\uE73E"
            : state.Phase == ConnectionPhase.Limited ? "\uEA18"
            : state.Phase == ConnectionPhase.Blocked ? "\uE783" : "\uE785";
        StatusBadge.SetResourceReference(Border.BackgroundProperty, connected ? "NativeStatusConnectedSurface" : "NativeIconSurface");
        StatusIcon.SetResourceReference(TextBlock.ForegroundProperty, connected ? "NativeStatusConnected" : "NativeForeground");
    }

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPage is not null || _navigationBusy || _closing) return;
        if (_state.StartupFailed) { RequestExit(); return; }
        if (UiMutationBusy || !await _operation.WaitAsync(0)) return;
        BeginUiMutation();
        try
        {
            if (_state.RequiresSignIn) { OpenAccount(); return; }
            var command = _state.Snapshot?.HasOwnedTunnel == true ? "disconnect" : "connect";
            if (command == "connect") _nativeOperationMayBeActive = true;
            SetState(_state with { Phase = command == "connect" ? ConnectionPhase.Connecting : ConnectionPhase.Disconnecting, Detail = null });
            var pending = ExecuteWithVerificationAsync(command, (token, method) => new { humanVerificationToken = token, humanVerificationMethod = method });
            await TrackProgressAsync(pending, _operationEpoch);
            var result = await pending;
            if (!result.Success) MessageBox.Show(this, result.Message ?? "The operation could not be completed.", "Brisa", MessageBoxButton.OK, MessageBoxImage.Warning);
            await RefreshSnapshotAsync(IssueTicket(fromOperation: true), TimeSpan.FromSeconds(20));
        }
        catch (OperationCanceledException) { if (!_closing) await RefreshSnapshotAsync(IssueTicket(fromOperation: true), TimeSpan.FromSeconds(20)); }
        catch (Exception ex) { if (CanCommit(IssueTicket(fromOperation: true))) SetError(ex, _state.Snapshot); }
        finally { EndUiMutation(); LeaveBusyPhase(); _operation.Release(); UpdateActionAvailability(); }
    }

    // Test seam only: production always runs the real hardened window.
    internal Func<VerificationChallenge, CancellationToken, Task<VerificationAnswer?>>? VerificationDialogFactory { get; init; }

    private async Task<VerificationAnswer?> ShowVerificationAsync(VerificationChallenge challenge, CancellationToken token)
    {
        var dialog = new VerificationWindow(challenge, token) { Owner = this };
        dialog.Show();
        return await dialog.Completion;
    }

    private async Task TrackProgressAsync(Task pending, long ownerEpoch)
    {
        while (!pending.IsCompleted && !_closing)
        {
            await Task.WhenAny(pending, Task.Delay(400));
            if (pending.IsCompleted || _closing) break;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                var progress = await _backend.CommandAsync("progress", new { }, timeout.Token);
                var detail = progress.Stage switch
                {
                    "preparing-profile" => "Preparing your VPN route…",
                    "installing-wiresock" => "Preparing WireSock SDK. Complete or cancel the visible installer if it opens.",
                    "closing-discord" => "Closing the old Discord session…",
                    "starting-tunnel" => "Starting and settling the tunnel…",
                    "starting-discord" => "Reopening Discord…",
                    "verifying-route" => "Checking Discord’s route…",
                    "stopping-tunnel" => "Restoring the normal connection…",
                    _ => null
                };
                // Advisory only, and only while the operation that started it still owns the UI:
                // a late poll must not repaint a state the user has already left.
                Volatile.Write(ref _installerStageSeenTicks, progress.Stage == "installing-wiresock" ? DateTime.UtcNow.Ticks : 0L);
                if (detail is not null && !pending.IsCompleted && !_closing
                    && _operationOwnsUi && _operationEpoch == ownerEpoch) SetState(_state with { Detail = detail });
            }
            catch { /* Progress is advisory; the command still controls its outcome. */ }
        }
    }

    public async Task<CommandResult> ExecuteWithVerificationAsync(string command, Func<string?, string?, object> payloadFactory, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        // Distinguish our own deadline from a caller or lifetime cancellation, so a timeout can be
        // reported instead of vanishing as a silent refresh.
        var deadlineExpired = false;
        using var deadline = timeout.Token.Register(() => deadlineExpired = true);
        var ceilingExpired = false;
        var connectExpired = false;
        Volatile.Write(ref _installerStageSeenTicks, 0L);
        // O prazo de três minutos mede o tempo em que o app está trabalhando. Enquanto o
        // instalador visível do fornecedor está aberto, esse tempo não corre; o teto de trinta
        // minutos continua correndo e é o que impede uma janela ocupada para sempre.
        var startedAt = DateTime.UtcNow;
        var waited = TimeSpan.Zero;
        var lastTick = startedAt;
        // O laço tem o próprio CTS: ele pode terminar depois que este método retornar e descartar
        // `timeout`, e cancelar um CTS descartado lançaria em uma tarefa não observada.
        using var watchdog = new CancellationTokenSource();
        // Descartar não cancela: sem o Cancel no finally do try abaixo, o laço ainda pode passar
        // pela checagem e cancelar um `timeout` já descartado em uma tarefa não observada.
        _ = Task.Run(async () =>
        {
            while (!watchdog.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromMilliseconds(500), watchdog.Token); } catch { return; }
                var now = DateTime.UtcNow;
                var ticks = Volatile.Read(ref _installerStageSeenTicks);
                var seenAt = ticks == 0 ? (DateTime?)null : new DateTime(ticks, DateTimeKind.Utc);
                var installerActive = InstallerDeadline.IsInstallerActive(seenAt, now);
                if (!installerActive) waited += now - lastTick;
                lastTick = now;
                var expiry = InstallerDeadline.Decide(startedAt, waited, now, installerActive);
                if (expiry == InstallerDeadline.Expiry.None) continue;
                if (watchdog.IsCancellationRequested) return;
                ceilingExpired = expiry == InstallerDeadline.Expiry.Ceiling;
                connectExpired = expiry == InstallerDeadline.Expiry.Connect;
                timeout.Cancel();
                return;
            }
        });
        var presented = false;
        string? token = null, method = null;
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var result = await _backend.CommandAsync(command, payloadFactory(token, method), timeout.Token);
                if (result.Success || result.CaptchaUrl is null) return result;
                if (!VerificationPolicy.TryParseChallenge(result.CaptchaUrl, out var challenge) || challenge is null)
                    return result with { Message = "The verification address was rejected." };
                VerificationAnswer? answer;
                try
                {
                    presented = true;
                    answer = VerificationDialogFactory is null
                        ? await ShowVerificationAsync(challenge, timeout.Token)
                        : await VerificationDialogFactory(challenge, timeout.Token);
                }
                catch (VerificationUnavailableException)
                {
                    // A window that never started is not a cancelled verification.
                    await _backend.CancelAsync();
                    return new(false, "VERIFICATION_UNAVAILABLE", "The verification window could not start. Install or repair the WebView2 Runtime, then try again.");
                }
                if (answer is null) throw new OperationCanceledException(timeout.Token);
                token = answer.Token; method = answer.Method;
            }
            return new(false, "VERIFICATION_LIMIT", "Verification could not be completed after two retries.");
        }
        catch (OperationCanceledException) when (deadlineExpired && ceilingExpired)
        {
            // The installer never closed. Do not let this look like an ordinary cancel: say what
            // is on screen and leave the vendor process alone, since the backend waits for it.
            // Only an acknowledgement is asked for: the full cancel waits for idle, which cannot
            // happen while the installer is open, so it would block the message for 45 seconds.
            using var ack = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await _backend.CommandAsync("cancel", new { }, ack.Token); } catch { }
            return new(false, "SDK_INSTALLER_STILL_OPEN", "The WireSock installer is still open. Finish or cancel it in the Windows installer window, then try again.");
        }
        catch (OperationCanceledException) when (deadlineExpired && connectExpired)
        {
            // The app spent its working budget and gave up. The user did not cancel, so this must
            // not read as a quiet refresh.
            try { await _backend.CancelAsync(); } catch { }
            return new(false, "CONNECT_TIMEOUT", "Connecting took too long and was stopped. Check the connection status, then try again.");
        }
        catch (OperationCanceledException) when (deadlineExpired && presented)
        {
            await _backend.CancelAsync();
            return new(false, "VERIFICATION_TIMEOUT", "Verification timed out. Try again.");
        }
        catch (OperationCanceledException) { await _backend.CancelAsync(); throw; }
        finally { watchdog.Cancel(); }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_navigationBusy || _closing || UiMutationBusy) return;
        var view = new SettingsView(_settings, _updates, RequestUpdateRestart, ShowAdvancedAsync);
        view.CloseRequested += async (_, _) => await ReturnHomeAsync();
        ShowPage(view, "Settings");
    }
    private void Account_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPage is not null || _navigationBusy || _closing || UiMutationBusy || !AccountButton.IsEnabled) return;
        OpenAccount();
    }
    private void OpenAccount() => ShowPage(new AccountView(_backend, this, _state.Snapshot), "Account");
    private void ShowPage(UserControl page, string title)
    {
        // Any page change invalidates snapshots issued for the previous surface.
        _navigationEpoch++;
        PageTransition.Stop(HomeContent);
        _currentPage = page; PageHost.Content = page;
        PageTitle.Text = title;
        HomeContent.Visibility = HomeActions.Visibility = Visibility.Collapsed;
        PageHost.Visibility = BackButton.Visibility = Visibility.Visible;
        PageTransition.Show(PageHost);
        BackButton.Focus();
    }
    private async void Back_Click(object sender, RoutedEventArgs e) => await ReturnHomeAsync();
    private async void OnNavigationKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (_currentPage is null || !(key == Key.Escape || key == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt)) return;
        e.Handled = true;
        await ReturnHomeAsync();
    }
    private Task ReturnHomeAsync()
    {
        if (_returnHomeTask is { IsCompleted: false }) return _returnHomeTask;
        return _returnHomeTask = ReturnHomeCoreAsync();
    }
    private async Task ReturnHomeCoreAsync()
    {
        if (_currentPage is null || _navigationBusy || _closing) return;
        var page = _currentPage;
        _navigationBusy = true; BackButton.IsEnabled = false; page.IsEnabled = false;
        UpdateActionAvailability();
        try
        {
            if (page is IAsyncDisposable disposable) await disposable.DisposeAsync();
            if (_closing) return;
            PageTransition.Stop(PageHost);
            PageHost.Content = null; _currentPage = null;
            PageHost.Visibility = BackButton.Visibility = Visibility.Collapsed;
            HomeContent.Visibility = HomeActions.Visibility = Visibility.Visible;
            PageTransition.Show(HomeContent);
            PageTitle.Text = "Connection";
            _navigationEpoch++;
            await RefreshSnapshotAsync(IssueTicket(), TimeSpan.FromSeconds(5));
        }
        catch { if (!_closing) MessageBox.Show(this, "The account operation is still stopping. Please try going back again.", "Brisa", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally
        {
            _navigationBusy = false; BackButton.IsEnabled = true; UpdateActionAvailability();
            if (_currentPage is null && !_closing) (page is SettingsView ? SettingsButton : AccountButton).Focus();
        }
    }
    private void UpdateActionAvailability()
    {
        var available = !_navigationBusy && !_closing;
        PrimaryButton.IsEnabled = available && _state.PrimaryEnabled && !UiMutationBusy;
        RouteButton.IsEnabled = available && CanChooseRoute;
        AccountButton.IsEnabled = available && !UiMutationBusy;
        // Settings must not be reachable mid-mutation, but it has to come back afterwards.
        SettingsButton.IsEnabled = available && !UiMutationBusy;
    }

    // One eligibility definition shared by the control and the handler: a queued or directly
    // raised event can still fire while the button is disabled.
    private bool CanChooseRoute => !_state.RequiresSignIn && _state.Phase == ConnectionPhase.Disconnected
        && _state.Snapshot is { ExternalTunnel: false, Reliable: true, HasOwnedTunnel: false };

    private async void Route_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPage is not null || _navigationBusy || _closing || !CanChooseRoute) return;
        var dialog = new RouteWindow(this) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        // The modal pumps the dispatcher, so eligibility can change while it is open. The
        // selection is never queued or replayed later; the user is told instead.
        if (_currentPage is not null || _navigationBusy || _closing || !CanChooseRoute)
        { _notice("The connection state changed while choosing a route. Your selection was not applied. Try again when Brisa is idle."); return; }
        if (!await _operation.WaitAsync(0))
        { _notice("The connection state changed while choosing a route. Your selection was not applied. Try again when Brisa is idle."); return; }
        BeginUiMutation();
        try
        {
            // Optimization can take minutes, so the window says so instead of looking idle.
            // The detail stays generic: the backend does not promise a useful optimize stage.
            SetState(new HomeState(ConnectionPhase.Optimizing, _state.Snapshot, "Choosing the best route for your selection…"));
            var pending = ExecuteWithVerificationAsync("optimize", (token, method) => new { country = dialog.SelectedCountry, humanVerificationToken = token, humanVerificationMethod = method });
            await TrackProgressAsync(pending, _operationEpoch);
            var result = await pending;
            if (!result.Success) MessageBox.Show(this, result.Message ?? "Route optimization failed.", "Route", MessageBoxButton.OK, MessageBoxImage.Warning);
            await RefreshSnapshotAsync(IssueTicket(fromOperation: true), TimeSpan.FromSeconds(20));
        }
        catch (OperationCanceledException) { if (!_closing) await RefreshSnapshotAsync(IssueTicket(fromOperation: true), TimeSpan.FromSeconds(20)); }
        catch (Exception ex) { MessageBox.Show(this, SafeMessage(ex), "Route", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { EndUiMutation(); LeaveBusyPhase(); _operation.Release(); UpdateActionAvailability(); }
    }
    /// <summary>Test seam: the real app shows a modal dialog, fixtures record the request.</summary>
    internal Action<AdvancedWindow>? PresentAdvanced { get; set; }

    private async void ShowAdvancedAsync()
    {
        // The button lives on the Settings page, so an open page is the only context it exists
        // in: refusing because one is open made the button dead. Navigating, closing and a
        // running UI mutation still refuse, because a dialog opened mid-transition or mid-connect
        // would work from a snapshot that no longer matches.
        if (_navigationBusy || _closing || UiMutationBusy) return;
        var dialog = new AdvancedWindow(_backend, _state.Snapshot, ImportConfigAsync) { Owner = this };
        (PresentAdvanced ?? (window => window.ShowDialog()))(dialog);
        await RefreshSnapshotAsync(IssueTicket(), TimeSpan.FromSeconds(20));
    }

    /// <summary>
    /// Advanced's import runs through the same UI mutation gate as Connect, so a dialog opened
    /// from a stale snapshot cannot issue a command mid-connect.
    /// </summary>
    internal async Task<CommandResult> ImportConfigAsync(string path)
    {
        const string busy = "Another operation is still running. Wait for it to finish and try importing again.";
        if (_closing || UiMutationBusy) return new(false, "UI_BUSY", busy);
        if (!await _operation.WaitAsync(0)) return new(false, "UI_BUSY", busy);
        BeginUiMutation();
        try
        {
            if (_closing) return new(false, "UI_BUSY", busy);
            return await _backend.CommandAsync("importConfig", new { path }, _lifetime.Token);
        }
        finally { EndUiMutation(); _operation.Release(); UpdateActionAvailability(); }
    }

    private async Task RunSmokeAsync(string output)
    {
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (ActualWidth < 440 || ActualHeight < 520 || PrimaryButton is null || RouteButton is null) throw new InvalidOperationException("Loaded-window assertions failed.");
        var directory = Path.GetDirectoryName(output); if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        void Capture(Window view, string file)
        {
            view.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(view);
            var bitmap = new RenderTargetBitmap((int)(view.ActualWidth * dpi.DpiScaleX), (int)(view.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(view); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(file); encoder.Save(stream);
        }
        Capture(this, output);
        foreach (var button in new[] { SettingsButton, AccountButton })
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            if (_currentPage is null || System.Windows.Application.Current.Windows.Count != 1 || HomeContent.IsVisible)
                throw new InvalidOperationException("Inline navigation must use the same window.");
            Capture(this, Path.Combine(directory ?? ".", Path.GetFileNameWithoutExtension(output) + "-" + _currentPage.GetType().Name + ".png"));
            await ReturnHomeAsync();
        }
        var surfaces = new Window[] { new RouteWindow(this), new AdvancedWindow(_backend, _state.Snapshot) };
        foreach (var view in surfaces)
        {
            view.Owner = this; view.Show();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            if (view.ActualWidth < 200 || view.ActualHeight < 100) throw new InvalidOperationException("Secondary window failed to load.");
            Capture(view, Path.Combine(directory ?? ".", Path.GetFileNameWithoutExtension(output) + "-" + view.GetType().Name + ".png"));
            view.Close();
        }
        RequestExit();
    }
    public void RequestExit() { _explicitExit = true; Close(); }
    public void RequestUpdateRestart()
    {
        if (_updates?.HasPendingUpdate != true || _closing) return;
        _applyUpdateOnExit = true;
        _explicitExit = true;
        Close();
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_canClose) return;
        PageTransition.Stop(PageHost);
        PageTransition.Stop(HomeContent);
        e.Cancel = true;
        if (!_explicitExit && _smokeOutput is null &&
            !(_state.StartupFailed && _terminalBackendFailure && !_nativeOperationMayBeActive))
        {
            if (_currentPage is AccountView || _returnHomeTask is { IsCompleted: false }) await ReturnHomeAsync();
            if (!_closing && !_canClose) Hide();
            return;
        }
        if (_closing) return;
        _closing = true;
        var mayBeActive = _nativeOperationMayBeActive || _state.Snapshot?.HasOwnedTunnel == true || _state.Phase == ConnectionPhase.Connecting;
        _lifetime.Cancel();
        var acquired = false;
        try
        {
            if (_currentPage is IAsyncDisposable disposable) await disposable.DisposeAsync();
            // ExitGuard performs the final native cancel only after active work has joined.
            acquired = await _operation.WaitAsync(TimeSpan.FromSeconds(15));
            if (!acquired) throw new InvalidOperationException("The current operation is still stopping. Please try exiting again.");
            var decision = await ExitGuard.DecideAsync(_backend, mayBeActive);
            var safeToApplyUpdate = decision.SafeToApplyUpdate;
            if (decision.Warning is { } warning && !_exitConfirm(warning))
            {
                _closing = false; _explicitExit = false; _applyUpdateOnExit = false;
                _lifetime.Dispose(); _lifetime = new();
                return;
            }
            await _backend.DisposeAsync();
            if (_updates is not null) await _updates.DisposeAsync();
            if (safeToApplyUpdate && _updates?.HasPendingUpdate == true)
            {
                // The updater process is launched only after account work, tunnel
                // ownership checks, and backend disposal have all completed.
                try { _updates?.PrepareApply(restart: _applyUpdateOnExit); } catch { }
            }
            _canClose = true;
            _ = Dispatcher.BeginInvoke(Close);
        }
        catch (Exception ex)
        {
            _closing = false; _explicitExit = false; _applyUpdateOnExit = false;
            _lifetime.Dispose(); _lifetime = new();
            _exitWarning(ex is BackendUnavailableException
                ? "The native service stopped while a connection may still be active. Brisa cannot confirm a safe disconnect."
                : ex is InvalidOperationException ? ex.Message : "The service has not finished stopping. Please try exiting again.");
        }
        finally { if (acquired) _operation.Release(); }
    }

    private string SafeMessage(Exception ex) => ex switch
    {
        TimeoutException => BackendClient.TimeoutMessage,
        InvalidDataException => "The native service returned an unreadable response.",
        FileNotFoundException => "The native backend is not installed with this build.",
        BackendUnavailableException => _nativeOperationMayBeActive
            ? "The native service stopped while a connection may still be active. Brisa cannot confirm a safe disconnect."
            : "The native service stopped. You can exit Brisa and reopen it to retry.",
        _ => "The native service could not complete this action. Check the connection status before retrying."
    };
}

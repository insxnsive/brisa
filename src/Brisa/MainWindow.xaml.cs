using System.IO;
using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;
using System.Windows.Input;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
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
    private readonly SemaphoreSlim _operation = new(1, 1);
    private HomeState _state = new(ConnectionPhase.Loading, null);
    private CancellationTokenSource _lifetime = new();
    private bool _closing, _canClose, _explicitExit, _navigationBusy, _applyUpdateOnExit;
    private UserControl? _currentPage;
    private Task? _returnHomeTask;
    private bool _refreshing;
    private bool _nativeOperationMayBeActive;
    private bool _terminalBackendFailure;
    private readonly System.Windows.Threading.DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(10) };

    public MainWindow(IBackendClient backend, SettingsStore settings, string? smokeOutput = null, IUpdateService? updates = null, Action<string>? exitWarning = null)
    {
        InitializeComponent(); _backend = backend; _settings = settings; _smokeOutput = smokeOutput; _updates = updates;
        _exitWarning = exitWarning ?? (message => { MessageBox.Show(this, message, "Brisa", MessageBoxButton.OK, MessageBoxImage.Warning); });
        SetState(_state);
        _statusTimer.Tick += RefreshStatus;
        Closed += (_, _) => _statusTimer.Stop();
        Closing += OnClosing;
        PreviewKeyDown += OnNavigationKeyDown;
    }

    public async Task InitializeAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var snapshot = await _backend.SnapshotAsync(timeout.Token).WaitAsync(timeout.Token);
            if (_closing || _canClose) return;
            SetState(HomeState.FromSnapshot(snapshot));
        }
        catch (Exception ex)
        {
            if (_closing || _canClose) return;
            SetError(ex, null);
        }
        if (_state.RequiresSignIn && _currentPage is null && !_closing) OpenAccount();
        _statusTimer.Start();
        if (_smokeOutput is not null) await RunSmokeAsync(_smokeOutput);
    }

    private async void RefreshStatus(object? sender, EventArgs e)
    {
        if (_refreshing || _closing || !IsVisible || _currentPage is not null ||
            _state.Phase is ConnectionPhase.Loading or ConnectionPhase.Connecting or ConnectionPhase.Disconnecting) return;
        _refreshing = true;
        var before = _state;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var snapshot = await _backend.SnapshotAsync(timeout.Token);
            if (!_closing && _currentPage is null && ReferenceEquals(before, _state)) SetState(HomeState.FromSnapshot(snapshot));
        }
        catch (Exception ex)
        {
            if (!_closing && ReferenceEquals(before, _state)) SetError(ex, _state.Snapshot);
        }
        finally { _refreshing = false; }
    }

    private void SetError(Exception error, NativeSnapshot? snapshot)
    {
        if (error is BackendUnavailableException) _terminalBackendFailure = true;
        SetState(new(ConnectionPhase.Error, snapshot, SafeMessage(error)));
    }

    private void SetState(HomeState state)
    {
        if (state.Snapshot?.HasOwnedTunnel == true) _nativeOperationMayBeActive = true;
        else if (_operation.CurrentCount != 0 && state.Phase is ConnectionPhase.Disconnected or ConnectionPhase.Blocked
            && state.Snapshot is { Reliable: true, HasOwnedTunnel: false }) _nativeOperationMayBeActive = false;
        _state = state; StatusText.Text = state.StatusText; DetailText.Text = state.Detail ?? "";
        PrimaryButton.Content = state.PrimaryLabel;
        var busy = state.Phase is ConnectionPhase.Loading or ConnectionPhase.Connecting or ConnectionPhase.Disconnecting;
        ConnectionProgress.IsIndeterminate = busy;
        ConnectionProgress.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
        UpdateActionAvailability();

        RouteText.Text = state.Snapshot?.Route is { } route ? $"{route.Country} · {route.Server}" : "Automatic";
        StatusIcon.Text = state.Phase == ConnectionPhase.Connected ? "\uE73E" : state.Phase == ConnectionPhase.Blocked ? "\uE783" : "\uE785";
    }

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPage is not null || _navigationBusy || _closing) return;
        if (_state.StartupFailed) { RequestExit(); return; }
        if (!await _operation.WaitAsync(0)) return;
        try
        {
            if (_state.RequiresSignIn) { OpenAccount(); return; }
            var command = _state.Snapshot?.HasOwnedTunnel == true ? "disconnect" : "connect";
            if (command == "connect") _nativeOperationMayBeActive = true;
            SetState(_state with { Phase = command == "connect" ? ConnectionPhase.Connecting : ConnectionPhase.Disconnecting, Detail = null });
            var pending = ExecuteWithVerificationAsync(command, (token, method) => new { humanVerificationToken = token, humanVerificationMethod = method });
            await TrackProgressAsync(pending);
            var result = await pending;
            if (!result.Success) MessageBox.Show(this, result.Message ?? "The operation could not be completed.", "Brisa", MessageBoxButton.OK, MessageBoxImage.Warning);
            SetState(HomeState.FromSnapshot(await _backend.SnapshotAsync()));
        }
        catch (OperationCanceledException) { if (!_closing) { try { SetState(HomeState.FromSnapshot(await _backend.SnapshotAsync())); } catch (Exception ex) { SetError(ex, _state.Snapshot); } } }
        catch (Exception ex) { SetError(ex, _state.Snapshot); }
        finally { _operation.Release(); }
    }

    private async Task TrackProgressAsync(Task pending)
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
                    "closing-discord" => "Closing the old Discord session…",
                    "starting-tunnel" => "Starting and settling the tunnel…",
                    "starting-discord" => "Reopening Discord…",
                    "verifying-route" => "Checking Discord’s route…",
                    "stopping-tunnel" => "Restoring the normal connection…",
                    _ => null
                };
                if (detail is not null && !pending.IsCompleted && !_closing) SetState(_state with { Detail = detail });
            }
            catch { /* Progress is advisory; the command still controls its outcome. */ }
        }
    }

    public async Task<CommandResult> ExecuteWithVerificationAsync(string command, Func<string?, string?, object> payloadFactory, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        string? token = null, method = null;
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var result = await _backend.CommandAsync(command, payloadFactory(token, method), timeout.Token);
                if (result.Success || result.CaptchaUrl is null) return result;
                if (!VerificationPolicy.TryParseChallenge(result.CaptchaUrl, out var challenge) || challenge is null)
                    return result with { Message = "The verification address was rejected." };
                var dialog = new VerificationWindow(challenge, timeout.Token) { Owner = this };
                dialog.Show();
                var answer = await dialog.Completion;
                if (answer is null) throw new OperationCanceledException(timeout.Token);
                token = answer.Token; method = answer.Method;
            }
            return new(false, "VERIFICATION_LIMIT", "Verification could not be completed after two retries.");
        }
        catch (OperationCanceledException) { await _backend.CancelAsync(); throw; }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPage is not null || _navigationBusy || _closing) return;
        var view = new SettingsView(_settings, _updates, RequestUpdateRestart, () => Advanced_Click(this, new RoutedEventArgs()));
        view.CloseRequested += async (_, _) => await ReturnHomeAsync();
        ShowPage(view, "Settings");
    }
    private void Account_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPage is not null || _navigationBusy || _closing || !AccountButton.IsEnabled) return;
        OpenAccount();
    }
    private void OpenAccount() => ShowPage(new AccountView(_backend, this, _state.Snapshot), "Account");
    private void ShowPage(UserControl page, string title)
    {
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
            using var refresh = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            refresh.CancelAfter(TimeSpan.FromSeconds(5));
            try { SetState(HomeState.FromSnapshot(await _backend.SnapshotAsync(refresh.Token))); }
            catch (Exception ex) { if (!_closing) SetError(ex, _state.Snapshot); }
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
        PrimaryButton.IsEnabled = available && _state.PrimaryEnabled;
        RouteButton.IsEnabled = available && !_state.RequiresSignIn && _state.Phase == ConnectionPhase.Disconnected && _state.Snapshot is { ExternalTunnel: false, Reliable: true, HasOwnedTunnel: false };
        AccountButton.IsEnabled = available && _state.Phase is not (ConnectionPhase.Connecting or ConnectionPhase.Disconnecting);
        SettingsButton.IsEnabled = available;
    }
    private async void Route_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RouteWindow(this) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (!await _operation.WaitAsync(0)) return;
        try
        {
            var result = await ExecuteWithVerificationAsync("optimize", (token, method) => new { country = dialog.SelectedCountry, humanVerificationToken = token, humanVerificationMethod = method });
            if (!result.Success) MessageBox.Show(this, result.Message ?? "Route optimization failed.", "Route", MessageBoxButton.OK, MessageBoxImage.Warning);
            SetState(HomeState.FromSnapshot(await _backend.SnapshotAsync()));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { MessageBox.Show(this, SafeMessage(ex), "Route", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { _operation.Release(); }
    }
    private async void Advanced_Click(object sender, RoutedEventArgs e)
    {
        new AdvancedWindow(_backend, _state.Snapshot) { Owner = this }.ShowDialog();
        try { SetState(HomeState.FromSnapshot(await _backend.SnapshotAsync())); } catch { }
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
            var safeToApplyUpdate = await ExitGuard.StopOwnedAsync(_backend, mayBeActive);
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
        FileNotFoundException => "The native backend is not installed with this build.",
        BackendUnavailableException => _nativeOperationMayBeActive
            ? "The native service stopped while a connection may still be active. Brisa cannot confirm a safe disconnect."
            : "The native service stopped. You can exit Brisa and reopen it to retry.",
        _ => "The native service could not complete this action. Check the connection status before retrying."
    };
}

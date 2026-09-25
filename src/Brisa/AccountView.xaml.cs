using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;
using Brisa.Models;
using Brisa.Services;

namespace Brisa;
public partial class AccountView : UserControl, IAsyncDisposable
{
    private readonly IBackendClient _backend;
    private readonly MainWindow _main;
    private readonly Action<Uri> _openExternal;
    private NativeSnapshot? _snapshot;
    private readonly CancellationTokenSource _lifetime = new();
    private Task _pending = Task.CompletedTask;
    private Task? _leaveTask;
    private bool _closed;
    public AccountView(IBackendClient backend, MainWindow main, NativeSnapshot? snapshot, Action<Uri>? openExternal = null)
    {
        InitializeComponent(); _backend = backend; _main = main; _snapshot = snapshot;
        _openExternal = openExternal ?? (uri => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }));
        Render();
    }
    private void Render()
    {
        var signedIn = _snapshot?.SignedIn == true;
        SignedOutPanel.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        SignedInPanel.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        RegistrationPanel.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        if (signedIn) Subtitle.Text = "Your Proton account.";
        UsernameText.Text = _snapshot?.Username ?? "";
        ActionButton.Content = signedIn ? "Sign Out" : "Sign In";
        ActionButton.IsEnabled = !_closed && (!signedIn || _snapshot is { HasOwnedTunnel: false, ExternalTunnel: false, Reliable: true });
    }
    private void SignUp_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        e.Handled = true;
        if (_closed) return;
        try { _openExternal(new Uri("https://account.protonvpn.com/signup?plan=free")); }
        catch
        {
            RegistrationHelp.Text = "Open https://account.protonvpn.com/signup?plan=free in your browser to create an account.";
            RegistrationHelp.Visibility = Visibility.Visible;
        }
    }
    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || !_pending.IsCompleted) return;
        _pending = RunActionAsync();
        await _pending;
    }
    private async Task RunActionAsync()
    {
        ActionButton.IsEnabled = false;
        try
        {
            CommandResult result;
            if (_snapshot?.SignedIn == true)
                result = await _main.ExecuteWithVerificationAsync("logout", (_, _) => new { }, _lifetime.Token);
            else
            {
                var username = UsernameBox.Text; var password = PasswordInput.Password; var twoFactor = TwoFactorInput.Password;
                try { result = await _main.ExecuteWithVerificationAsync("login", (token, method) => new { username, password, twoFactorCode = string.IsNullOrWhiteSpace(twoFactor) ? null : twoFactor, humanVerificationToken = token, humanVerificationMethod = method }, _lifetime.Token); }
                finally { PasswordInput.Clear(); TwoFactorInput.Clear(); password = ""; twoFactor = ""; }
                if (_closed) return;
                if (!result.Success && result.Code is "TWO_FACTOR_REQUIRED" or "2FA_REQUIRED")
                { TwoFactorPanel.Visibility = Visibility.Visible; Subtitle.Text = "Enter your password again with your two-factor code."; return; }
            }
            if (_closed) return;
            if (!result.Success) { MessageBox.Show(_main, result.Message ?? "The account operation failed.", "Account", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            using var refresh = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            refresh.CancelAfter(TimeSpan.FromSeconds(5));
            _snapshot = await _backend.SnapshotAsync(refresh.Token);
            if (!_closed) Render();
        }
        catch (OperationCanceledException) { }
        catch { if (!_closed) MessageBox.Show(_main, "The account service is unavailable.", "Account", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { if (!_closed) Render(); }
    }
    public ValueTask DisposeAsync() => new(_leaveTask ??= LeaveAsync());
    private async Task LeaveAsync()
    {
        _closed = true; _lifetime.Cancel();
        PasswordInput.Clear(); TwoFactorInput.Clear(); ActionButton.IsEnabled = false;
        // Join cancellation before Home can start another operation; a delayed
        // cancel acknowledgement must never cancel the next Connect request.
        await _pending;
        _lifetime.Dispose();
    }
}

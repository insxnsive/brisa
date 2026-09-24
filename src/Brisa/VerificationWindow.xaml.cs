using System.IO;
using System.Windows;
using Brisa.Security;
using Microsoft.Web.WebView2.Core;

namespace Brisa;

public partial class VerificationWindow : Window
{
    private readonly VerificationChallenge _challenge;
    private readonly CancellationToken _cancellationToken;
    private readonly TaskCompletionSource<VerificationAnswer?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _profile;
    private CancellationTokenRegistration _registration;
    private bool _closed;

    public VerificationWindow(VerificationChallenge challenge, CancellationToken cancellationToken)
    {
        InitializeComponent(); _challenge = challenge; _cancellationToken = cancellationToken;
        Loaded += OnLoaded; Closed += OnClosed;
    }

    public Task<VerificationAnswer?> Completion => _result.Task;
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _registration = _cancellationToken.Register(() => Dispatcher.BeginInvoke(Cancel));
            if (_cancellationToken.IsCancellationRequested) { Cancel(); return; }
            _profile = VerificationProfileStore.Create();
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: _profile);
            if (_closed || _cancellationToken.IsCancellationRequested) return;
            await Browser.EnsureCoreWebView2Async(environment);
            if (_closed || _cancellationToken.IsCancellationRequested) return;
            var core = Browser.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.DownloadStarting += (_, args) => { args.Cancel = true; args.Handled = true; };
            core.PermissionRequested += (_, args) => { args.State = CoreWebView2PermissionState.Deny; args.Handled = true; };
            core.NavigationStarting += (_, args) => { if (!VerificationPolicy.IsExactNavigation(args.Uri, _challenge)) args.Cancel = true; };
            core.WebMessageReceived += OnWebMessage;
            await core.AddScriptToExecuteOnDocumentCreatedAsync("""
                window.addEventListener('message', event => {
                  if (event.source !== window || event.origin !== window.location.origin) return;
                  const value = typeof event.data === 'string' ? event.data : JSON.stringify(event.data);
                  window.chrome.webview.postMessage(value);
                });
                """);
            core.Navigate(_challenge.Url.AbsoluteUri);
        }
        catch { _result.TrySetResult(null); if (!_closed) Close(); }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string serialized;
        try { serialized = e.TryGetWebMessageAsString(); } catch (InvalidOperationException) { return; }
        if (!VerificationPolicy.TryParseMessage(serialized, _challenge, e.Source, out var answer)) return;
        _result.TrySetResult(answer); Close();
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Cancel();
    private void Cancel() { _result.TrySetResult(null); if (!_closed) Close(); }
    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _registration.Dispose(); _result.TrySetResult(null); Browser.Dispose();
        if (_profile is not null) _ = VerificationProfileStore.DeleteWithRetryAsync(_profile);
    }
}

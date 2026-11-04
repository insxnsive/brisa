using System.Windows;
using Brisa.Models;
using Brisa.Services;
using Microsoft.Win32;

namespace Brisa;
public partial class AdvancedWindow : Window
{
    private readonly IBackendClient _backend;
    // Production passes MainWindow's gated import; standalone fixtures keep the default.
    private readonly Func<string, Task<CommandResult>>? _import;
    private readonly Action<string, string, bool> _notice;
    private readonly Func<string?> _pickFile;
    public AdvancedWindow(IBackendClient backend, NativeSnapshot? snapshot,
        Func<string, Task<CommandResult>>? import = null,
        Action<string, string, bool>? notice = null,
        Func<string?>? pickFile = null)
    {
        InitializeComponent(); _backend = backend; _import = import;
        _notice = notice ?? ((message, title, success) => MessageBox.Show(this, message, title, MessageBoxButton.OK, success ? MessageBoxImage.Information : MessageBoxImage.Warning));
        _pickFile = pickFile ?? PickFile;
        // Import and sign-in need no WireSock access, so an unelevated or foreign-tunnel
        // state must not hide the button. Only a native tunnel or a running operation does.
        var nativeBusy = snapshot is { Stage: not (null or "idle") };
        ImportButton.IsEnabled = !nativeBusy && snapshot is not { HasOwnedTunnel: true };
    }
    private string? PickFile()
    {
        var picker = new OpenFileDialog { Title = "Import WireGuard configuration", Filter = "WireGuard configuration (*.conf)|*.conf", CheckFileExists = true, Multiselect = false };
        return picker.ShowDialog(this) == true ? picker.FileName : null;
    }
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var path = _pickFile();
        if (path is null) return;
        ImportButton.IsEnabled = false;
        try
        {
            // The picker is modal, so the operation state can change while it is open; the
            // gate rechecks and refuses rather than replaying the choice later.
            var result = _import is null
                ? await _backend.CommandAsync("importConfig", new { path })
                : await _import(path);
            _notice(result.Message ?? (result.Success ? "Configuration imported." : "The configuration was rejected."), "Import", result.Success);
        }
        catch { _notice("The native service is unavailable.", "Import", false); }
        finally { ImportButton.IsEnabled = true; }
    }
    private async void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        DiagnosticsBox.Text = "Loading…";
        try { DiagnosticsBox.Text = await _backend.DiagnosticsAsync(); } catch { DiagnosticsBox.Text = "Diagnostics are unavailable."; }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

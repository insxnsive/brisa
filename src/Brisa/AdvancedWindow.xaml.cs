using System.Windows;
using Brisa.Models;
using Brisa.Services;
using Microsoft.Win32;

namespace Brisa;
public partial class AdvancedWindow : Window
{
    private readonly IBackendClient _backend;
    public AdvancedWindow(IBackendClient backend, NativeSnapshot? snapshot)
    {
        InitializeComponent(); _backend = backend;
        ImportButton.IsEnabled = snapshot is { HasOwnedTunnel: false, ExternalTunnel: false, Reliable: true };
    }
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "Import WireGuard configuration", Filter = "WireGuard configuration (*.conf)|*.conf", CheckFileExists = true, Multiselect = false };
        if (picker.ShowDialog(this) != true) return;
        ImportButton.IsEnabled = false;
        try { var result = await _backend.CommandAsync("importConfig", new { path = picker.FileName }); MessageBox.Show(this, result.Message ?? (result.Success ? "Configuration imported." : "The configuration was rejected."), "Import", MessageBoxButton.OK, result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning); }
        catch { MessageBox.Show(this, "The native service is unavailable.", "Import", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { ImportButton.IsEnabled = true; }
    }
    private async void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        DiagnosticsBox.Text = "Loading…";
        try { DiagnosticsBox.Text = await _backend.DiagnosticsAsync(); } catch { DiagnosticsBox.Text = "Diagnostics are unavailable."; }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

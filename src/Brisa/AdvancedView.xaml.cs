using System.Windows;
using System.Windows.Controls;
using Brisa.Models;
using Brisa.Services;
using Microsoft.Win32;

namespace Brisa;

/// <summary>
/// Advanced é uma página dentro da janela, não um diálogo: o AGENTS.md do repositório pede
/// navegação na mesma janela, e um diálogo por cima de outro diálogo era o único lugar do app
/// onde isso não valia. O botão Voltar do cabeçalho substitui o antigo Close.
/// </summary>
public partial class AdvancedView : UserControl
{
    private readonly IBackendClient _backend;
    // A importação em produção passa pelo mesmo portão de mutação da MainWindow; fixtures usam o padrão.
    private readonly Func<string, Task<CommandResult>>? _import;
    private readonly Action<string, string, bool> _notice;
    private readonly Func<string?> _pickFile;

    public AdvancedView(IBackendClient backend, NativeSnapshot? snapshot,
        Func<string, Task<CommandResult>>? import = null,
        Action<string, string, bool>? notice = null,
        Func<string?>? pickFile = null)
    {
        InitializeComponent(); _backend = backend; _import = import;
        _notice = notice ?? DefaultNotice;
        _pickFile = pickFile ?? PickFile;
        // Importar e entrar não precisam de WireSock, então um túnel de terceiro ou a falta de
        // elevação não podem esconder o botão. Só um túnel próprio ou uma operação em curso.
        var nativeBusy = snapshot is { Stage: not (null or "idle") };
        ImportButton.IsEnabled = !nativeBusy && snapshot is not { HasOwnedTunnel: true };
    }

    private void DefaultNotice(string message, string title, bool success)
    {
        var owner = Window.GetWindow(this);
        if (owner is null) MessageBox.Show(message, title, MessageBoxButton.OK,
            success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        else MessageBox.Show(owner, message, title, MessageBoxButton.OK,
            success ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private string? PickFile()
    {
        var picker = new OpenFileDialog { Title = "Import WireGuard configuration", Filter = "WireGuard configuration (*.conf)|*.conf", CheckFileExists = true, Multiselect = false };
        return picker.ShowDialog(Window.GetWindow(this)) == true ? picker.FileName : null;
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var path = _pickFile();
        if (path is null) return;
        ImportButton.IsEnabled = false;
        try
        {
            // O seletor de arquivo é modal, então o estado pode mudar enquanto ele está aberto; o
            // portão recheca e recusa em vez de repetir a escolha depois.
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
}

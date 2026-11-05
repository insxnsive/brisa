using System;
using Brisa.Localization;
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
public partial class AdvancedView : UserControl, IAsyncDisposable
{
    private bool _logReady;
    private readonly System.Windows.Threading.DispatcherTimer _noticeTimer;
    private readonly IBackendClient _backend;
    // A importação em produção passa pelo mesmo portão de mutação da MainWindow; fixtures usam o padrão.
    private readonly Func<string, Task<CommandResult>>? _import;
    private readonly Action<string, string, bool> _notice;
    private readonly Func<string?> _pickFile;
    private readonly IUpdateService? _updates;
    private readonly Action? _restartForUpdate;

    public AdvancedView(IBackendClient backend, NativeSnapshot? snapshot,
        Func<string, Task<CommandResult>>? import = null,
        Action<string, string, bool>? notice = null,
        Func<string?>? pickFile = null,
        IUpdateService? updates = null,
        Action? restartForUpdate = null)
    {
        _noticeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _noticeTimer.Tick += (_, _) => { _noticeTimer.Stop(); LogNotice.Text = ""; };
        InitializeComponent(); _backend = backend; _import = import;
        _notice = notice ?? DefaultNotice;
        _pickFile = pickFile ?? PickFile;
        _updates = updates; _restartForUpdate = restartForUpdate;
        // Importar e entrar não precisam de WireSock, então um túnel de terceiro ou a falta de
        // elevação não podem esconder o botão. Só um túnel próprio ou uma operação em curso.
        var nativeBusy = snapshot is { Stage: not (null or "idle") };
        ImportButton.IsEnabled = !nativeBusy && snapshot is not { HasOwnedTunnel: true };
        VersionText.Text = $"Brisa {AppVersion.Current}";
        if (_updates is null)
        {
            UpdateStatusText.Text = "Updates are unavailable in this isolated session.";
            CheckUpdateButton.IsEnabled = false;
        }
        else
        {
            _updates.StatusChanged += Updates_StatusChanged;
            ShowUpdateStatus(_updates.Status);
        }
    }

    private void Source_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/insxnsive/brisa") { UseShellExecute = true }); }
        catch { UpdateStatusText.Text = "Open github.com/insxnsive/brisa for source and license information."; }
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_updates is null) return;
        CheckUpdateButton.IsEnabled = false;
        try { await _updates.CheckNowAsync(); }
        catch { UpdateStatusText.Text = "Updates could not be checked. Try again later."; }
        finally { if (IsEnabled) CheckUpdateButton.IsEnabled = _updates.Status.Phase is not (UpdatePhase.Checking or UpdatePhase.Downloading); }
    }

    private void RestartUpdate_Click(object sender, RoutedEventArgs e) => _restartForUpdate?.Invoke();

    private void Updates_StatusChanged(object? sender, UpdateStatus status)
    {
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(() => ShowUpdateStatus(status)); return; }
        ShowUpdateStatus(status);
    }

    private void ShowUpdateStatus(UpdateStatus status)
    {
        UpdateStatusText.Text = status.Message;
        CheckUpdateButton.IsEnabled = status.Phase is not (UpdatePhase.Checking or UpdatePhase.Downloading);
        RestartUpdateButton.Visibility = status.Phase == UpdatePhase.Ready ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgress.Visibility = status.Phase == UpdatePhase.Downloading ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgress.IsIndeterminate = status.Phase == UpdatePhase.Downloading && status.Progress is null;
        UpdateProgress.Value = status.Progress ?? 0;
    }

    public ValueTask DisposeAsync()
    {
        if (_updates is not null) _updates.StatusChanged -= Updates_StatusChanged;
        return ValueTask.CompletedTask;
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

    // O log vem pronto do backend: a interface não monta linha, não redige nada e não reordena.
    private async void GenerateLog_Click(object sender, RoutedEventArgs e)
    {
        LogNotice.Text = "";
        LogBox.Text = LocalizationManager.Current["GeneratingLog"];
        KeepLogReady(false);
        try
        {
            var text = await _backend.DiagnosticsAsync();
            LogBox.Text = text;
            // O log abre no começo: a primeira linha é o aviso do que o arquivo não contém, e é
            // justamente o que a pessoa precisa ler antes de enviar.
            LogBox.CaretIndex = 0;
            LogBox.ScrollToHome();
            _logReady = !string.IsNullOrWhiteSpace(text);
        }
        catch
        {
            LogBox.Text = "The log could not be generated.";
            _logReady = false;
        }
        KeepLogReady(_logReady);
    }

    private void KeepLogReady(bool ready)
    {
        SaveLogButton.IsEnabled = ready;
        CopyLogButton.IsEnabled = ready;
    }

    // O nome padrão é o que o usuário anexa no e-mail de suporte: data e hora em UTC, sem espaços.
    private void SaveLog_Click(object sender, RoutedEventArgs e)
    {
        if (!_logReady) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = LocalizationManager.Current["SaveLogDialogTitle"],
            DefaultExt = ".txt",
            Filter = LocalizationManager.Current["SaveLogFilter"],
            FileName = SupportLogFileName(DateTime.UtcNow),
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            System.IO.File.WriteAllText(dialog.FileName, LogBox.Text, new System.Text.UTF8Encoding(false));
            Announce(LocalizationManager.Current["LogSaved"]);
        }
        catch { Announce("The log could not be saved."); }
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        if (!_logReady) return;
        try { System.Windows.Clipboard.SetText(LogBox.Text); Announce(LocalizationManager.Current["LogCopied"]); }
        catch { Announce("The log could not be copied."); }
    }

    /// Aviso passageiro: some sozinho, para não envelhecer na tela nem ser lido de novo por engano.
    private void Announce(string text)
    {
        LogNotice.Text = text;
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    public static string SupportLogFileName(DateTime utc) =>
        $"brisa-log-{utc:yyyyMMdd}-{utc:HHmm}.txt";


}

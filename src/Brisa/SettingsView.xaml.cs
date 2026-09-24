using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;
using Brisa.Services;

namespace Brisa;
public partial class SettingsView : UserControl, IAsyncDisposable
{
    private readonly SettingsStore _store;
    private readonly IUpdateService? _updates;
    private readonly Action? _restartForUpdate;
    private readonly Action? _openAdvanced;
    public event EventHandler? CloseRequested;
    public SettingsView(SettingsStore store, Action? openAdvanced = null)
        : this(store, null, null, openAdvanced) { }
    public SettingsView(SettingsStore store, IUpdateService? updates, Action? restartForUpdate, Action? openAdvanced = null)
    {
        InitializeComponent(); _store = store; _updates = updates; _restartForUpdate = restartForUpdate; _openAdvanced = openAdvanced;
        StartupBox.IsChecked = store.Current.StartWithWindows; TrayBox.IsChecked = store.Current.CloseToTray;
        SystemTheme.IsChecked = store.Current.Theme == AppTheme.System;
        LightTheme.IsChecked = store.Current.Theme == AppTheme.Light;
        DarkTheme.IsChecked = store.Current.Theme == AppTheme.Dark;
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
    private void Advanced_Click(object sender, RoutedEventArgs e) => _openAdvanced?.Invoke();
    private void Cancel_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var theme = DarkTheme.IsChecked == true ? AppTheme.Dark : LightTheme.IsChecked == true ? AppTheme.Light : AppTheme.System;
        try { _store.Save(new(theme, StartupBox.IsChecked == true, TrayBox.IsChecked == true)); }
        catch { MessageBox.Show(Window.GetWindow(this), "Settings could not be saved. Please try again.", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        CloseRequested?.Invoke(this, EventArgs.Empty);
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
}

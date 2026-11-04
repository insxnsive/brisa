using System.Windows;
using System.Windows.Controls;
using Brisa.Localization;
using Brisa.Services;

namespace Brisa;
public partial class SettingsView : UserControl
{
    private readonly SettingsStore _store;
    private readonly Action? _openAdvanced;
    public event EventHandler? CloseRequested;
    public SettingsView(SettingsStore store, Action? openAdvanced = null)
    {
        InitializeComponent(); _store = store; _openAdvanced = openAdvanced;
        StartupBox.IsChecked = store.Current.StartWithWindows;
        DetailsBox.IsChecked = store.Current.ShowDetails;
        SystemTheme.IsChecked = store.Current.Theme == AppTheme.System;
        LightTheme.IsChecked = store.Current.Theme == AppTheme.Light;
        DarkTheme.IsChecked = store.Current.Theme == AppTheme.Dark;
        LanguageBox.SelectedIndex = store.Current.Language switch
        {
            LocalizationManager.PortugueseBrazil => 2,
            LocalizationManager.English => 1,
            _ => 0,
        };
    }
    private void Advanced_Click(object sender, RoutedEventArgs e) => _openAdvanced?.Invoke();
    private void Cancel_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var theme = DarkTheme.IsChecked == true ? AppTheme.Dark : LightTheme.IsChecked == true ? AppTheme.Light : AppTheme.System;
        var language = (LanguageBox.SelectedItem as ComboBoxItem)?.Tag as string ?? LocalizationManager.System;
        try { _store.Save(new(theme, StartupBox.IsChecked == true, DetailsBox.IsChecked == true, language)); }
        catch
        {
            MessageBox.Show(Window.GetWindow(this), LocalizationManager.Current["SettingsSaveFailed"], LocalizationManager.Current["SettingsTitle"], MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // O idioma é aplicado aqui, na janela que já está aberta: nada é fechado, então o túnel não
        // passa por OnClosing nem pelo ExitGuard.
        LocalizationManager.Current.Apply(language);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}

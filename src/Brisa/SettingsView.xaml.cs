using System.Windows;
using System.Windows.Controls;
using Brisa.Localization;
using Brisa.Services;

namespace Brisa;
public partial class SettingsView : UserControl
{
    private readonly SettingsStore _store;
    private readonly Action<UserSettings>? _openAdvanced;
    public event EventHandler? CloseRequested;
    public SettingsView(SettingsStore store, Action<UserSettings>? openAdvanced = null, UserSettings? draft = null)
    {
        InitializeComponent(); _store = store; _openAdvanced = openAdvanced;
        var current = draft ?? store.Current;
        StartupBox.IsChecked = current.StartWithWindows;
        AutoConnectBox.IsChecked = current.AutoConnect;
        DetailsBox.IsChecked = current.ShowDetails;
        SystemTheme.IsChecked = current.Theme == AppTheme.System;
        LightTheme.IsChecked = current.Theme == AppTheme.Light;
        DarkTheme.IsChecked = current.Theme == AppTheme.Dark;
        LanguageBox.SelectedIndex = current.Language switch
        {
            LocalizationManager.PortugueseBrazil => 2,
            LocalizationManager.English => 1,
            _ => 0,
        };
    }
    // A navegação para Advanced não salva: este retrato dos controles volta com o usuário e
    // Cancel continua descartando, sem tocar no registro de inicialização nem no UAC.
    public UserSettings Draft
    {
        get
        {
            var theme = DarkTheme.IsChecked == true ? AppTheme.Dark : LightTheme.IsChecked == true ? AppTheme.Light : AppTheme.System;
            var language = (LanguageBox.SelectedItem as ComboBoxItem)?.Tag as string ?? LocalizationManager.System;
            return _store.Current with
            {
                Theme = theme,
                StartWithWindows = StartupBox.IsChecked == true,
                ShowDetails = DetailsBox.IsChecked == true,
                Language = language,
                AutoConnect = AutoConnectBox.IsChecked == true,
            };
        }
    }
    private void Advanced_Click(object sender, RoutedEventArgs e) => _openAdvanced?.Invoke(Draft);
    private void Cancel_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var theme = DarkTheme.IsChecked == true ? AppTheme.Dark : LightTheme.IsChecked == true ? AppTheme.Light : AppTheme.System;
        var language = (LanguageBox.SelectedItem as ComboBoxItem)?.Tag as string ?? LocalizationManager.System;
        try { _store.Save(_store.Current with { Theme = theme, StartWithWindows = StartupBox.IsChecked == true, ShowDetails = DetailsBox.IsChecked == true, Language = language, AutoConnect = AutoConnectBox.IsChecked == true }); }
        // The administrator prompt was refused, or this account cannot register the logon task: say which, and
        // stay on Settings with nothing changed. Anything else is an ordinary save failure.
        catch (StartupRegistrationCanceledException) { MessageBox.Show(Window.GetWindow(this), LocalizationManager.Current["StartupElevationCanceled"], LocalizationManager.Current["SettingsTitle"], MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        catch (StartupElevationNeedsAdminException) { MessageBox.Show(Window.GetWindow(this), LocalizationManager.Current["StartupElevationNeedsAdmin"], LocalizationManager.Current["SettingsTitle"], MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        catch { MessageBox.Show(Window.GetWindow(this), LocalizationManager.Current["SettingsSaveFailed"], LocalizationManager.Current["SettingsTitle"], MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        // O idioma é aplicado aqui, na janela que já está aberta: nada é fechado, então o túnel não
        // passa por OnClosing nem pelo ExitGuard.
        LocalizationManager.Current.Apply(language);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}

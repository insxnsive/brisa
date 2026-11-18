using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Brisa.Localization;
using Brisa.Services;

namespace Brisa;
public partial class SettingsView : UserControl
{
    private readonly SettingsStore _store;
    private readonly Action<UserSettings>? _openAdvanced;
    private readonly Func<bool>? _allowMotion;
    private readonly DispatcherTimer _savedTimer = new() { Interval = TimeSpan.FromSeconds(1.6) };
    public event EventHandler? CloseRequested;
    public event EventHandler? Saved;
    public SettingsView(SettingsStore store, Action<UserSettings>? openAdvanced = null, UserSettings? draft = null, Func<bool>? allowMotion = null)
    {
        InitializeComponent(); _store = store; _openAdvanced = openAdvanced; _allowMotion = allowMotion;
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
        CheckMotion.Attach(StartupBox, allowMotion);
        CheckMotion.Attach(AutoConnectBox, allowMotion);
        CheckMotion.Attach(DetailsBox, allowMotion);
        Loaded += (_, _) => UpdateThemeIndicator(false);
        SizeChanged += (_, _) => UpdateThemeIndicator(false);
        _savedTimer.Tick += (_, _) => { _savedTimer.Stop(); HideSaved(); };
    }
    // A navegaÃ§Ã£o para Advanced nÃ£o salva: este retrato dos controles volta com o usuÃ¡rio e
    // Cancel continua descartando, sem tocar no registro de inicializaÃ§Ã£o nem no UAC.
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
    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) UpdateThemeIndicator(true);
    }
    // Measure after UpdateLayout: the checked segment changes weight, so the pill target moves.
    private void UpdateThemeIndicator(bool animate)
    {
        var selected = SystemTheme.IsChecked == true ? SystemTheme : LightTheme.IsChecked == true ? LightTheme : DarkTheme;
        ThemeTrack.UpdateLayout();
        var fromX = ThemeIndicatorTranslate.X;
        var fromWidth = ThemeIndicator.Width;
        var x = selected.TranslatePoint(new System.Windows.Point(0, 0), ThemeTrack).X;
        var width = selected.ActualWidth;
        ThemeIndicator.Width = width;
        ThemeIndicatorTranslate.X = x;
        if (!animate || !StatusMotion.Allowed(_allowMotion)) return;
        var duration = TimeSpan.FromMilliseconds(220);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var slide = new DoubleAnimation(fromX, x, duration) { FillBehavior = FillBehavior.Stop, EasingFunction = ease };
        slide.Completed += (_, _) => ThemeIndicatorTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        var grow = new DoubleAnimation(fromWidth, width, duration) { FillBehavior = FillBehavior.Stop, EasingFunction = ease };
        grow.Completed += (_, _) => ThemeIndicator.BeginAnimation(WidthProperty, null);
        ThemeIndicatorTranslate.BeginAnimation(TranslateTransform.XProperty, slide);
        ThemeIndicator.BeginAnimation(WidthProperty, grow);
    }
    private void ShowSaved()
    {
        SavedText.Visibility = Visibility.Visible;
        SavedText.Opacity = 1;
        if (SavedText.RenderTransform is not TranslateTransform translate)
        {
            translate = new TranslateTransform();
            SavedText.RenderTransform = translate;
        }
        translate.Y = 0;
        if (StatusMotion.Allowed(_allowMotion))
        {
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)) { FillBehavior = FillBehavior.Stop };
            fade.Completed += (_, _) => SavedText.BeginAnimation(OpacityProperty, null);
            var rise = new DoubleAnimation(4, 0, TimeSpan.FromMilliseconds(150)) { FillBehavior = FillBehavior.Stop };
            rise.Completed += (_, _) => translate.BeginAnimation(TranslateTransform.YProperty, null);
            SavedText.BeginAnimation(OpacityProperty, fade);
            translate.BeginAnimation(TranslateTransform.YProperty, rise);
        }
        _savedTimer.Stop();
        _savedTimer.Start();
    }
    private void HideSaved()
    {
        if (StatusMotion.Allowed(_allowMotion))
        {
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200)) { FillBehavior = FillBehavior.Stop };
            fade.Completed += (_, _) =>
            {
                SavedText.BeginAnimation(OpacityProperty, null);
                SavedText.Opacity = 0;
                SavedText.Visibility = Visibility.Collapsed;
            };
            SavedText.BeginAnimation(OpacityProperty, fade);
            return;
        }
        SavedText.Opacity = 0;
        SavedText.Visibility = Visibility.Collapsed;
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
        // O idioma Ã© aplicado aqui, na janela que jÃ¡ estÃ¡ aberta: nada Ã© fechado, entÃ£o o tÃºnel nÃ£o
        // passa por OnClosing nem pelo ExitGuard. A pÃ¡gina continua aberta e confirma com "Saved".
        LocalizationManager.Current.Apply(language);
        Saved?.Invoke(this, EventArgs.Empty);
        ShowSaved();
    }
}

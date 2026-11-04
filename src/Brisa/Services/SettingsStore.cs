using System.Diagnostics;
using System.Text.Json;
using System.Windows;

namespace Brisa.Services;

public enum AppTheme { System, Light, Dark }
public sealed record UserSettings(AppTheme Theme = AppTheme.System, bool StartWithWindows = false);

public sealed class SettingsStore
{
    public string DataDirectory { get; }
    private readonly bool _allowStartupRegistration;
    private readonly Action<bool> _setStartup;
    private string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public UserSettings Current { get; private set; }
    public SettingsStore(string? dataDirectory = null, bool allowStartupRegistration = true, Action<bool>? setStartup = null)
    {
        DataDirectory = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Brisa");
        _allowStartupRegistration = allowStartupRegistration;
        _setStartup = setStartup ?? SetStartup;
        try { Current = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(SettingsPath)) ?? new(); }
        catch { Current = new(); }
    }
    public void Save(UserSettings value)
    {
        Directory.CreateDirectory(DataDirectory);
        var temporaryPath = Path.Combine(DataDirectory, ".settings-" + Guid.NewGuid().ToString("N") + ".tmp");
        var startupChanged = false;
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(value));
            if (_allowStartupRegistration)
            {
                _setStartup(value.StartWithWindows);
                startupChanged = true;
            }
            if (File.Exists(SettingsPath)) File.Replace(temporaryPath, SettingsPath, null);
            else File.Move(temporaryPath, SettingsPath);
        }
        catch
        {
            // Persistence failed after registration; restore the previous choice.
            if (startupChanged) _setStartup(Current.StartWithWindows);
            throw;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
        Current = value;
        ApplyTheme();
    }
    public void ApplyTheme()
    {
        var app = System.Windows.Application.Current;
        app.ThemeMode = Current.Theme switch
        {
            AppTheme.Light => ThemeMode.Light,
            AppTheme.Dark => ThemeMode.Dark,
            _ => ThemeMode.System
        };
        var systemLight = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
        var dark = Current.Theme == AppTheme.Dark || Current.Theme == AppTheme.System && systemLight is int value && value == 0;
        System.Windows.Media.SolidColorBrush Brush(string hex) => new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        app.Resources["NativeBackground"] = SystemParameters.HighContrast ? System.Windows.SystemColors.WindowBrush : Brush(dark ? "#202020" : "#F3F3F3");
        app.Resources["NativeForeground"] = SystemParameters.HighContrast ? System.Windows.SystemColors.WindowTextBrush : Brush(dark ? "#F4F4F4" : "#1A1A1A");
        app.Resources["NativeSurface"] = SystemParameters.HighContrast ? System.Windows.SystemColors.ControlBrush : Brush(dark ? "#2B2B2B" : "#FFFFFF");
        app.Resources["NativeSecondary"] = SystemParameters.HighContrast ? System.Windows.SystemColors.WindowTextBrush : Brush(dark ? "#C5C5C5" : "#5D5D5D");
        app.Resources["NativeStroke"] = SystemParameters.HighContrast ? System.Windows.SystemColors.WindowTextBrush : Brush(dark ? "#353535" : "#E5E5E5");
        app.Resources["NativeIconSurface"] = SystemParameters.HighContrast ? System.Windows.SystemColors.ControlBrush : Brush(dark ? "#363636" : "#F3F3F3");
        // Hover e pressionado precisam ser distinguíveis, e no tema claro o fundo da página já é
        // #F3F3F3: um hover nessa cor seria invisível, então os dois passos descem.
        app.Resources["NativeIconSurfaceHover"] = SystemParameters.HighContrast ? System.Windows.SystemColors.HighlightBrush : Brush(dark ? "#363636" : "#E8E8E8");
        app.Resources["NativeIconSurfacePressed"] = SystemParameters.HighContrast ? System.Windows.SystemColors.HighlightBrush : Brush(dark ? "#4A4A4A" : "#DCDCDC");

        // Um acento só para "conectado/protegido": o teal da marca. Salmon continua sendo só ação.
        // O fundo é o mesmo teal em baixa opacidade, resolvido por tema porque #125B50 sobre #2B2B2B
        // fica indistinguível do cartão.
        app.Resources["NativeStatusConnectedSurface"] = SystemParameters.HighContrast
            ? System.Windows.SystemColors.HighlightBrush : Brush(dark ? "#1E3A35" : "#E3F1EE");
        app.Resources["NativeStatusConnected"] = SystemParameters.HighContrast
            ? System.Windows.SystemColors.HighlightTextBrush : Brush(dark ? "#4FD1B8" : "#125B50");
        // A cor de destaque é da marca, não a do Windows. O Fluent deriva o botão primário do accent
        // do sistema, e nesta máquina ele saía vermelho de erro no tema claro. Mesma matiz do salmão
        // do tema escuro, escurecida no claro até passar 4.5:1 contra o rótulo branco.
        if (!SystemParameters.HighContrast)
        {
            // Idle, hover e pressionado existem porque o Fluent claro usava a mesma cor nos três: sem
            // degrau, o botão não responde ao ponteiro. Todos mantêm o rótulo acima de 4.5:1.
            var accentFill = dark ? "#F38064" : "#C74626";
            var accentHover = dark ? "#FA9E88" : "#B64023";
            var accentPressed = dark ? "#DB6B50" : "#A53A1F";
            var accentLabel = dark ? "#000000" : "#FFFFFF";
            app.Resources["AccentButtonBackground"] = Brush(accentFill);
            app.Resources["AccentButtonBackgroundPointerOver"] = Brush(accentHover);
            app.Resources["AccentButtonBackgroundPressed"] = Brush(accentPressed);
            app.Resources["AccentButtonForeground"] = Brush(accentLabel);
            app.Resources["AccentButtonForegroundPointerOver"] = Brush(accentLabel);
            // Hyperlink herda a cor de destaque do sistema no Fluent: no tema claro saía vinho,
            // não o coral fixado. 4.85:1 no claro e 5.45:1 no escuro sobre o cartão.
            app.Resources["NativeLinkForeground"] = SystemParameters.HighContrast ? System.Windows.SystemColors.HotTrackBrush : Brush(accentFill);
            app.Resources["NativeLinkForegroundPointerOver"] = SystemParameters.HighContrast ? System.Windows.SystemColors.HotTrackBrush : Brush(accentHover);
            app.Resources["AccentFillColorDefaultBrush"] = Brush(accentFill);
            app.Resources["AccentFillColorSecondaryBrush"] = Brush(accentFill);
            app.Resources["AccentFillColorTertiaryBrush"] = Brush(accentFill);
            app.Resources["AccentTextFillColorPrimaryBrush"] = Brush(accentFill);
            app.Resources["AccentTextFillColorSecondaryBrush"] = Brush(accentFill);
            // O CheckBox e o RadioButton do Fluent não usam AccentFillColor*: a marca sai do
            // SystemAccentColor, uma cor, não um brush. Sem fixar a cor, o tema claro desenhava
            // #C21C14 (o destaque do Windows) em vez do coral fixado.
            var accentColor = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(accentFill);
            app.Resources["SystemAccentColor"] = accentColor;
            app.Resources["SystemAccentColorLight1"] = accentColor;
            app.Resources["SystemAccentColorLight2"] = accentColor;
            app.Resources["SystemAccentColorLight3"] = accentColor;
            app.Resources["SystemAccentColorDark1"] = accentColor;
            app.Resources["SystemAccentColorDark2"] = accentColor;
            app.Resources["SystemAccentColorDark3"] = accentColor;
            app.Resources["CheckBoxCheckBackgroundFillChecked"] = Brush(accentFill);
            app.Resources["CheckBoxCheckBackgroundFillCheckedPointerOver"] = Brush(accentHover);
            app.Resources["CheckBoxCheckBackgroundFillCheckedPressed"] = Brush(accentPressed);
            app.Resources["RadioButtonOuterEllipseCheckedFill"] = Brush(accentFill);
            app.Resources["RadioButtonOuterEllipseCheckedFillPointerOver"] = Brush(accentHover);
            app.Resources["RadioButtonOuterEllipseCheckedFillPressed"] = Brush(accentPressed);
        }
    }
    private static void SetStartup(bool enabled)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("Brisa", $"\"{Environment.ProcessPath}\" --startup"); else key.DeleteValue("Brisa", false);
    }
}

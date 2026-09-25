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
    }
    private static void SetStartup(bool enabled)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("Brisa", $"\"{Environment.ProcessPath}\" --startup"); else key.DeleteValue("Brisa", false);
    }
}

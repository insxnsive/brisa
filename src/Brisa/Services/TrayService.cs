using System.Drawing;
using Brisa.Localization;
using System.Windows;

namespace Brisa.Services;

public sealed class TrayService : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly Icon? _productIcon;
    public TrayService(Window window, SettingsStore settings)
    {
        try { if (Environment.ProcessPath is { } path) _productIcon = Icon.ExtractAssociatedIcon(path); }
        catch { _productIcon = null; }
        _icon = new System.Windows.Forms.NotifyIcon { Text = "Brisa", Icon = _productIcon ?? SystemIcons.Application, Visible = true };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(LocalizationManager.Current["TrayOpen"], null, (_, _) => { window.Show(); window.Activate(); });
        menu.Items.Add(LocalizationManager.Current["TrayExit"], null, (_, _) => ((MainWindow)window).RequestExit());
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => { window.Show(); window.Activate(); };
        // O menu da bandeja é construído uma vez, então a troca de idioma precisa reconstruí-lo.
        LocalizationManager.Current.PropertyChanged += (_, _) =>
        {
            var rebuilt = new System.Windows.Forms.ContextMenuStrip();
            rebuilt.Items.Add(LocalizationManager.Current["TrayOpen"], null, (_, _) => { window.Show(); window.Activate(); });
            rebuilt.Items.Add(LocalizationManager.Current["TrayExit"], null, (_, _) => ((MainWindow)window).RequestExit());
            var previous = _icon.ContextMenuStrip;
            _icon.ContextMenuStrip = rebuilt;
            previous?.Dispose();
        };
    }
    public void Dispose() { _icon.Visible = false; _icon.Dispose(); _productIcon?.Dispose(); }
}

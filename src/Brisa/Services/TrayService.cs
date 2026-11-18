using System.ComponentModel;
using System.Drawing;
using Brisa.Localization;
using Brisa.Models;

namespace Brisa.Services;

/// <summary>
/// The notification area icon and its menu: a live status line, Open, the same main action Home
/// offers, and Exit. The menu is updated in place when Home changes state or the language changes.
/// </summary>
public sealed class TrayService : IDisposable
{
    /// <summary>The classic tooltip limit is 63 characters plus the terminator; longer text throws.</summary>
    public const int MaxTooltipLength = 63;

    private readonly MainWindow _window;
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly Icon? _productIcon;
    private readonly System.Windows.Forms.ContextMenuStrip _menu;
    private readonly System.Windows.Forms.ToolStripMenuItem _status;
    private readonly System.Windows.Forms.ToolStripMenuItem _open;
    private readonly System.Windows.Forms.ToolStripMenuItem _primary;
    private readonly System.Windows.Forms.ToolStripMenuItem _exit;
    private readonly PropertyChangedEventHandler _languageChanged;
    private readonly EventHandler<HomeState> _stateChanged;
    private bool _disposed;

    /// <param name="showNotifyIcon">False in tests: the menu and tooltip exist and can be read, but no icon appears on the desktop.</param>
    public TrayService(MainWindow window, SettingsStore settings, bool showNotifyIcon = true)
    {
        _window = window;
        try { if (Environment.ProcessPath is { } path) _productIcon = Icon.ExtractAssociatedIcon(path); }
        catch { _productIcon = null; }
        _status = new System.Windows.Forms.ToolStripMenuItem { Enabled = false };
        _open = new System.Windows.Forms.ToolStripMenuItem(null, null, (_, _) => window.ShowFromActivation());
        _primary = new System.Windows.Forms.ToolStripMenuItem(null, null, (_, _) => window.RunPrimaryFromTray());
        _exit = new System.Windows.Forms.ToolStripMenuItem(null, null, (_, _) => window.RequestExit());
        _menu = new System.Windows.Forms.ContextMenuStrip();
        _menu.Items.AddRange([_status, _open, _primary, new System.Windows.Forms.ToolStripSeparator(), _exit]);
        // Whether the main action is available also depends on pages and busy work that do not
        // change the Home state, so it is read again whenever the menu opens.
        _menu.Opening += OnMenuOpening;
        _icon = new System.Windows.Forms.NotifyIcon { Text = "Brisa", Icon = _productIcon ?? SystemIcons.Application, ContextMenuStrip = _menu };
        _icon.DoubleClick += (_, _) => window.ShowFromActivation();
        if (showNotifyIcon)
        {
            // An elevated Brisa would otherwise never hear that Explorer restarted, and its icon would stay gone.
            AllowTaskbarCreated();
            _icon.Visible = true;
        }
        _stateChanged = (_, state) => Apply(state);
        // O menu é atualizado no lugar: a troca de idioma só precisa reaplicar o último estado.
        _languageChanged = (_, _) => Apply(_window.CurrentHomeState);
        window.HomeStateChanged += _stateChanged;
        LocalizationManager.Current.PropertyChanged += _languageChanged;
        Apply(window.CurrentHomeState);
    }

    // Test seams: read-only views of what a user would see.
    internal string TooltipText => _icon.Text;
    internal IReadOnlyList<System.Windows.Forms.ToolStripItem> MenuItems => _menu.Items.Cast<System.Windows.Forms.ToolStripItem>().ToList();
    internal bool IconVisible => _icon.Visible;
    internal void ClickMenuItem(System.Windows.Forms.ToolStripItem item) => (item as System.Windows.Forms.ToolStripMenuItem)?.PerformClick();

    private void OnMenuOpening(object? sender, CancelEventArgs e) => Apply(_window.CurrentHomeState);

    internal void Apply(HomeState state)
    {
        if (_disposed) return;
        var loc = LocalizationManager.Current;
        var line = StatusLine(state);
        _status.Text = line;
        _icon.Text = Truncate(line, MaxTooltipLength);
        _open.Text = loc["TrayOpen"];
        _exit.Text = loc["TrayExit"];
        _primary.Text = state.PrimaryLabel;
        _primary.Visible = !state.RequiresSignIn && !state.StartupFailed;
        _primary.Enabled = _window.CanRunPrimaryFromTray;
    }

    /// <summary>"Brisa: Connected (SV#43)". The server only shows while connected.</summary>
    internal static string StatusLine(HomeState state)
    {
        var server = state.Phase == ConnectionPhase.Connected && state.Snapshot?.Route?.Server is { } name && !string.IsNullOrWhiteSpace(name)
            ? " (" + name.Trim() + ")" : "";
        return string.Format(LocalizationManager.Current["TrayStatusFormat"], state.StatusText + server);
    }

    /// <summary>Cuts to at most <paramref name="limit"/> characters, ending in three dots, without splitting a surrogate pair.</summary>
    internal static string Truncate(string text, int limit)
    {
        if (text.Length <= limit) return text;
        var keep = limit - 3;
        if (keep > 0 && char.IsHighSurrogate(text[keep - 1])) keep--;
        return text[..keep] + "...";
    }

    /// <summary>Lets the "TaskbarCreated" message through even when Brisa runs at a higher integrity level than Explorer.</summary>
    internal static bool AllowTaskbarCreated()
    {
        try
        {
            var message = NativeMethods.RegisterWindowMessage("TaskbarCreated");
            return message != 0 && NativeMethods.ChangeWindowMessageFilter(message, 1);
        }
        catch { return false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Sem isto o singleton manteria a bandeja viva, e um Apply posterior reconstruiria o menu de
        // um ícone já descartado.
        _window.HomeStateChanged -= _stateChanged;
        LocalizationManager.Current.PropertyChanged -= _languageChanged;
        _menu.Opening -= OnMenuOpening;
        _icon.Visible = false;
        _icon.ContextMenuStrip = null;
        _icon.Dispose();
        _menu.Dispose();
        _productIcon?.Dispose();
    }
}

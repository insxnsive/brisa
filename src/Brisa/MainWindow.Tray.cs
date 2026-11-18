using System.Windows;
using Brisa.Models;

namespace Brisa;

public partial class MainWindow
{
    /// <summary>The state the Home screen is showing right now, for a tray menu that starts after the first paint.</summary>
    internal HomeState CurrentHomeState => _state;

    /// <summary>
    /// The tray may run the main action only when Home itself could: no page open, nothing busy,
    /// signed in, and the button enabled (or able to restart as administrator). It goes through
    /// the same click handler, so it can never bypass Home's guards.
    /// </summary>
    internal bool CanRunPrimaryFromTray => _currentPage is null && !_navigationBusy && !_closing && !UiMutationBusy
        && !_state.RequiresSignIn && !_state.StartupFailed && (_state.PrimaryEnabled || _state.CanRestartElevated);

    internal void RunPrimaryFromTray()
    {
        if (CanRunPrimaryFromTray) Primary_Click(this, new RoutedEventArgs());
    }

    /// <summary>Brings the window back from the tray, the taskbar or behind other windows. Shared by the tray and a second launch.</summary>
    internal void ShowFromActivation()
    {
        if (_closing || _canClose) return;
        try
        {
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
        }
        catch (InvalidOperationException) { }
    }
}

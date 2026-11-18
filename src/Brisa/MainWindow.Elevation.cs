using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Windows;

namespace Brisa;

public partial class MainWindow
{
    /// <summary>Test seam: replaces the real relaunch so a test never shows a Windows administrator prompt.</summary>
    internal Func<Task>? RestartElevatedOverride { get; init; }

    /// <summary>
    /// Relaunches Brisa with administrator rights. The new process waits for this one to leave, then
    /// takes over the single-instance mutex. Only the current account is passed along, so a prompt
    /// answered with another account's password refuses to run instead of using the wrong profile.
    /// </summary>
    private async Task RestartElevatedAsync()
    {
        var exe = Environment.ProcessPath;
        if (exe is null || !Path.GetFileName(exe).Equals("Brisa.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(exe)) return;
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        var info = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        info.ArgumentList.Add("--wait-for-pid");
        info.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add("--expect-user-sid");
        info.ArgumentList.Add(sid);
        try { using var _ = Process.Start(info); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // The user answered No at the prompt.
            _notice(Localization.LocalizationManager.Current["ElevationCanceled"]);
            return;
        }
        catch (Win32Exception)
        {
            _notice(Localization.LocalizationManager.Current["ElevationCanceled"]);
            return;
        }
        // Drop the mutex and the activation event now, so the new process can take them as soon as
        // this window has finished closing through the usual exit checks.
        (System.Windows.Application.Current as App)?.ReleaseSingleInstance();
        RequestExit();
        await Task.CompletedTask;
    }
}

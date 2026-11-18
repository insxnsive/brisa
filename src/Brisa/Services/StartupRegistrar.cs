using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace Brisa.Services;

/// <summary>How Brisa starts at sign in. Off: not at all. RunKey: the HKCU Run value. ElevatedTask: a logon task.</summary>
public enum StartupMode { Off, RunKey, ElevatedTask }

public interface IStartupRegistrar
{
    StartupMode Current();
    void Apply(StartupMode mode);
}

/// <summary>The user answered "No" to the Windows administrator prompt. Nothing was changed.</summary>
public sealed class StartupRegistrationCanceledException : Exception
{
    public StartupRegistrationCanceledException() : base("Startup registration was canceled.") { }
}

/// <summary>The change did not happen. Messages are app-owned and never carry Windows text.</summary>
public class StartupRegistrationFailedException : Exception
{
    public StartupRegistrationFailedException(string message) : base(message) { }
}

/// <summary>This account cannot start Brisa elevated, so the logon task cannot be created for it.</summary>
public sealed class StartupElevationNeedsAdminException : StartupRegistrationFailedException
{
    public StartupElevationNeedsAdminException() : base("This account cannot start Brisa elevated.") { }
}

/// <summary>What the registrar needs from Windows. A fake stands in for it in tests.</summary>
internal interface IStartupSystem
{
    string? ProcessPath { get; }
    bool TaskExists();
    bool RunKeyPresent();
    void SetRunKey(string command);
    void DeleteRunKey();
    /// <summary>Runs the elevated helper and returns its exit code, or -1 on a timeout. Throws the canceled exception on UAC "No".</summary>
    int RunElevatedHelper(string verb);
    bool TryDeleteTaskWithoutElevation();
}

/// <summary>
/// Applies a startup mode while asking for administrator rights as rarely as possible. The order of
/// the steps matters: the Run value is only touched after the helper has succeeded, so a refused
/// prompt leaves the old arrangement exactly as it was.
/// </summary>
public sealed class ProductionStartupRegistrar : IStartupRegistrar
{
    private readonly IStartupSystem _system;
    private readonly Func<string> _runKeyCommand;

    public ProductionStartupRegistrar(Func<string> runKeyCommand) : this(runKeyCommand, new WindowsStartupSystem()) { }

    internal ProductionStartupRegistrar(Func<string> runKeyCommand, IStartupSystem system)
    {
        _runKeyCommand = runKeyCommand;
        _system = system;
    }

    public StartupMode Current() => _system.TaskExists() ? StartupMode.ElevatedTask : _system.RunKeyPresent() ? StartupMode.RunKey : StartupMode.Off;

    public void Apply(StartupMode mode)
    {
        // A dotnet.exe path from a developer run must never be registered for sign in.
        if (mode != StartupMode.Off && !IsBrisaExecutable(_system.ProcessPath))
            throw new StartupRegistrationFailedException("Brisa.exe is not the running program.");
        switch (mode)
        {
            case StartupMode.ElevatedTask:
                RunHelper("enable");
                _system.DeleteRunKey();
                break;
            case StartupMode.RunKey:
                if (_system.TaskExists()) RunHelper("disable");
                _system.SetRunKey(_runKeyCommand());
                break;
            default:
                if (_system.TaskExists()) RunHelper("disable");
                _system.DeleteRunKey();
                break;
        }
    }

    private void RunHelper(string verb)
    {
        var code = _system.RunElevatedHelper(verb);
        if (code == StartupTaskHelper.Ok) return;
        if (code is StartupTaskHelper.WrongUser or StartupTaskHelper.NotElevated) throw new StartupElevationNeedsAdminException();
        throw new StartupRegistrationFailedException("The startup helper did not finish.");
    }

    internal static bool IsBrisaExecutable(string? path) =>
        path is not null && Path.GetFileName(path).Equals("Brisa.exe", StringComparison.OrdinalIgnoreCase);

    private sealed class WindowsStartupSystem : IStartupSystem
    {
        private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private static string Sid => WindowsIdentity.GetCurrent().User!.Value;

        public string? ProcessPath => Environment.ProcessPath;

        public bool TaskExists() => StartupTaskHelper.RunSchtasksProcess(["/Query", "/TN", StartupTaskHelper.TaskName(Sid)]) == 0;

        public bool RunKeyPresent()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunPath);
            return key?.GetValue("Brisa") is not null;
        }

        public void SetRunKey(string command)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunPath);
            key.SetValue("Brisa", command);
        }

        public void DeleteRunKey()
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunPath);
            key.DeleteValue("Brisa", false);
        }

        public int RunElevatedHelper(string verb)
        {
            var exe = ProcessPath!;
            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            };
            info.ArgumentList.Add(StartupTaskHelper.Flag);
            info.ArgumentList.Add(verb);
            info.ArgumentList.Add("--expect-user-sid");
            info.ArgumentList.Add(Sid);
            try
            {
                using var process = Process.Start(info);
                if (process is null) return -1;
                return process.WaitForExit(60_000) ? process.ExitCode : -1;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new StartupRegistrationCanceledException(); }
            catch (Win32Exception) { throw new StartupRegistrationFailedException("The startup helper could not start."); }
        }

        public bool TryDeleteTaskWithoutElevation() =>
            StartupTaskHelper.RunSchtasksProcess(["/Delete", "/TN", StartupTaskHelper.TaskName(Sid), "/F"], 5_000) == 0;
    }

    /// <summary>Best effort at uninstall. Every failure is ignored: a leftover task points at a missing file and fails quietly.</summary>
    internal static void RemoveRunKeyAndTryDeleteTask(IStartupSystem system)
    {
        try { system.DeleteRunKey(); } catch { }
        try { system.TryDeleteTaskWithoutElevation(); } catch { }
    }

    internal static void RemoveRunKeyAndTryDeleteTask() => RemoveRunKeyAndTryDeleteTask(new WindowsStartupSystem());
}

/// <summary>The uninstall hook in App.Main calls this.</summary>
public static class StartupCleanup
{
    public static void RemoveRunKeyAndTryDeleteTask() => ProductionStartupRegistrar.RemoveRunKeyAndTryDeleteTask();
}

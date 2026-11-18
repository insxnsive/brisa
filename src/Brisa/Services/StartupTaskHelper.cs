using System.Diagnostics;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace Brisa.Services;

/// <summary>
/// Everything the elevated "--sync-startup-task" helper needs from the machine. The real host talks
/// to Windows; tests pass a fake, so no real task is ever created or deleted by a test.
/// </summary>
public interface IStartupTaskHost
{
    string CurrentSid { get; }
    bool IsElevated { get; }
    string? ProcessPath { get; }
    string TempDirectory { get; }
    /// <summary>Runs schtasks.exe with these arguments and returns its exit code, or -1 on a timeout.</summary>
    int RunSchtasks(IReadOnlyList<string> arguments);
}

/// <summary>
/// The one-shot elevated helper that creates or removes the logon task. It takes no paths and no
/// task text from its caller: it derives the command, the folder and the user itself, and it refuses
/// to touch anything unless it runs elevated as the account that asked for it.
/// </summary>
public static class StartupTaskHelper
{
    public const string Flag = "--sync-startup-task";
    public const int Ok = 0;
    public const int BadArguments = 2;
    public const int WrongUser = 3;
    public const int NotElevated = 4;
    public const int SchtasksFailed = 5;
    public const int WrongExecutable = 6;

    private static readonly Regex SidPattern = new(@"^S-1-5-21-[0-9-]+\z", RegexOptions.CultureInvariant);

    public static bool IsAcceptableSid(string? sid) => sid is { Length: > 0 and <= 184 } && SidPattern.IsMatch(sid);

    /// <summary>Per user and ASCII only: the first 12 hex characters of SHA-256 of the SID.</summary>
    public static string TaskName(string userSid) =>
        @"\Brisa\Startup-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userSid)))[..12].ToLowerInvariant();

    /// <summary>Exactly: --sync-startup-task enable|disable --expect-user-sid S-1-5-21-...</summary>
    public static bool TryParse(IReadOnlyList<string> args, out string verb, out string sid)
    {
        verb = "";
        sid = "";
        if (args.Count != 4) return false;
        if (!args[0].Equals(Flag, StringComparison.OrdinalIgnoreCase)) return false;
        if (!args[2].Equals("--expect-user-sid", StringComparison.OrdinalIgnoreCase)) return false;
        if (!args[1].Equals("enable", StringComparison.OrdinalIgnoreCase) && !args[1].Equals("disable", StringComparison.OrdinalIgnoreCase)) return false;
        if (!IsAcceptableSid(args[3])) return false;
        verb = args[1].ToLowerInvariant();
        sid = args[3];
        return true;
    }

    /// <summary>
    /// The task definition. A pure function: every inserted value is XML-escaped. Priority 4 and no time
    /// limit, because the Task Scheduler defaults (priority 7, stop after 72 hours, no start on battery)
    /// would quietly harm a tray app that is meant to stay up.
    /// </summary>
    public static string BuildXml(string commandPath, string workingDir, string userSid)
    {
        var command = SecurityElement.Escape(commandPath);
        var directory = SecurityElement.Escape(workingDir);
        var sid = SecurityElement.Escape(userSid);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Author>Brisa</Author><Description>Starts Brisa elevated at sign in so Connect when Brisa opens can connect.</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{sid}</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{sid}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled><Hidden>false</Hidden><RunOnlyIfIdle>false</RunOnlyIfIdle><WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{command}</Command><Arguments>--startup</Arguments><WorkingDirectory>{directory}</WorkingDirectory></Exec></Actions>
            </Task>
            """;
    }

    /// <summary>UTF-16 little endian with a byte order mark, which is what Task Scheduler expects.</summary>
    public static byte[] EncodeXml(string xml)
    {
        var encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        return [.. encoding.GetPreamble(), .. encoding.GetBytes(xml)];
    }

    public static int Run(IReadOnlyList<string> args, IStartupTaskHost? host = null)
    {
        host ??= new WindowsStartupTaskHost();
        if (!TryParse(args, out var verb, out var sid)) return BadArguments;
        // Over-the-shoulder elevation runs this process as another account: do nothing at all then.
        if (!string.Equals(sid, host.CurrentSid, StringComparison.Ordinal)) return WrongUser;
        if (!host.IsElevated) return NotElevated;
        var exe = host.ProcessPath;
        if (exe is null || !Path.GetFileName(exe).Equals("Brisa.exe", StringComparison.OrdinalIgnoreCase)) return WrongExecutable;
        var name = TaskName(sid);
        return verb == "enable" ? Enable(host, name, exe, sid) : Disable(host, name);
    }

    private static int Enable(IStartupTaskHost host, string name, string exe, string sid)
    {
        var xmlPath = Path.Combine(host.TempDirectory, "brisa-task-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            using (var stream = new FileStream(xmlPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = EncodeXml(BuildXml(exe, Path.GetDirectoryName(exe)!, sid));
                stream.Write(bytes, 0, bytes.Length);
            }
            return host.RunSchtasks(["/Create", "/TN", name, "/XML", xmlPath, "/F"]) == 0 ? Ok : SchtasksFailed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return SchtasksFailed; }
        finally { try { File.Delete(xmlPath); } catch { } }
    }

    private static int Disable(IStartupTaskHost host, string name)
    {
        if (host.RunSchtasks(["/Delete", "/TN", name, "/F"]) == 0) return Ok;
        // A task that is already gone counts as removed.
        return host.RunSchtasks(["/Query", "/TN", name]) != 0 ? Ok : SchtasksFailed;
    }

    /// <summary>Runs the system's own schtasks.exe by full path. No shell, no window, no inherited console.</summary>
    internal static int RunSchtasksProcess(IReadOnlyList<string> arguments, int timeoutMilliseconds = 30_000)
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info);
        if (process is null) return -1;
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(timeoutMilliseconds))
        {
            try { process.Kill(true); } catch { }
            return -1;
        }
        return process.ExitCode;
    }

    private sealed class WindowsStartupTaskHost : IStartupTaskHost
    {
        public string CurrentSid => WindowsIdentity.GetCurrent().User?.Value ?? "";
        public bool IsElevated => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        public string? ProcessPath => Environment.ProcessPath;
        public string TempDirectory => Path.GetTempPath();
        public int RunSchtasks(IReadOnlyList<string> arguments) => RunSchtasksProcess(arguments);
    }
}

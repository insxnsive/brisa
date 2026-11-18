using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace Brisa.Services;

/// <summary>
/// One Brisa per user, even when the first one runs elevated. A mutex or event made by an elevated
/// process gets a default security descriptor that leaves out the plain user, so a normal launch
/// would be refused access to it. These objects carry an explicit, protected descriptor for the
/// current user's SID instead, and a refused open is treated as "another instance is running".
/// </summary>
public sealed class SingleInstanceActivation : IDisposable
{
    public const string DefaultPrefix = @"Local\Brisa";
    private readonly Mutex _mutex;
    private readonly EventWaitHandle? _activate;
    private RegisteredWaitHandle? _wait;
    private bool _owned;

    public static string UserSid => WindowsIdentity.GetCurrent().User!.Value;

    /// <param name="prefix">Tests pass their own prefix so they never touch the live Local\Brisa.</param>
    /// <param name="waitForPredecessor">A relaunch that expects the old process to leave: wait for its mutex instead of giving up.</param>
    public SingleInstanceActivation(string prefix = DefaultPrefix, bool waitForPredecessor = false, TimeSpan? waitBudget = null)
    {
        var user = WindowsIdentity.GetCurrent().User!;
        var mutexSecurity = new MutexSecurity();
        mutexSecurity.SetAccessRuleProtection(true, false);
        mutexSecurity.AddAccessRule(new MutexAccessRule(user, MutexRights.FullControl, AccessControlType.Allow));
        try
        {
            _mutex = MutexAcl.Create(true, prefix, out var created, mutexSecurity);
            _owned = created;
            if (!created && waitForPredecessor)
            {
                try { _owned = _mutex.WaitOne(waitBudget ?? TimeSpan.FromSeconds(30)); }
                catch (AbandonedMutexException) { _owned = true; }
            }
            IsPrimary = _owned;
        }
        catch (UnauthorizedAccessException)
        {
            // Held by an older build that used the default descriptor: that is another instance.
            _mutex = new Mutex(false);
            IsPrimary = false;
        }
        if (!IsPrimary) return;
        var eventSecurity = new EventWaitHandleSecurity();
        eventSecurity.SetAccessRuleProtection(true, false);
        eventSecurity.AddAccessRule(new EventWaitHandleAccessRule(user, EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize, AccessControlType.Allow));
        // If something else still holds the event name, run without activation rather than fail to start.
        try { _activate = EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, ActivationName(prefix), out _, eventSecurity); }
        catch (UnauthorizedAccessException) { _activate = null; }
    }

    public bool IsPrimary { get; }

    private static string ActivationName(string prefix) => $"{prefix}.Activate.{UserSid}";

    /// <summary>Calls back on a pool thread each time another launch signals this one.</summary>
    public void Listen(Action onActivate)
    {
        if (_activate is null) return;
        _wait = ThreadPool.RegisterWaitForSingleObject(_activate, (_, timedOut) => { if (!timedOut) onActivate(); }, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>
    /// A second launch hands the foreground to the first, then wakes it. The first is a background
    /// process and cannot take the foreground by itself. Never throws.
    /// </summary>
    public static bool SignalPrimary(string prefix = DefaultPrefix)
    {
        try
        {
            NativeMethods.AllowSetForegroundWindow(-1);
            using var signal = EventWaitHandle.OpenExisting(ActivationName(prefix));
            return signal.Set();
        }
        catch (WaitHandleCannotBeOpenedException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }

    /// <summary>The access list of the activation event, for tests. Null when this is not the primary.</summary>
    internal string? ActivationSddl => _activate?.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);

    /// <summary>The access list of the mutex, for tests.</summary>
    internal string MutexSddl => _mutex.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);

    public void Dispose()
    {
        _wait?.Unregister(null);
        _wait = null;
        _activate?.Dispose();
        // ReleaseMutex has to run on the thread that owns it: the window thread, never a pool callback.
        if (_owned)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
            _owned = false;
        }
        _mutex.Dispose();
    }
}

/// <summary>What an elevated relaunch tells the new process. Everything here is untrusted text.</summary>
public sealed record LaunchRequest(int? WaitForPid, bool RefuseUser);

public static class LaunchArguments
{
    private static readonly Regex SidPattern = new(@"^S-1-5-21-[0-9-]+\z", RegexOptions.CultureInvariant);

    /// <summary>All ASCII digits, greater than zero, and not this process.</summary>
    public static bool TryParsePid(string? text, int ownPid, out int pid)
    {
        pid = 0;
        if (string.IsNullOrEmpty(text) || text.Length > 10 || !text.All(c => c is >= '0' and <= '9')) return false;
        if (!int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)) return false;
        if (value <= 0 || value == ownPid) return false;
        pid = value;
        return true;
    }

    /// <summary>A usable account id: the domain or machine form for an ordinary account, never a well known group.</summary>
    public static bool IsAcceptableSid(string? sid) => sid is { Length: > 0 and <= 184 } && SidPattern.IsMatch(sid);

    /// <summary>
    /// A bad wait value is ignored, since the mutex still guards against a second copy. A user id that is
    /// present but malformed or different from the current account refuses the launch.
    /// </summary>
    public static LaunchRequest Parse(IReadOnlyList<string> args, int ownPid, string currentSid)
    {
        int? waitPid = null;
        var refuse = false;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i].Equals("--wait-for-pid", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Count && TryParsePid(args[i + 1], ownPid, out var pid)) waitPid = pid;
                i++;
            }
            else if (args[i].Equals("--expect-user-sid", StringComparison.OrdinalIgnoreCase))
            {
                var value = i + 1 < args.Count ? args[i + 1] : null;
                if (!IsAcceptableSid(value) || !string.Equals(value, currentSid, StringComparison.Ordinal)) refuse = true;
                i++;
            }
        }
        return new LaunchRequest(waitPid, refuse);
    }
}

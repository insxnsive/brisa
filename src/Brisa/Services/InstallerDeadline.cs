namespace Brisa.Services;

/// <summary>
/// The connect deadline has to allow for a human reading a vendor installer. The three-minute
/// limit measures time the app spends working; time spent with the WireSock installer on screen
/// does not count against it, and a hard ceiling still bounds the wait so the window cannot sit
/// busy forever.
/// </summary>
public static class InstallerDeadline
{
    public static readonly TimeSpan Connect = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan Ceiling = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long after the last report the installer stage still counts as on screen. The poll
    /// cadence is a 400 ms delay plus a progress call that can take up to 2 s, and one failed poll
    /// doubles that, so a tighter grace would leak installer time into the working budget.
    /// </summary>
    public static readonly TimeSpan InstallerGrace = TimeSpan.FromSeconds(6);

    public enum Expiry { None, Connect, Ceiling }

    public static bool IsInstallerActive(DateTime? lastInstallerStageAt, DateTime now) =>
        lastInstallerStageAt is { } at && now >= at && now - at <= InstallerGrace;

    /// <summary>
    /// <paramref name="waitedWithoutInstaller"/> is the time the app spent working, which the
    /// caller accumulates only while the installer stage is not on screen.
    /// </summary>
    public static Expiry Decide(DateTime startedAt, TimeSpan waitedWithoutInstaller, DateTime now, bool installerActive)
    {
        if (now - startedAt >= Ceiling) return Expiry.Ceiling;
        if (!installerActive && waitedWithoutInstaller >= Connect) return Expiry.Connect;
        return Expiry.None;
    }
}

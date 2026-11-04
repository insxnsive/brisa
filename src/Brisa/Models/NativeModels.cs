using System.Text.Json;
using System.Text.Json.Serialization;

namespace Brisa.Models;

public sealed record RouteInfo(string Server, string Country, int? PingMs = null);
public sealed record NativeSnapshot(
    bool Connected, bool ExternalTunnel, bool Reliable, bool SignedIn, string Username, RouteInfo? Route,
    string Mode = "proton", bool TunnelActive = false, string Readiness = "inactive", bool DiscordRunning = false,
    string Stage = "idle",
    [property: JsonPropertyName("hasOwnedTunnel")] bool OwnedTunnelPresent = false,
    [property: JsonPropertyName("requiresElevation")] bool RequiresElevation = false,
    // The raw inspection result, before an unknown installer state forces Reliable false.
    [property: JsonPropertyName("inspectionReliable")] bool InspectionReliable = false,
    [property: JsonPropertyName("installerUnknown")] bool InstallerUnknown = false)
{
    // Positive evidence that this process's own tunnel may still be running, even when the
    // aggregate state is mixed or unreadable. Never authorizes stopping anything by itself.
    [JsonIgnore]
    public bool HasOwnedTunnel => OwnedTunnelPresent || TunnelActive || Connected;
}
public sealed record CommandResult(bool Success, string? Code = null, string? Message = null, string? CaptchaUrl = null, RouteInfo? Route = null, string? Stage = null);
public sealed record BackendEnvelope(string Id, bool Ok, JsonElement Result, string? Error = null);
public sealed record BackendRequest(string Id, string Command, object Payload);

[JsonSerializable(typeof(NativeSnapshot))]
[JsonSerializable(typeof(CommandResult))]
[JsonSerializable(typeof(BackendEnvelope))]
[JsonSerializable(typeof(BackendRequest))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class NativeJsonContext : JsonSerializerContext;

public enum ConnectionPhase { Loading, Disconnected, Connecting, Connected, Unverified, Disconnecting, Blocked, Error, Limited, Optimizing }

public sealed record HomeState(ConnectionPhase Phase, NativeSnapshot? Snapshot, string? Detail = null)
{
    public string StatusText => Phase switch
    {
        ConnectionPhase.Loading => Loc("StatusChecking"),
        ConnectionPhase.Connecting => Loc("StatusConnecting"),
        ConnectionPhase.Disconnecting => Loc("StatusDisconnecting"),
        ConnectionPhase.Optimizing => Loc("StatusOptimizing"),
        ConnectionPhase.Connected => Loc("StatusConnected"),
        ConnectionPhase.Unverified => Loc("StatusUnverified"),
        ConnectionPhase.Blocked => Loc("StatusUnavailable"),
        ConnectionPhase.Limited => Loc("StatusAdministratorRequired"),
        ConnectionPhase.Error => Loc("StatusError"),
        _ => Loc("StatusDisconnected")
    };

    private static string Loc(string key) => Localization.LocalizationManager.Current[key];

    public bool RequiresSignIn => Snapshot is { SignedIn: false, Mode: "proton", HasOwnedTunnel: false, ExternalTunnel: false, Reliable: true };
    public bool StartupFailed => Phase == ConnectionPhase.Error && Snapshot is null;
    public string PrimaryLabel => StartupFailed ? Loc("ExitBrisa") : Phase == ConnectionPhase.Limited ? Loc("Connect")
        : Snapshot?.HasOwnedTunnel == true ? Loc("Disconnect") : RequiresSignIn ? Loc("SignIn") : Loc("Connect");
    public bool PrimaryEnabled => StartupFailed || (Phase is ConnectionPhase.Disconnected or ConnectionPhase.Connected or ConnectionPhase.Unverified
        && Snapshot is { ExternalTunnel: false, Reliable: true });

    public static HomeState FromSnapshot(NativeSnapshot snapshot) => snapshot switch
    {
        // Brisa's own tunnel is positively identified next to another WireSock tunnel. It
        // cannot be stopped safely while the other one exists, so say so instead of exiting.
        { HasOwnedTunnel: true, ExternalTunnel: true } => new(ConnectionPhase.Blocked, snapshot,
            "Brisa's tunnel is still running alongside another WireSock tunnel. Close the other tunnel, then disconnect Brisa. Brisa will stay open until its tunnel can be stopped safely."),
        { HasOwnedTunnel: true, Reliable: false } => new(ConnectionPhase.Blocked, snapshot,
            "Brisa's tunnel may still be running, but ownership could not be checked reliably. Run Brisa as administrator and retry. Brisa will stay open."),
        // Supervision of the vendor installer was lost: the tunnel state is still readable, but
        // the install result is not, so say what is actually unknown instead of asking for
        // elevation the user already has.
        { InstallerUnknown: true } => new(ConnectionPhase.Blocked, snapshot,
            "WireSock installation status is unknown. Restart Brisa before connecting."),
        { ExternalTunnel: true } => new(ConnectionPhase.Blocked, snapshot, "Another app owns the active tunnel. Close it before continuing."),
        // Limited access is not a broken state: sign-in and import work without elevation.
        { RequiresElevation: true } => new(ConnectionPhase.Limited, snapshot,
            "Open Brisa as administrator to connect or disconnect. You can sign in and import a profile here."),
        { Reliable: false } => new(ConnectionPhase.Blocked, snapshot, "Tunnel ownership could not be verified. Run Brisa as administrator. No changes were made."),
        { Connected: true } => new(ConnectionPhase.Connected, snapshot),
        { TunnelActive: true } => new(ConnectionPhase.Unverified, snapshot, "The tunnel is running, but Discord routing is not verified. Disconnect and try again."),
        _ => new(ConnectionPhase.Disconnected, snapshot)
    };
}

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

public sealed record HomeState(ConnectionPhase Phase, NativeSnapshot? Snapshot, string? DetailKey = null)
{
    // O detalhe é resolvido na hora de pintar, não quando o snapshot chega: assim uma troca de
    // idioma repinta o texto que veio do estado, e não só o rótulo do status.
    public string? Detail => DetailKey is null ? null : Loc(DetailKey);

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
            "DetailTunnelMixedOwnership"),
        { HasOwnedTunnel: true, Reliable: false } => new(ConnectionPhase.Blocked, snapshot,
            "DetailOwnershipUnreliable"),
        // Supervision of the vendor installer was lost: the tunnel state is still readable, but
        // the install result is not, so say what is actually unknown instead of asking for
        // elevation the user already has.
        { InstallerUnknown: true } => new(ConnectionPhase.Blocked, snapshot,
            "DetailInstallerUnknown"),
        { ExternalTunnel: true } => new(ConnectionPhase.Blocked, snapshot, "DetailExternalTunnel"),
        // Limited access is not a broken state: sign-in and import work without elevation.
        { RequiresElevation: true } => new(ConnectionPhase.Limited, snapshot,
            "DetailRequiresElevation"),
        { Reliable: false } => new(ConnectionPhase.Blocked, snapshot, "DetailOwnershipUnverified"),
        { Connected: true } => new(ConnectionPhase.Connected, snapshot),
        { TunnelActive: true } => new(ConnectionPhase.Unverified, snapshot, "DetailTunnelUnverified"),
        _ => new(ConnectionPhase.Disconnected, snapshot)
    };
}

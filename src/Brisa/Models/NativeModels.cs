using System.Text.Json;
using System.Text.Json.Serialization;

namespace Brisa.Models;

public sealed record RouteInfo(string Server, string Country, int? PingMs = null);
public sealed record NativeSnapshot(
    bool Connected, bool ExternalTunnel, bool Reliable, bool SignedIn, string Username, RouteInfo? Route,
    string Mode = "proton", bool TunnelActive = false, string Readiness = "inactive", bool DiscordRunning = false,
    string Stage = "idle",
    [property: JsonPropertyName("hasOwnedTunnel")] bool OwnedTunnelPresent = false,
    [property: JsonPropertyName("requiresElevation")] bool RequiresElevation = false)
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
        ConnectionPhase.Loading => "Checking status…",
        ConnectionPhase.Connecting => "Connecting…",
        ConnectionPhase.Disconnecting => "Disconnecting…",
        ConnectionPhase.Optimizing => "Optimizing route…",
        ConnectionPhase.Connected => "Connected",
        ConnectionPhase.Unverified => "Not verified",
        ConnectionPhase.Blocked => "Unavailable",
        ConnectionPhase.Limited => "Administrator required",
        ConnectionPhase.Error => "Couldn’t load status",
        _ => "Disconnected"
    };

    public bool RequiresSignIn => Snapshot is { SignedIn: false, Mode: "proton", HasOwnedTunnel: false, ExternalTunnel: false, Reliable: true };
    public bool StartupFailed => Phase == ConnectionPhase.Error && Snapshot is null;
    public string PrimaryLabel => StartupFailed ? "Exit Brisa" : Phase == ConnectionPhase.Limited ? "Connect"
        : Snapshot?.HasOwnedTunnel == true ? "Disconnect" : RequiresSignIn ? "Sign In" : "Connect";
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

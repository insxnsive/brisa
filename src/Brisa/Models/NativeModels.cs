using System.Text.Json;
using System.Text.Json.Serialization;

namespace Brisa.Models;

public sealed record RouteInfo(string Server, string Country, int? PingMs = null);
public sealed record NativeSnapshot(bool Connected, bool ExternalTunnel, bool Reliable, bool SignedIn, string Username, RouteInfo? Route, string Mode = "proton", bool TunnelActive = false, string Readiness = "inactive", bool DiscordRunning = false, string Stage = "idle")
{
    public bool HasOwnedTunnel => TunnelActive || Connected;
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

public enum ConnectionPhase { Loading, Disconnected, Connecting, Connected, Unverified, Disconnecting, Blocked, Error }

public sealed record HomeState(ConnectionPhase Phase, NativeSnapshot? Snapshot, string? Detail = null)
{
    public string StatusText => Phase switch
    {
        ConnectionPhase.Loading => "Checking status…",
        ConnectionPhase.Connecting => "Connecting…",
        ConnectionPhase.Disconnecting => "Disconnecting…",
        ConnectionPhase.Connected => "Connected",
        ConnectionPhase.Unverified => "Not verified",
        ConnectionPhase.Blocked => "Unavailable",
        ConnectionPhase.Error => "Couldn’t load status",
        _ => "Disconnected"
    };

    public bool RequiresSignIn => Snapshot is { SignedIn: false, Mode: "proton", HasOwnedTunnel: false, ExternalTunnel: false, Reliable: true };
    public string PrimaryLabel => Snapshot?.HasOwnedTunnel == true ? "Disconnect" : RequiresSignIn ? "Sign In" : "Connect";
    public bool PrimaryEnabled => Phase is ConnectionPhase.Disconnected or ConnectionPhase.Connected or ConnectionPhase.Unverified
        && Snapshot is { ExternalTunnel: false, Reliable: true };

    public static HomeState FromSnapshot(NativeSnapshot snapshot) => snapshot switch
    {
        { ExternalTunnel: true } => new(ConnectionPhase.Blocked, snapshot, "Another app owns the active tunnel. Close it before continuing."),
        { Reliable: false } => new(ConnectionPhase.Blocked, snapshot, "Tunnel ownership could not be verified. Run Brisa as administrator. No changes were made."),
        { Connected: true } => new(ConnectionPhase.Connected, snapshot),
        { TunnelActive: true } => new(ConnectionPhase.Unverified, snapshot, "The tunnel is running, but Discord routing is not verified. Disconnect and try again."),
        _ => new(ConnectionPhase.Disconnected, snapshot)
    };
}

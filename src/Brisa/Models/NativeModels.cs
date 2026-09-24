using System.Text.Json;
using System.Text.Json.Serialization;

namespace Brisa.Models;

public sealed record RouteInfo(string Server, string Country, int? PingMs = null);
public sealed record NativeSnapshot(bool Connected, bool ExternalTunnel, bool Reliable, bool SignedIn, string Username, RouteInfo? Route, string Mode = "proton");
public sealed record CommandResult(bool Success, string? Code = null, string? Message = null, string? CaptchaUrl = null, RouteInfo? Route = null);
public sealed record BackendEnvelope(string Id, bool Ok, JsonElement Result, string? Error = null);
public sealed record BackendRequest(string Id, string Command, object Payload);

[JsonSerializable(typeof(NativeSnapshot))]
[JsonSerializable(typeof(CommandResult))]
[JsonSerializable(typeof(BackendEnvelope))]
[JsonSerializable(typeof(BackendRequest))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class NativeJsonContext : JsonSerializerContext;

public enum ConnectionPhase { Loading, Disconnected, Connecting, Connected, Disconnecting, Blocked, Error }

public sealed record HomeState(ConnectionPhase Phase, NativeSnapshot? Snapshot, string? Detail = null)
{
    public string StatusText => Phase switch
    {
        ConnectionPhase.Loading => "Checking status…",
        ConnectionPhase.Connecting => "Connecting…",
        ConnectionPhase.Disconnecting => "Disconnecting…",
        ConnectionPhase.Connected => "Connected",
        ConnectionPhase.Blocked => "Unavailable",
        ConnectionPhase.Error => "Couldn’t load status",
        _ => "Disconnected"
    };

    public string PrimaryLabel => Phase == ConnectionPhase.Connected ? "Disconnect" : "Connect";
    public bool PrimaryEnabled => Phase is ConnectionPhase.Disconnected or ConnectionPhase.Connected
        && Snapshot is { ExternalTunnel: false, Reliable: true };

    public static HomeState FromSnapshot(NativeSnapshot snapshot) => snapshot switch
    {
        { ExternalTunnel: true } => new(ConnectionPhase.Blocked, snapshot, "Another app owns the active tunnel. Close it before continuing."),
        { Reliable: false } => new(ConnectionPhase.Blocked, snapshot, "Tunnel ownership could not be verified. Run Brisa as administrator. No changes were made."),
        { Connected: true } => new(ConnectionPhase.Connected, snapshot),
        _ => new(ConnectionPhase.Disconnected, snapshot)
    };
}

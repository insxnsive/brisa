using Brisa.Models;

namespace Brisa.Services;

public sealed class UiTestBackendClient : IBackendClient
{
    private NativeSnapshot _state;
    public List<string> Commands { get; } = [];
    private readonly TimeSpan _commandDelay;
    private readonly RouteInfo? _route;
    private string _inFlight = "";
    public UiTestBackendClient(NativeSnapshot? initial = null, TimeSpan? commandDelay = null, RouteInfo? route = null)
    {
        _route = route;
        _state = (initial ?? new(false, false, true, false, "", null)) with { Route = route ?? initial?.Route };
        _commandDelay = commandDelay ?? TimeSpan.Zero;
    }
    public Task<NativeSnapshot> SnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(_state);
    public async Task<CommandResult> CommandAsync(string command, object payload, CancellationToken cancellationToken = default)
    {
        // O estágio tem de pertencer ao comando em curso. Antes o fixture respondia sempre com
        // "starting-tunnel", e a captura de uma desconexão mostrava "Starting and settling".
        if (command == "progress")
            return new(true, Stage: _inFlight == "disconnect" ? "stopping-tunnel" : "starting-tunnel");
        Commands.Add(command);
        if (command is "connect" or "disconnect") _inFlight = command;
        if (command is "connect" or "disconnect" && _commandDelay > TimeSpan.Zero) await Task.Delay(_commandDelay, cancellationToken).ConfigureAwait(false);
        _inFlight = "";
        if (command == "connect") _state = _state with { Connected = true, TunnelActive = true, OwnedTunnelPresent = true, Route = _route ?? _state.Route };
        if (command == "disconnect") _state = _state with { Connected = false, TunnelActive = false, OwnedTunnelPresent = false };
        if (command == "logout") _state = _state with { SignedIn = false, Username = "" };
        return new CommandResult(true, Route: _state.Route);
    }
    public Task CancelAsync() => RefuseCancel
        ? Task.FromException(new BackendRefusalException("SDK_INSTALL_SUPERVISION_LOST", "WireSock installation status is unknown. Brisa will stay open; restart Brisa before trying again."))
        : Task.CompletedTask;
    // Reproduces the state that used to make exit impossible: the backend refuses the cancel
    // because installer supervision was lost.
    public bool RefuseCancel { get; set; }
    // Log de fixture: mesma forma do real, dizendo na primeira linha que é fixture, para nenhuma
    // captura ser confundida com dados de uma máquina de verdade.
    public Task<string> DiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        return Task.FromResult(string.Join("\n",
            $"Brisa log. Generated {now:yyyy-MM-ddTHH:mm:ss.fffZ}. Contains no account names, passwords, file paths or IP addresses.",
            "",
            "Elevated: false",
            "Discord flavour: STABLE",
            "WireSock SDK version: 0.9.4.0",
            "WireSock trust: TRUSTED",
            "Mode: PROTON",
            "Protocol: WIREGUARD_UDP",
            "Theme: DARK",
            "Phase: CONNECTED",
            "Stage: idle",
            "Route country: HT",
            "Route check: verified",
            "",
            "Events (this session):",
            $"{now.AddMinutes(-2):yyyy-MM-ddTHH:mm:ss.fffZ} startup NOT_ELEVATED",
            $"{now.AddMinutes(-1):yyyy-MM-ddTHH:mm:ss.fffZ} starting-tunnel START_CLIENT_EXITED exitCode=1 timedOut=false"));
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

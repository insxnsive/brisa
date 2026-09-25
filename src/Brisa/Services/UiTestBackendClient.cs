using Brisa.Models;

namespace Brisa.Services;

public sealed class UiTestBackendClient : IBackendClient
{
    private NativeSnapshot _state;
    public List<string> Commands { get; } = [];
    private readonly TimeSpan _commandDelay;
    public UiTestBackendClient(NativeSnapshot? initial = null, TimeSpan? commandDelay = null)
    {
        _state = initial ?? new(false, false, true, false, "", null);
        _commandDelay = commandDelay ?? TimeSpan.Zero;
    }
    public Task<NativeSnapshot> SnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(_state);
    public async Task<CommandResult> CommandAsync(string command, object payload, CancellationToken cancellationToken = default)
    {
        if (command == "progress") return new(true, Stage: "starting-tunnel");
        Commands.Add(command);
        if (command is "connect" or "disconnect" && _commandDelay > TimeSpan.Zero) await Task.Delay(_commandDelay, cancellationToken).ConfigureAwait(false);
        if (command == "connect") _state = _state with { Connected = true };
        if (command == "disconnect") _state = _state with { Connected = false, TunnelActive = false };
        if (command == "logout") _state = _state with { SignedIn = false, Username = "" };
        return new CommandResult(true, Route: _state.Route);
    }
    public Task CancelAsync() => Task.CompletedTask;
    public Task<string> DiagnosticsAsync(CancellationToken cancellationToken = default) => Task.FromResult("UI test backend\nNetwork access: disabled\nTunnel actions: simulated");
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

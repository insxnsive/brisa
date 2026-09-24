using Brisa.Models;

namespace Brisa.Services;

public sealed class UiTestBackendClient : IBackendClient
{
    private NativeSnapshot _state;
    public List<string> Commands { get; } = [];
    public UiTestBackendClient(NativeSnapshot? initial = null) => _state = initial ?? new(false, false, true, false, "", null);
    public Task<NativeSnapshot> SnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(_state);
    public Task<CommandResult> CommandAsync(string command, object payload, CancellationToken cancellationToken = default)
    {
        Commands.Add(command);
        if (command == "connect") _state = _state with { Connected = true };
        if (command == "disconnect") _state = _state with { Connected = false };
        if (command == "logout") _state = _state with { SignedIn = false, Username = "" };
        return Task.FromResult(new CommandResult(true, Route: _state.Route));
    }
    public Task CancelAsync() => Task.CompletedTask;
    public Task<string> DiagnosticsAsync(CancellationToken cancellationToken = default) => Task.FromResult("UI test backend\nNetwork access: disabled\nTunnel actions: simulated");
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

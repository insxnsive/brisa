using Brisa.Models;

namespace Brisa.Services;

public sealed class UnavailableBackendClient : IBackendClient
{
    private static InvalidOperationException Unavailable() => new("The native backend bundle is unavailable.");
    public Task<NativeSnapshot> SnapshotAsync(CancellationToken cancellationToken = default) => Task.FromException<NativeSnapshot>(Unavailable());
    public Task<CommandResult> CommandAsync(string command, object payload, CancellationToken cancellationToken = default) => Task.FromException<CommandResult>(Unavailable());
    public Task CancelAsync() => Task.CompletedTask;
    public Task<string> DiagnosticsAsync(CancellationToken cancellationToken = default) => Task.FromResult("Native backend: unavailable\nNo tunnel changes were made.");
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

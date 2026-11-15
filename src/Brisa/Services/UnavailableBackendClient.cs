using Brisa.Models;

namespace Brisa.Services;

public sealed class UnavailableBackendClient : IBackendClient
{
    public string? StartupCode => "INIT_UNKNOWN";
    private static BackendUnavailableException Unavailable() => new();
    public Task<NativeSnapshot> SnapshotAsync(CancellationToken cancellationToken = default) => Task.FromException<NativeSnapshot>(Unavailable());
    public Task<CommandResult> CommandAsync(string command, object payload, CancellationToken cancellationToken = default) => Task.FromException<CommandResult>(Unavailable());
    public Task CancelAsync() => Task.CompletedTask;
    public Task<string> DiagnosticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(StartupDiagnostics.Render(StartupCode, Array.Empty<string>()));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

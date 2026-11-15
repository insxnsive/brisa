using Brisa.Models;

namespace Brisa.Services;

public interface IBackendClient : IAsyncDisposable
{
    string? StartupCode => null;
    IReadOnlyList<string> StandardErrorTail => Array.Empty<string>();
    Task<NativeSnapshot> SnapshotAsync(CancellationToken cancellationToken = default);
    Task<CommandResult> CommandAsync(string command, object payload, CancellationToken cancellationToken = default);
    Task CancelAsync();
    Task<string> DiagnosticsAsync(CancellationToken cancellationToken = default);
}

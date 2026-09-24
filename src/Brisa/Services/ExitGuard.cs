using System.Runtime.CompilerServices;

namespace Brisa.Services;

public static class ExitGuard
{
    private static readonly ConditionalWeakTable<IBackendClient, CancellationAttempt> CancellationAttempts = new();

    public static async Task StopOwnedAsync(IBackendClient backend, bool nativeOperationMayBeActive)
    {
        await GetCancellation(backend).WaitAsync(TimeSpan.FromSeconds(6));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var state = await backend.SnapshotAsync(timeout.Token);
        if (!state.Reliable && (nativeOperationMayBeActive || state.Connected))
            throw new InvalidOperationException("Tunnel ownership could not be verified. The app will stay open.");
        if (state.Connected && state.Reliable && !state.ExternalTunnel)
        {
            var result = await backend.CommandAsync("disconnect", new { }, timeout.Token);
            if (!result.Success) throw new InvalidOperationException("The native tunnel could not be stopped safely. The app will stay open.");
            var after = await backend.SnapshotAsync(timeout.Token);
            if (after.Connected || !after.Reliable)
                throw new InvalidOperationException("Disconnection could not be confirmed. The app will stay open.");
        }
    }

    private static Task GetCancellation(IBackendClient backend)
    {
        var attempt = CancellationAttempts.GetValue(backend, static _ => new());
        lock (attempt)
        {
            if (attempt.Task is null || attempt.Task.IsCompleted)
                attempt.Task = backend.CancelAsync();
            return attempt.Task;
        }
    }

    private sealed class CancellationAttempt
    {
        public Task? Task { get; set; }
    }
}

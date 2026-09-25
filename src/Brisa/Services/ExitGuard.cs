using System.Runtime.CompilerServices;

namespace Brisa.Services;

public static class ExitGuard
{
    private static readonly ConditionalWeakTable<IBackendClient, CancellationAttempt> CancellationAttempts = new();

    // False allows an idle failed-service window to close, but never authorizes
    // applying an update without a reliable final ownership check.
    public static async Task<bool> StopOwnedAsync(IBackendClient backend, bool nativeOperationMayBeActive)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        Brisa.Models.NativeSnapshot state;
        try
        {
            await GetCancellation(backend).WaitAsync(TimeSpan.FromSeconds(6));
            state = await backend.SnapshotAsync(timeout.Token);
        }
        catch (BackendUnavailableException) when (!nativeOperationMayBeActive)
        {
            return false;
        }
        if (!state.Reliable && (nativeOperationMayBeActive || state.HasOwnedTunnel))
            throw new InvalidOperationException("Tunnel ownership could not be verified. The app will stay open.");
        if (state.HasOwnedTunnel && state.Reliable && !state.ExternalTunnel)
        {
            var result = await backend.CommandAsync("disconnect", new { }, timeout.Token);
            if (!result.Success) throw new InvalidOperationException("The native tunnel could not be stopped safely. The app will stay open.");
            var after = await backend.SnapshotAsync(timeout.Token);
            if (after.HasOwnedTunnel || !after.Reliable)
                throw new InvalidOperationException("Disconnection could not be confirmed. The app will stay open.");
        }
        return state.Reliable;
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

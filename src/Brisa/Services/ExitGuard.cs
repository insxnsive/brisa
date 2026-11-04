using System.Runtime.CompilerServices;

namespace Brisa.Services;

public sealed record ExitDecision(bool SafeToApplyUpdate, string? Warning = null);

public static class ExitGuard
{
    private static readonly ConditionalWeakTable<IBackendClient, CancellationAttempt> CancellationAttempts = new();

    // False allows an idle failed-service window to close, but never authorizes
    // applying an update without a reliable final ownership check.
    public static async Task<bool> StopOwnedAsync(IBackendClient backend, bool nativeOperationMayBeActive)
        => (await DecideAsync(backend, nativeOperationMayBeActive)).SafeToApplyUpdate;

    public static async Task<ExitDecision> DecideAsync(IBackendClient backend, bool nativeOperationMayBeActive)
    {
        // Per-call budgets: a single token spanning the snapshots and the disconnect made a slow
        // stop look like a failed exit, because the disconnect alone can outlast it.
        using var snapshotBudget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Brisa.Models.NativeSnapshot state;
        try
        {
            await GetCancellation(backend).WaitAsync(TimeSpan.FromSeconds(6));
            state = await backend.SnapshotAsync(snapshotBudget.Token);
        }
        catch (BackendRefusalException refusal) when (refusal.Code == "SDK_INSTALL_SUPERVISION_LOST")
        {
            // A refused cancel is expected in exactly the state this guard exists for. The raw
            // inspection below decides whether leaving is safe; rethrowing here would keep the
            // app open forever, which is the trap this path is meant to remove.
            state = await backend.SnapshotAsync(snapshotBudget.Token);
        }
        catch (BackendUnavailableException) when (!nativeOperationMayBeActive)
        {
            return new ExitDecision(false);
        }
        // Supervision of the vendor installer was lost, which forces every snapshot to be
        // unreliable. That must not become a one-way door: the raw inspection is still valid,
        // so an app with no owned tunnel may leave, without authorizing an update.
        if (!state.Reliable && state.InspectionReliable && !state.HasOwnedTunnel)
            return new ExitDecision(false, "WireSock installation status is unknown, and a WireSock installer window may still be open. Discord was closed. Exit Brisa anyway? Check the tunnel before reconnecting.");
        if (!state.Reliable && (nativeOperationMayBeActive || state.HasOwnedTunnel))
            throw new InvalidOperationException("Tunnel ownership could not be verified. The app will stay open.");
        // Mixed ownership: Brisa's tunnel cannot be stopped while another one exists, and
        // exiting anyway would leave it running behind the user's back.
        if (state.HasOwnedTunnel && state.ExternalTunnel)
            throw new InvalidOperationException("Brisa's tunnel is still running alongside another WireSock tunnel. Close the other tunnel and retry exiting. Brisa will stay open.");
        if (state.HasOwnedTunnel && state.Reliable && !state.ExternalTunnel)
        {
            // A stop plus the Discord relaunch can take well over the snapshot budget, so the
            // disconnect gets its own, larger one.
            using var disconnectBudget = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var result = await backend.CommandAsync("disconnect", new { }, disconnectBudget.Token);
            if (!result.Success) throw new InvalidOperationException("The native tunnel could not be stopped safely. The app will stay open.");
            using var afterBudget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var after = await backend.SnapshotAsync(afterBudget.Token);
            if (after.HasOwnedTunnel || !after.Reliable)
                throw new InvalidOperationException("Disconnection could not be confirmed. The app will stay open.");
        }
        return new ExitDecision(state.Reliable);
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

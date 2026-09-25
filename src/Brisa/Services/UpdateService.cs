using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace Brisa.Services;

public enum UpdatePhase { Idle, Checking, Downloading, Current, Ready, Error }

public sealed record UpdateStatus(UpdatePhase Phase, string Message, string? Version = null, int? Progress = null);

public sealed record UpdateRelease(string Version, object NativeRelease);

public interface IUpdateClient
{
    string CurrentVersion { get; }
    bool HasPendingUpdate { get; }
    Task<UpdateRelease?> CheckForUpdatesAsync(CancellationToken cancellationToken);
    Task DownloadUpdatesAsync(UpdateRelease release, Action<int>? progress, CancellationToken cancellationToken);
    void PrepareApply(bool restart);
}

public interface IUpdateService : IDisposable, IAsyncDisposable
{
    UpdateStatus Status { get; }
    bool HasPendingUpdate { get; }
    event EventHandler<UpdateStatus>? StatusChanged;
    void Start();
    Task CheckNowAsync(CancellationToken cancellationToken = default);
    void PrepareApply(bool restart);
}

public static class UpdateLaunchPolicy
{
    public static bool ShouldStart(IEnumerable<string> arguments) => !arguments.Any(argument =>
        argument.Equals("--ui-test", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("--smoke-test", StringComparison.OrdinalIgnoreCase));
}

public static class AppVersion
{
    public const string Package = "0.1.0-beta.1";

    public static string Current
    {
        get
        {
            var informational = typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return string.IsNullOrWhiteSpace(informational) ? Package : informational.Split('+', 2)[0];
        }
    }
}

public sealed class VelopackUpdateClient : IUpdateClient
{
    public const string RepositoryUrl = "https://github.com/insxnsive/brisa";
    public const string Channel = "win";

    private readonly UpdateManager _manager;

    public VelopackUpdateClient()
    {
        var source = new GithubSource(RepositoryUrl, accessToken: null, prerelease: AppVersion.Current.Contains('-'));
        _manager = new UpdateManager(source, new UpdateOptions
        {
            ExplicitChannel = Channel,
            AllowVersionDowngrade = false
        });
    }

    public string CurrentVersion => _manager.CurrentVersion?.ToString() ?? AppVersion.Current;
    public bool HasPendingUpdate => _manager.UpdatePendingRestart is not null;

    public async Task<UpdateRelease?> CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Velopack 1.2.158 does not pass a token into the feed request. WaitAsync
        // still lets Brisa end its service lifetime promptly during safe exit;
        // no later check can start because the coordinator is then disposed.
        var update = await _manager.CheckForUpdatesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        return update is null ? null : new(update.TargetFullRelease.Version.ToString(), update);
    }

    public Task DownloadUpdatesAsync(UpdateRelease release, Action<int>? progress, CancellationToken cancellationToken) =>
        _manager.DownloadUpdatesAsync((UpdateInfo)release.NativeRelease, progress, cancellationToken);

    public void PrepareApply(bool restart)
    {
        var pending = _manager.UpdatePendingRestart;
        if (pending is not null)
            _manager.WaitExitThenApplyUpdates(pending, silent: true, restart: restart);
    }
}

public sealed class UpdateService : IUpdateService
{
    public static readonly TimeSpan DefaultCheckInterval = TimeSpan.FromHours(6);

    private readonly IUpdateClient _client;
    private readonly TimeSpan _checkInterval;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _singleFlight = new(1, 1);
    private readonly object _lifecycleGate = new();
    private Task? _loop;
    private bool _disposed;
    private Task? _disposeTask;
    private TaskCompletionSource _checksIdle = CompletedSignal();
    private int _activeChecks;
    private UpdateStatus _status;

    public UpdateService(IUpdateClient client, TimeSpan? checkInterval = null)
    {
        _client = client;
        _checkInterval = checkInterval ?? DefaultCheckInterval;
        _status = client.HasPendingUpdate
            ? Ready(null)
            : new(UpdatePhase.Idle, "Updates are checked automatically.");
    }

    public UpdateStatus Status => Volatile.Read(ref _status);
    public bool HasPendingUpdate => _client.HasPendingUpdate;
    public event EventHandler<UpdateStatus>? StatusChanged;

    public void Start()
    {
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            if (_loop is null)
            {
                var token = _lifetime.Token;
                _loop = Task.Run(() => RunAsync(token));
            }
        }
    }

    public Task CheckNowAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycleGate)
        {
            if (_disposed) return Task.CompletedTask;
            if (_activeChecks++ == 0) _checksIdle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return CheckAdmittedAsync(cancellationToken, _lifetime.Token);
        }
    }

    private async Task CheckAdmittedAsync(CancellationToken cancellationToken, CancellationToken lifetimeToken)
    {
        var acquired = false;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeToken);
            try
            {
                acquired = await _singleFlight.WaitAsync(0, linked.Token).ConfigureAwait(false);
                if (!acquired) return;
                Publish(new(UpdatePhase.Checking, "Checking for updates…"));
                var release = await _client.CheckForUpdatesAsync(linked.Token).ConfigureAwait(false);
                if (release is null)
                {
                    Publish(_client.HasPendingUpdate ? Ready(null) : new(UpdatePhase.Current, "Brisa is up to date."));
                    return;
                }

                if (!SemanticVersion.TryParse(_client.CurrentVersion, out var current) ||
                    !SemanticVersion.TryParse(release.Version, out var offered) ||
                    offered.CompareTo(current) <= 0)
                {
                    Publish(_client.HasPendingUpdate ? Ready(null) : new(UpdatePhase.Current, "Brisa is up to date."));
                    return;
                }

                Publish(new(UpdatePhase.Downloading, "Downloading update…", offered.ToString(), 0));
                await _client.DownloadUpdatesAsync(release, progress =>
                    Publish(new(UpdatePhase.Downloading, "Downloading update…", offered.ToString(), Math.Clamp(progress, 0, 100))), linked.Token).ConfigureAwait(false);
                Publish(Ready(offered.ToString()));
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                Publish(_client.HasPendingUpdate ? Ready(null) : new(UpdatePhase.Idle, "Updates are checked automatically."));
            }
            catch
            {
                Publish(_client.HasPendingUpdate ? Ready(null) : new(UpdatePhase.Error, "Updates could not be checked. Try again later."));
            }
        }
        finally
        {
            if (acquired) _singleFlight.Release();
            lock (_lifecycleGate)
                if (--_activeChecks == 0) _checksIdle.TrySetResult();
        }
    }

    public void PrepareApply(bool restart)
    {
        if (HasPendingUpdate) _client.PrepareApply(restart);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await CheckNowAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(_checkInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static UpdateStatus Ready(string? version) => new(
        UpdatePhase.Ready,
        version is null ? "An update will install when you exit Brisa." : $"Brisa {version} will install when you exit.",
        version);

    private void Publish(UpdateStatus status)
    {
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            Volatile.Write(ref _status, status);
            if (StatusChanged is not { } handlers) return;
            foreach (EventHandler<UpdateStatus> handler in handlers.GetInvocationList())
            {
                if (_disposed) break;
                try { handler(this, status); } catch { /* UI observers cannot break update cleanup. */ }
            }
        }
    }

    public void Dispose() => DisposeAsync().AsTask().ConfigureAwait(false).GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleGate)
        {
            if (_disposeTask is null)
            {
                _disposed = true;
                var loop = _loop;
                var checksIdle = _checksIdle.Task;
                _disposeTask = Task.Run(() => DisposeCoreAsync(loop, checksIdle));
            }
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync(Task? loop, Task checksIdle)
    {
        _lifetime.Cancel();
        if (loop is not null)
            try { await loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        await checksIdle.ConfigureAwait(false);
        _singleFlight.Dispose();
        _lifetime.Dispose();
    }

    private static TaskCompletionSource CompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }
}

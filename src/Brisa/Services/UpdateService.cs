using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace Brisa.Services;

public enum UpdatePhase { Idle, Checking, Downloading, Current, Ready, Error }

/// <summary>
/// O segundo campo é uma chave da tabela de strings, não uma frase pronta. A frase é resolvida na
/// leitura, então uma troca de idioma alcança um status que já estava publicado. Um texto que não
/// seja chave volta como ele mesmo, que é o que os clientes de teste passam.
/// </summary>
public sealed record UpdateStatus(UpdatePhase Phase, string MessageKey, string? Version = null, int? Progress = null)
{
    public string Message
    {
        get
        {
            var text = Localization.LocalizationManager.Current[MessageKey];
            return Version is not null && text.Contains("{0}") ? string.Format(text, Version) : text;
        }
    }
}

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

    // Velopack's UpdateManager is built on first use, not at construction, so creating the client
    // before the window paints costs nothing. A manager that cannot be built means "no updater":
    // nothing pending, checks fail into the Error status, and nothing throws into the UI.
    private readonly Lazy<UpdateManager?> _manager = new(CreateManager);

    private static UpdateManager? CreateManager()
    {
        try
        {
            var source = new GithubSource(RepositoryUrl, accessToken: null, prerelease: AppVersion.Current.Contains('-'));
            return new UpdateManager(source, new UpdateOptions
            {
                ExplicitChannel = Channel,
                AllowVersionDowngrade = false
            });
        }
        catch { return null; }
    }

    private UpdateManager Manager => _manager.Value ?? throw new InvalidOperationException("The updater is unavailable.");

    public string CurrentVersion => _manager.Value?.CurrentVersion?.ToString() ?? AppVersion.Current;
    public bool HasPendingUpdate => _manager.Value?.UpdatePendingRestart is not null;

    public async Task<UpdateRelease?> CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Velopack 1.2.158 does not pass a token into the feed request. WaitAsync
        // still lets Brisa end its service lifetime promptly during safe exit;
        // no later check can start because the coordinator is then disposed.
        var update = await Manager.CheckForUpdatesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        return update is null ? null : new(update.TargetFullRelease.Version.ToString(), update);
    }

    public Task DownloadUpdatesAsync(UpdateRelease release, Action<int>? progress, CancellationToken cancellationToken) =>
        Manager.DownloadUpdatesAsync((UpdateInfo)release.NativeRelease, progress, cancellationToken);

    public void PrepareApply(bool restart)
    {
        var pending = Manager.UpdatePendingRestart;
        if (pending is not null)
            Manager.WaitExitThenApplyUpdates(pending, silent: true, restart: restart);
    }
}

public sealed class UpdateService : IUpdateService
{
    public static readonly TimeSpan DefaultCheckInterval = TimeSpan.FromHours(6);
    // The first GitHub check waits this long after launch, so starting Brisa stays quiet.
    public static readonly TimeSpan DefaultStartupDelay = TimeSpan.FromSeconds(25);

    private readonly IUpdateClient _client;
    private readonly TimeSpan _checkInterval;
    private readonly TimeSpan _initialDelay;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _singleFlight = new(1, 1);
    private readonly object _lifecycleGate = new();
    private Task? _loop;
    private bool _disposed;
    private Task? _disposeTask;
    private TaskCompletionSource _checksIdle = CompletedSignal();
    private int _activeChecks;
    // Null until the first read or publication: the constructor never touches the client.
    private UpdateStatus? _status;

    public UpdateService(IUpdateClient client, TimeSpan? checkInterval = null, TimeSpan? initialDelay = null)
    {
        _client = client;
        _checkInterval = checkInterval ?? DefaultCheckInterval;
        _initialDelay = initialDelay is { } delay && delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
    }

    public UpdateStatus Status => Volatile.Read(ref _status) ?? InitialStatus();

    private UpdateStatus InitialStatus()
    {
        var initial = _client.HasPendingUpdate ? Ready(null) : new UpdateStatus(UpdatePhase.Idle, "UpdateIdle");
        Interlocked.CompareExchange(ref _status, initial, null);
        return Volatile.Read(ref _status)!;
    }
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
                Publish(new(UpdatePhase.Checking, "UpdateChecking"));
                var release = await _client.CheckForUpdatesAsync(linked.Token).ConfigureAwait(false);
                if (release is null)
                {
                    Publish(_client.HasPendingUpdate ? Ready(null) : new(UpdatePhase.Current, "UpdateUpToDate"));
                    return;
                }

                if (!SemanticVersion.TryParse(_client.CurrentVersion, out var current) ||
                    !SemanticVersion.TryParse(release.Version, out var offered) ||
                    offered.CompareTo(current) <= 0)
                {
                    Publish(_client.HasPendingUpdate ? Ready(null) : new(UpdatePhase.Current, "UpdateUpToDate"));
                    return;
                }

                Publish(new(UpdatePhase.Downloading, "UpdateDownloading", offered.ToString(), 0));
                await _client.DownloadUpdatesAsync(release, progress =>
                    Publish(new(UpdatePhase.Downloading, "UpdateDownloading", offered.ToString(), Math.Clamp(progress, 0, 100))), linked.Token).ConfigureAwait(false);
                Publish(Ready(offered.ToString()));
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                Publish(_client.HasPendingUpdate ? Ready(null) : new(UpdatePhase.Idle, "UpdateIdle"));
            }
            catch
            {
                Publish(_client.HasPendingUpdate ? Ready(null) : new(UpdatePhase.Error, "UpdatesCheckFailed"));
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
            if (_initialDelay > TimeSpan.Zero)
                await Task.Delay(_initialDelay, cancellationToken).ConfigureAwait(false);
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
        version is null ? "UpdateReadyOnExit" : "UpdateReadyVersion",
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

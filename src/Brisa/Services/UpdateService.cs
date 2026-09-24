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
    private int _disposed;
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
        if (Volatile.Read(ref _disposed) != 0) return;
        lock (_lifecycleGate)
            _loop ??= RunAsync(_lifetime.Token);
    }

    public async Task CheckNowAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0 || !await _singleFlight.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try
        {
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
            Publish(new(UpdatePhase.Idle, "Updates are checked automatically."));
        }
        catch
        {
            Publish(new(UpdatePhase.Error, "Updates could not be checked. Try again later."));
        }
        finally
        {
            _singleFlight.Release();
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
        Volatile.Write(ref _status, status);
        StatusChanged?.Invoke(this, status);
    }

    public void Dispose() => DisposeAsync().AsTask().ConfigureAwait(false).GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        Task? loop;
        lock (_lifecycleGate) loop = _loop;
        if (loop is not null)
            try { await loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        await _singleFlight.WaitAsync().ConfigureAwait(false);
        _singleFlight.Release();
        _singleFlight.Dispose();
        _lifetime.Dispose();
    }
}

using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using Brisa.Localization;
using Brisa.Models;
using Brisa.Security;

namespace Brisa.Services;

public sealed record FeedFetchResult(int StatusCode, byte[]? BodyBytes, string? ETag);

public interface INotificationFeedClient : IDisposable
{
    Task<FeedFetchResult> FetchAsync(string? ifNoneMatch, CancellationToken cancellationToken);
}

public interface INotificationService : IDisposable, IAsyncDisposable
{
    NotificationSnapshot Snapshot { get; }
    event EventHandler<NotificationSnapshot>? SnapshotChanged;
    void Start();
    Task RefreshNowAsync(CancellationToken cancellationToken = default);
    void BeginPopoverSession();
    void EndPopoverSession();
    void MarkRead(string id);
    void MarkAllRead();
}

/// <summary>No network at all. Used by the isolated test and smoke launches.</summary>
public sealed class NullNotificationFeedClient : INotificationFeedClient
{
    public Task<FeedFetchResult> FetchAsync(string? ifNoneMatch, CancellationToken cancellationToken) =>
        Task.FromResult(new FeedFetchResult(0, null, null));

    public void Dispose() { }
}

/// <summary>Serves one prepared envelope from memory. Never opens a socket.</summary>
public sealed class FixtureNotificationFeedClient(byte[] envelope, string etag = "\"fixture\"") : INotificationFeedClient
{
    public Task<FeedFetchResult> FetchAsync(string? ifNoneMatch, CancellationToken cancellationToken) =>
        Task.FromResult(ifNoneMatch == etag ? new FeedFetchResult(304, null, etag) : new FeedFetchResult(200, envelope, etag));

    public void Dispose() { }
}

/// <summary>
/// One GET to the pinned address. Redirects, cookies and anything over 64 KiB are refused, and
/// every failure is silent: the cached feed stays and the window never shows an error.
/// </summary>
public sealed class HttpNotificationFeedClient : INotificationFeedClient
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);
    private static readonly FeedFetchResult Failure = new(0, null, null);
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public HttpNotificationFeedClient() : this(CreateHandler(), RequestTimeout) { }

    internal HttpNotificationFeedClient(HttpMessageHandler handler, TimeSpan timeout)
    {
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _timeout = timeout;
    }

    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Brotli,
        ConnectTimeout = ConnectTimeout,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        SslOptions = { EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 },
    };

    public async Task<FeedFetchResult> FetchAsync(string? ifNoneMatch, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, NotificationTrustAnchors.FeedUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            // Version only: no OS, user or account detail leaves the machine.
            request.Headers.TryAddWithoutValidation("User-Agent", "Brisa/" + AppVersion.Current);
            // The edge may compress the body and then label the tag weak (W/"..."). Send it back as a
            // plain tag so the conditional request can still end in 304.
            var strong = ifNoneMatch is not null && ifNoneMatch.StartsWith("W/", StringComparison.Ordinal) ? ifNoneMatch[2..] : ifNoneMatch;
            if (strong is { Length: > 0 and <= 128 } && strong.All(c => c is >= ' ' and <= '~'))
                request.Headers.TryAddWithoutValidation("If-None-Match", strong);

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var etag = response.Headers.ETag?.ToString();
            if (response.StatusCode == HttpStatusCode.NotModified) return new FeedFetchResult(304, null, etag);
            if (response.StatusCode != HttpStatusCode.OK) return new FeedFetchResult((int)response.StatusCode, null, null);
            if (response.Content.Headers.ContentLength > NotificationVerifier.MaxEnvelopeBytes) return Failure;

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                // Stop at the first byte past the limit and drop the connection.
                if (buffer.Length > NotificationVerifier.MaxEnvelopeBytes) return Failure;
            }
            return new FeedFetchResult(200, buffer.ToArray(), etag);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Failure; }
    }

    public void Dispose() => _http.Dispose();
}

public sealed record NotificationStateData(
    int SchemaVersion = 1,
    int HighWaterEpoch = 0,
    long HighWaterSeq = 0,
    string? HighWaterPublishedAtUtc = null,
    string? Etag = null,
    List<string>? ReadIds = null,
    string? CachedEnvelopeJson = null);

/// <summary>notifications-state.json in the data directory. Written atomically, like settings.json.</summary>
public sealed class NotificationStateStore(string dataDirectory)
{
    public const string FileName = "notifications-state.json";
    private const long MaxBytes = 262_144;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string Path { get; } = System.IO.Path.Combine(dataDirectory, FileName);

    public NotificationStateData Load()
    {
        try
        {
            var info = new FileInfo(Path);
            if (!info.Exists || info.Length > MaxBytes) return new NotificationStateData();
            var data = JsonSerializer.Deserialize<NotificationStateData>(File.ReadAllText(Path), Json);
            // A damaged or unknown file starts empty rather than guessing.
            return data is { SchemaVersion: 1 } ? data : new NotificationStateData();
        }
        catch { return new NotificationStateData(); }
    }

    public void Save(NotificationStateData data)
    {
        Directory.CreateDirectory(dataDirectory);
        var temporary = System.IO.Path.Combine(dataDirectory, ".notifications-state-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(data, Json));
            if (File.Exists(Path)) File.Replace(temporary, Path, null);
            else File.Move(temporary, Path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed class NotificationService : INotificationService
{
    public static readonly TimeSpan DefaultStartupDelay = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMinutes(30);
    public const int MaxJitterSeconds = 120;
    public const int MaxReadIds = 200;
    private static readonly TimeSpan RefreshOnOpenAfter = TimeSpan.FromSeconds(60);

    private readonly INotificationFeedClient _client;
    private readonly NotificationVerifier _verifier;
    private readonly NotificationStateStore _store;
    private readonly IUpdateService? _updates;
    private readonly Func<DateTimeOffset> _clock;
    private readonly string? _appVersion;
    private readonly TimeSpan _startupDelay;
    private readonly TimeSpan _pollInterval;
    private readonly SynchronizationContext? _context;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancel = new();
    private readonly SemaphoreSlim _fetchLock = new(1, 1);
    private readonly EventHandler<UpdateStatus> _updateChanged;
    private readonly PropertyChangedEventHandler _languageChanged;
    private readonly List<string> _readIds = [];

    private VerifiedManifest? _manifest;
    private NotificationHighWater _highWater = NotificationHighWater.None;
    private string? _etag;
    private string? _cachedEnvelope;
    private HashSet<string> _sessionFresh = [];
    private bool _started;
    private bool _fetchAllowed;
    private int _disposed;
    private DateTimeOffset? _lastAttempt;
    private Task? _loop;
    private Task _saveChain = Task.CompletedTask;
    private NotificationSnapshot _snapshot;

    public NotificationService(
        INotificationFeedClient client,
        NotificationStateStore store,
        IUpdateService? updates = null,
        NotificationVerifier? verifier = null,
        Func<DateTimeOffset>? clock = null,
        string? appVersion = null,
        TimeSpan? startupDelay = null,
        TimeSpan? pollInterval = null)
    {
        _client = client;
        _store = store;
        _updates = updates;
        _verifier = verifier ?? NotificationVerifier.Production;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _appVersion = appVersion ?? AppVersion.Current;
        _startupDelay = startupDelay ?? DefaultStartupDelay;
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _context = SynchronizationContext.Current;
        // Construction does no disk or network work: the window has to paint first.
        _snapshot = Project();
        _updateChanged = (_, _) => Publish();
        _languageChanged = (_, _) => Publish();
        if (_updates is not null) _updates.StatusChanged += _updateChanged;
        LocalizationManager.Current.PropertyChanged += _languageChanged;
    }

    public NotificationSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public event EventHandler<NotificationSnapshot>? SnapshotChanged;

    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed != 0) return;
            _started = true;
        }
        _loop = Task.Run(() => RunAsync(_cancel.Token));
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            LoadState();
            await Task.Delay(_startupDelay, token).ConfigureAwait(false);
            lock (_gate) _fetchAllowed = true;
            while (!token.IsCancellationRequested)
            {
                await PollOnceAsync(token).ConfigureAwait(false);
                await Task.Delay(_pollInterval + TimeSpan.FromSeconds(Random.Shared.Next(0, MaxJitterSeconds + 1)), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    /// <summary>Reads the saved state and checks the cached feed's signature again before trusting it.</summary>
    internal void LoadState()
    {
        var data = _store.Load();
        var rewrite = false;
        lock (_gate)
        {
            _readIds.Clear();
            foreach (var id in (data.ReadIds ?? []).Where(id => id is { Length: > 0 and <= 63 }).Distinct(StringComparer.Ordinal).TakeLast(MaxReadIds))
                _readIds.Add(id);
            DateTimeOffset? published = DateTimeOffset.TryParseExact(data.HighWaterPublishedAtUtc, "yyyy-MM-dd'T'HH:mm:ss'Z'",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;
            _highWater = new NotificationHighWater(Math.Max(0, data.HighWaterEpoch), Math.Max(0, data.HighWaterSeq), published);
            _manifest = null;
            _cachedEnvelope = null;
            _etag = null;
            if (!string.IsNullOrEmpty(data.CachedEnvelopeJson))
            {
                var result = _verifier.Verify(Encoding.UTF8.GetBytes(data.CachedEnvelopeJson), _highWater);
                if (result.Accepted)
                {
                    _manifest = result.Manifest;
                    _cachedEnvelope = data.CachedEnvelopeJson;
                    _etag = data.Etag;
                }
                else rewrite = true;
            }
        }
        if (rewrite) QueuePersist();
        Publish();
    }

    public Task RefreshNowAsync(CancellationToken cancellationToken = default) => PollLinkedAsync(cancellationToken);

    private async Task PollLinkedAsync(CancellationToken cancellationToken)
    {
        if (_disposed != 0) return;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancel.Token);
            await PollOnceAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    internal async Task PollOnceAsync(CancellationToken token)
    {
        if (!await _fetchLock.WaitAsync(0, token).ConfigureAwait(false)) return;
        try
        {
            string? ifNoneMatch;
            lock (_gate)
            {
                ifNoneMatch = _cachedEnvelope is null ? null : _etag;
                _lastAttempt = _clock();
            }
            FeedFetchResult result;
            try { result = await _client.FetchAsync(ifNoneMatch, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { if (token.IsCancellationRequested) throw; return; }
            catch { return; }
            if (result.StatusCode == 200 && result.BodyBytes is { Length: > 0 } body) Accept(body, result.ETag);
        }
        finally { _fetchLock.Release(); }
    }

    private void Accept(byte[] body, string? etag)
    {
        NotificationHighWater high;
        lock (_gate) high = _highWater;
        var result = _verifier.Verify(body, high);
        if (!result.Accepted || result.Manifest is not { } manifest) return;
        lock (_gate)
        {
            var epoch = Math.Max(_highWater.Epoch, manifest.Epoch);
            var seq = manifest.Epoch > _highWater.Epoch ? manifest.Seq : Math.Max(_highWater.Seq, manifest.Seq);
            var published = _highWater.PublishedAtUtc is { } known && known > manifest.PublishedAt ? known : manifest.PublishedAt;
            _highWater = new NotificationHighWater(epoch, seq, published);
            _manifest = manifest;
            _etag = etag;
            _cachedEnvelope = Encoding.UTF8.GetString(body);
        }
        QueuePersist();
        Publish();
    }

    public void BeginPopoverSession()
    {
        bool refresh;
        lock (_gate)
        {
            _sessionFresh = ActiveItems().Where(item => !_readIds.Contains(item.Id)).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            refresh = _fetchAllowed && (_lastAttempt is null || _clock() - _lastAttempt >= RefreshOnOpenAfter);
        }
        Publish();
        if (refresh) _ = PollLinkedAsync(CancellationToken.None);
    }

    public void EndPopoverSession()
    {
        lock (_gate) _sessionFresh = [];
        Publish();
    }

    public void MarkRead(string id)
    {
        lock (_gate)
        {
            if (_readIds.Contains(id) || !ActiveItems().Any(item => item.Id == id)) return;
            AddRead(id);
        }
        QueuePersist();
        Publish();
    }

    public void MarkAllRead()
    {
        var changed = false;
        lock (_gate)
            foreach (var item in ActiveItems())
                if (!_readIds.Contains(item.Id)) { AddRead(item.Id); changed = true; }
        if (!changed) return;
        QueuePersist();
        Publish();
    }

    private void AddRead(string id)
    {
        _readIds.Add(id);
        while (_readIds.Count > MaxReadIds) _readIds.RemoveAt(0);
    }

    private IReadOnlyList<VerifiedNotificationItem> ActiveItems() =>
        _manifest is null ? [] : NotificationVerifier.SelectActive(_manifest, _highWater, _clock(), _appVersion);

    private NotificationSnapshot Project()
    {
        var portuguese = LocalizationManager.Current.Language == LocalizationManager.PortugueseBrazil;
        PinnedUpdateItem? pinned = null;
        if (_updates is { } updates && (updates.HasPendingUpdate || updates.Status.Phase is UpdatePhase.Downloading or UpdatePhase.Ready))
        {
            var status = updates.Status;
            pinned = new PinnedUpdateItem(status.Phase, status.Version, status.Progress, status.Message, updates.HasPendingUpdate);
        }
        var items = ActiveItems().OrderByDescending(item => item.PublishedAt).Select(item => new NotificationDisplayItem(
            item.Id,
            item.Kind,
            portuguese ? item.TitlePtBr ?? item.Title : item.Title,
            portuguese ? item.BodyPtBr ?? item.Body : item.Body,
            item.PublishedAt,
            item.ActionUrl,
            item.ActionUrl is null ? null : portuguese ? item.ActionLabelPtBr ?? item.ActionLabel : item.ActionLabel,
            IsUnread: !_readIds.Contains(item.Id),
            IsSessionFresh: _sessionFresh.Contains(item.Id))).ToList();
        var unread = items.Count(item => item.IsUnread);
        return new NotificationSnapshot(unread, unread + (_updates?.HasPendingUpdate == true ? 1 : 0), pinned, items);
    }

    private void Publish()
    {
        if (_disposed != 0) return;
        NotificationSnapshot next;
        lock (_gate)
        {
            next = Project();
            Volatile.Write(ref _snapshot, next);
        }
        var handler = SnapshotChanged;
        if (handler is null) return;
        if (_context is null || SynchronizationContext.Current == _context) handler(this, next);
        else _context.Post(_ => handler(this, next), null);
    }

    private void QueuePersist()
    {
        lock (_gate)
        {
            var data = new NotificationStateData(
                1, _highWater.Epoch, _highWater.Seq,
                _highWater.PublishedAtUtc?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
                _etag, [.. _readIds], _cachedEnvelope);
            _saveChain = _saveChain.ContinueWith(_ => { try { _store.Save(data); } catch { } }, TaskScheduler.Default);
        }
    }

    /// <summary>Waits for queued writes. Tests and shutdown use it; the UI never does.</summary>
    internal Task FlushAsync()
    {
        lock (_gate) return _saveChain;
    }

    public void Dispose() => DisposeCore();

    public ValueTask DisposeAsync() => new(Task.Run(DisposeCore));

    private void DisposeCore()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cancel.Cancel();
        if (_updates is not null) _updates.StatusChanged -= _updateChanged;
        LocalizationManager.Current.PropertyChanged -= _languageChanged;
        Task[] pending;
        lock (_gate) pending = _loop is null ? [_saveChain] : [_loop, _saveChain];
        try { Task.WaitAll(pending, TimeSpan.FromSeconds(2)); } catch { }
        _client.Dispose();
        _cancel.Dispose();
    }
}

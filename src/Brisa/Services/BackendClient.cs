using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Brisa.Models;

namespace Brisa.Services;

public sealed class BackendClient : IBackendClient
{
    private readonly Process _process;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BackendEnvelope>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _reader;
    private readonly Task _errorReader;
    private Task? _disposeTask;
    private int _disposed;
    private readonly object _transportLock = new();
    private Exception? _terminalError;
    // A caller that supplies its own cancellable token owns its deadline. Only a default
    // token falls back to this bound, so a three-minute command budget is never shortened.
    private readonly TimeSpan _requestTimeout;
    internal const string TimeoutMessage = "The native service did not respond in time. Check the connection status before retrying.";

    public BackendClient(string appDirectory, string dataDirectory) : this(appDirectory, dataDirectory, TimeSpan.FromSeconds(60)) { }

    internal BackendClient(string appDirectory, string dataDirectory, TimeSpan requestTimeout)
    {
        _requestTimeout = requestTimeout;
        var node = Path.Combine(appDirectory, "runtime", "node.exe");
        var script = Path.Combine(appDirectory, "backend", "backend.cjs");
        if (!File.Exists(node) || !File.Exists(script))
            throw new FileNotFoundException("The native backend bundle is incomplete.");

        Directory.CreateDirectory(dataDirectory);
        var start = new ProcessStartInfo(node)
        {
            WorkingDirectory = appDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // The backend speaks UTF-8. Without these, a windowless app falls back to a console code page.
            StandardInputEncoding = new System.Text.UTF8Encoding(false),
            StandardOutputEncoding = new System.Text.UTF8Encoding(false),
            StandardErrorEncoding = new System.Text.UTF8Encoding(false),
            CreateNoWindow = true
        };
        start.ArgumentList.Add(script);
        start.Environment["BRISA_DATA_DIR"] = dataDirectory;
        start.Environment["BRISA_RESOURCE_DIR"] = Path.Combine(appDirectory, "resources");
        start.Environment["ELECTRON_RUN_AS_NODE"] = null;
        _process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the native backend.");
        _reader = ReadResponsesAsync();
        _errorReader = DrainErrorsAsync();
    }

    public Task<NativeSnapshot> SnapshotAsync(CancellationToken cancellationToken = default) =>
        SendAsync("snapshot", new { }, NativeJsonContext.Default.NativeSnapshot, cancellationToken);

    public Task<CommandResult> CommandAsync(string command, object payload, CancellationToken cancellationToken = default) =>
        SendAsync(command, payload, NativeJsonContext.Default.CommandResult, cancellationToken);

    public async Task CancelAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            await CommandAsync("cancel", new { }, timeout.Token);
            // Abort acknowledgement is not a cleanup acknowledgement. A refused waitForIdle (for
            // example an installer whose supervision was lost) must not look like a clean cancel.
            var idle = await CommandAsync("waitForIdle", new { }, timeout.Token);
            if (!idle.Success)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(idle.Message)
                    ? "The native backend did not confirm that it became idle. Brisa will stay open."
                    : idle.Message);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            // A silent cancellation is indistinguishable from a clean one to the caller, so a
            // deadline that expires without confirmation is reported as a failure instead.
            throw new TimeoutException("The native backend did not confirm that it became idle. Brisa will stay open.");
        }
    }

    public async Task<string> DiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        var envelope = await SendEnvelopeAsync("diagnostics", new { }, cancellationToken);
        return envelope.Result.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "";
    }

    private async Task<T> SendAsync<T>(string command, object payload, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, CancellationToken token)
    {
        var envelope = await SendEnvelopeAsync(command, payload, token);
        return envelope.Result.Deserialize(typeInfo) ?? throw new InvalidDataException("Backend returned an empty result.");
    }

    private async Task<BackendEnvelope> SendEnvelopeAsync(string command, object payload, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<BackendEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_transportLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_terminalError is not null) throw _terminalError;
            if (!_pending.TryAdd(id, completion)) throw new InvalidOperationException("Duplicate request id.");
        }
        var bounded = !token.CanBeCanceled;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        if (bounded) deadline.CancelAfter(_requestTimeout);
        var effective = deadline.Token;
        using var registration = effective.Register(() => completion.TrySetCanceled(effective));
        try
        {
            // Never emit a frame for a request that is already abandoned.
            effective.ThrowIfCancellationRequested();
            var line = NdjsonProtocol.SerializeRequest(id, command, payload);
            await _writeLock.WaitAsync(effective);
            try
            {
                await _process.StandardInput.WriteLineAsync(line.AsMemory(), effective);
                await _process.StandardInput.FlushAsync(effective);
            }
            catch (OperationCanceledException) when (bounded && !token.IsCancellationRequested && !_lifetime.IsCancellationRequested)
            { throw new TimeoutException(TimeoutMessage); }
            catch (IOException) { var failure = new BackendUnavailableException(); FailPending(failure); throw failure; }
            finally { _writeLock.Release(); }
            BackendEnvelope response;
            try { response = await completion.Task; }
            catch (OperationCanceledException) when (bounded && !token.IsCancellationRequested && !_lifetime.IsCancellationRequested)
            { throw new TimeoutException(TimeoutMessage); }
            if (!response.Ok) throw new InvalidOperationException(string.IsNullOrWhiteSpace(response.Error) ? "Backend request failed." : response.Error);
            return response;
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task ReadResponsesAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested && await _process.StandardOutput.ReadLineAsync(_lifetime.Token) is { } line)
            {
                BackendEnvelope? response;
                try { response = NdjsonProtocol.ParseResponse(line); }
                catch (InvalidDataException)
                {
                    // A frame we cannot parse is either one request's bad luck or a broken
                    // stream. Only a single recoverable id makes it that request's problem;
                    // anything else poisons the transport so no caller waits forever.
                    if (NdjsonProtocol.TryRecoverRequestId(line, out var recoveredId)
                        && _pending.TryGetValue(recoveredId, out var faulty))
                    { faulty.TrySetException(new InvalidDataException("The native service returned an unreadable response.")); continue; }
                    FailPending(new BackendUnavailableException());
                    return;
                }
                if (response is not null && _pending.TryGetValue(response.Id, out var completion)) completion.TrySetResult(response);
            }
            FailPending(new BackendUnavailableException());
        }
        catch (OperationCanceledException) { }
        catch (Exception) { FailPending(new BackendUnavailableException()); }
    }

    private async Task DrainErrorsAsync()
    {
        try { while (!_lifetime.IsCancellationRequested && await _process.StandardError.ReadLineAsync(_lifetime.Token) is not null) { } }
        catch { }
    }

    private void FailPending(Exception ex)
    {
        lock (_transportLock)
        {
            _terminalError ??= ex;
            foreach (var item in _pending.Values) item.TrySetException(_terminalError);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_transportLock)
        {
            if (_disposeTask is null)
            {
                Interlocked.Exchange(ref _disposed, 1);
                _disposeTask = Task.Run(DisposeCoreAsync);
            }
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        FailPending(new ObjectDisposedException(nameof(BackendClient)));
        _lifetime.Cancel();
        // Discord is intentionally relaunched on the normal route before disposal.
        // It is not a backend-owned worker and must survive Brisa exiting.
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: false); } catch { }
        try { await Task.WhenAll(_reader, _errorReader).ConfigureAwait(false); } catch { }
        _process.Dispose(); _writeLock.Dispose(); _lifetime.Dispose();
    }
}

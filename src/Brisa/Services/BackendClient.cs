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
    private int _disposed;

    public BackendClient(string appDirectory, string dataDirectory)
    {
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
            CreateNoWindow = true
        };
        start.ArgumentList.Add(script);
        start.Environment["BRISA_DATA_DIR"] = dataDirectory;
        start.Environment["BRISA_RESOURCE_DIR"] = Path.Combine(appDirectory, "resources");
        start.Environment["ELECTRON_RUN_AS_NODE"] = null;
        _process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the native backend.");
        _reader = ReadResponsesAsync();
        _ = DrainErrorsAsync();
    }

    public Task<NativeSnapshot> SnapshotAsync(CancellationToken cancellationToken = default) =>
        SendAsync("snapshot", new { }, NativeJsonContext.Default.NativeSnapshot, cancellationToken);

    public Task<CommandResult> CommandAsync(string command, object payload, CancellationToken cancellationToken = default) =>
        SendAsync(command, payload, NativeJsonContext.Default.CommandResult, cancellationToken);

    public async Task CancelAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await CommandAsync("cancel", new { }, timeout.Token); } catch { }
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
        if (!_pending.TryAdd(id, completion)) throw new InvalidOperationException("Duplicate request id.");
        using var registration = token.Register(() => completion.TrySetCanceled(token));
        try
        {
            var line = NdjsonProtocol.SerializeRequest(id, command, payload);
            await _writeLock.WaitAsync(token);
            try { await _process.StandardInput.WriteLineAsync(line.AsMemory(), token); await _process.StandardInput.FlushAsync(token); }
            finally { _writeLock.Release(); }
            var response = await completion.Task;
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
                catch (InvalidDataException) { continue; }
                if (response is not null && _pending.TryGetValue(response.Id, out var completion)) completion.TrySetResult(response);
            }
            FailPending(new EndOfStreamException("Native backend closed unexpectedly."));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { FailPending(ex); }
    }

    private async Task DrainErrorsAsync()
    {
        try { while (!_lifetime.IsCancellationRequested && await _process.StandardError.ReadLineAsync(_lifetime.Token) is not null) { } }
        catch { }
    }

    private void FailPending(Exception ex) { foreach (var item in _pending.Values) item.TrySetException(ex); }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        FailPending(new ObjectDisposedException(nameof(BackendClient)));
        _lifetime.Cancel();
        try { if (!_process.HasExited) _process.Kill(true); } catch { }
        try { await _reader; } catch { }
        _process.Dispose(); _writeLock.Dispose(); _lifetime.Dispose();
    }
}

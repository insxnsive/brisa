using System.Text.Json;
using System.IO;
using Brisa.Models;
using Brisa.Security;
using Brisa.Services;

var tests = new (string Name, Action Run)[]
{
    ("connected state disconnects", () =>
    {
        var state = HomeState.FromSnapshot(new(true, false, true, true, "u", new("s", "BR")));
        Assert(state.Phase == ConnectionPhase.Connected && state.PrimaryLabel == "Disconnect" && state.PrimaryEnabled);
    }),
    ("unverified owned tunnel offers Disconnect without claiming Connected", () =>
    {
        var snapshot = JsonSerializer.Deserialize<NativeSnapshot>("{\"connected\":false,\"tunnelActive\":true,\"externalTunnel\":false,\"reliable\":true,\"signedIn\":false,\"username\":\"\",\"route\":null}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var state = HomeState.FromSnapshot(snapshot);
        Assert(state.StatusText != "Connected" && state.PrimaryLabel == "Disconnect" && state.PrimaryEnabled);
        var backend = new UiTestBackendClient(snapshot);
        ExitGuard.StopOwnedAsync(backend, false).GetAwaiter().GetResult();
        Assert(backend.Commands.Contains("disconnect"));
    }),
    ("external tunnel blocks mutations", () =>
    {
        var state = HomeState.FromSnapshot(new(false, true, true, false, "", null));
        Assert(state.Phase == ConnectionPhase.Blocked && !state.PrimaryEnabled && state.Detail!.Contains("Another app"));
    }),
    ("unreliable inspection fails closed", () => Assert(!HomeState.FromSnapshot(new(false, false, false, false, "", null)).PrimaryEnabled)),
    ("ndjson parses exact envelope", () =>
    {
        var frame = NdjsonProtocol.ParseResponse("{\"id\":\"a\",\"ok\":true,\"result\":{\"success\":true}}");
        Assert(frame.Id == "a" && frame.Ok && frame.Result.GetProperty("success").GetBoolean());
        Throws<InvalidDataException>(() => NdjsonProtocol.ParseResponse("{\"id\":\"a\"}\n{}"));
        Throws<InvalidDataException>(() => NdjsonProtocol.ParseResponse("not json"));
    }),
    ("ndjson request keeps secrets in framed payload", () =>
    {
        var frame = NdjsonProtocol.SerializeRequest("id-1", "login", new { username = "user", password = "private", twoFactorCode = "123456" });
        Assert(!frame.Contains('\n') && !frame.Contains('\r'));
        using var json = JsonDocument.Parse(frame);
        Assert(json.RootElement.GetProperty("command").GetString() == "login" && json.RootElement.GetProperty("payload").GetProperty("password").GetString() == "private");
    }),
    ("optional request fields are omitted instead of rejected nulls", () =>
    {
        var frame = NdjsonProtocol.SerializeRequest("optional", "login", new { username = "fixture", password = "fixture-only", twoFactorCode = (string?)null, humanVerificationToken = (string?)null });
        using var json = JsonDocument.Parse(frame);
        Assert(!json.RootElement.GetProperty("payload").TryGetProperty("twoFactorCode", out _));
        Assert(!json.RootElement.GetProperty("payload").TryGetProperty("humanVerificationToken", out _));
    }),
    ("temporary WebView profiles are cleaned after file locks release", () =>
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "native-cleanup-test-" + Guid.NewGuid().ToString("N"));
        var profile = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        var file = Path.Combine(profile, "synthetic-cache");
        File.WriteAllText(file, "fixture-only");
        using (var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            VerificationProfileStore.Cleanup(root);
            Assert(Directory.Exists(profile));
        }
        VerificationProfileStore.Cleanup(root);
        Assert(!Directory.Exists(profile));
        Directory.Delete(root);
    }),
    ("failed startup offers Exit instead of a disabled Connect", () =>
    {
        var state = new HomeState(ConnectionPhase.Error, null);
        Assert(state.PrimaryEnabled && state.PrimaryLabel == "Exit Brisa");
    }),
    ("idle exit is allowed when the backend never started", () =>
    {
        ExitGuard.StopOwnedAsync(new UnavailableBackendClient(), false).GetAwaiter().GetResult();
    }),
    ("exit disconnects only the native-owned tunnel", () =>
    {
        var backend = new UiTestBackendClient(new(true, false, true, true, "fixture", null));
        ExitGuard.StopOwnedAsync(backend, true).GetAwaiter().GetResult();
        Assert(!backend.SnapshotAsync().Result.Connected && backend.Commands.Contains("disconnect"));
        var external = new UiTestBackendClient(new(false, true, true, false, "", null));
        ExitGuard.StopOwnedAsync(external, false).GetAwaiter().GetResult();
        Assert(!external.Commands.Contains("disconnect"));
    }),
    ("exit fails closed for an unreliable connected final snapshot", () =>
    {
        var backend = new UiTestBackendClient(new(true, false, false, false, "", null));
        Throws<InvalidOperationException>(() => ExitGuard.StopOwnedAsync(backend, false).GetAwaiter().GetResult());
        Assert(!backend.Commands.Contains("disconnect"));
    }),
    ("exit retry joins a timed-out cancellation", () =>
    {
        var backend = new DelayedCancelBackend();
        Throws<TimeoutException>(() => ExitGuard.StopOwnedAsync(backend, false).GetAwaiter().GetResult());
        var retry = ExitGuard.StopOwnedAsync(backend, false);
        Assert(backend.CancelCalls == 1 && !retry.IsCompleted);
        backend.ReleaseCancel.TrySetResult();
        retry.GetAwaiter().GetResult();
        Assert(backend.CancelCalls == 1 && backend.PeakCancellations == 1);
    }),
    ("malformed verification fields are rejected without throwing", () =>
    {
        VerificationPolicy.TryParseChallenge("https://vpn-api.proton.me/core/v4/captcha?Token=abc123", out var challenge);
        Assert(!VerificationPolicy.TryParseMessage("{\"type\":42}", challenge!, challenge!.Url.AbsoluteUri, out _));
        Assert(!VerificationPolicy.TryParseMessage("{\"type\":\"proton_captcha\",\"token\":42}", challenge!, challenge.Url.AbsoluteUri, out _));
    }),
    ("ownership challenge is exact", () =>
    {
        const string url = "https://verify.proton.me/?token=CHALLENGE&methods=ownership-email%2Cownership-sms&embed=1&vpn=1";
        Assert(VerificationPolicy.TryParseChallenge(url, out var challenge) && challenge!.Methods.SequenceEqual(["ownership-email", "ownership-sms"]));
        Assert(!VerificationPolicy.TryParseChallenge(url.Replace("https://", "http://"), out _));
        Assert(!VerificationPolicy.TryParseChallenge(url + "&next=https://evil.test", out _));
        Assert(!VerificationPolicy.IsExactNavigation("https://verify.proton.me/", challenge!));
    }),
    ("captcha challenge host and query are exact", () =>
    {
        Assert(VerificationPolicy.TryParseChallenge("https://vpn-api.proton.me/core/v4/captcha?Token=abc123", out _));
        Assert(!VerificationPolicy.TryParseChallenge("https://proton.me/core/v4/captcha?Token=abc123", out _));
        Assert(!VerificationPolicy.TryParseChallenge("https://vpn-api.proton.me/core/v4/captcha?Token=abc123&x=1", out _));
        Assert(!VerificationPolicy.TryParseChallenge("https://user@vpn-api.proton.me/core/v4/captcha?Token=abc123", out _));
    }),
    ("serialized ownership message validates source and method", () =>
    {
        VerificationPolicy.TryParseChallenge("https://verify.proton.me/?token=CHALLENGE&methods=ownership-email%2Cownership-sms&embed=1&vpn=1", out var challenge);
        var json = JsonSerializer.Serialize(new { type = "HUMAN_VERIFICATION_SUCCESS", payload = new { token = "opaque", type = "ownership-email" } });
        const string source = "https://verify.proton.me/?token=CHALLENGE&methods=ownership-email%2Cownership-sms&embed=1&vpn=1";
        Assert(VerificationPolicy.TryParseMessage(json, challenge!, source, out var answer) && answer!.Method == "ownership-email");
        Assert(!VerificationPolicy.TryParseMessage(json, challenge!, "https://evil.test/", out _));
        Assert(!VerificationPolicy.TryParseMessage(json.Replace("ownership-email", "push"), challenge!, source, out _));
    }),
    ("captcha message binds token prefix", () =>
    {
        VerificationPolicy.TryParseChallenge("https://vpn-api.proton.me/core/v4/captcha?Token=abc123", out var challenge);
        const string source = "https://vpn-api.proton.me/core/v4/captcha?Token=abc123";
        Assert(VerificationPolicy.TryParseMessage("{\"type\":\"proton_captcha\",\"token\":\"abc123:answer\"}", challenge!, source, out _));
        Assert(!VerificationPolicy.TryParseMessage("{\"type\":\"proton_captcha\",\"token\":\"other:answer\"}", challenge!, source, out _));
    }),
    ("isolated launches disable all updater work", () =>
    {
        Assert(!UpdateLaunchPolicy.ShouldStart(["--ui-test"]));
        Assert(!UpdateLaunchPolicy.ShouldStart(["--smoke-test", "fixture.png"]));
        Assert(UpdateLaunchPolicy.ShouldStart([]));
    }),
    ("only newer semantic release versions are staged", () =>
    {
        var older = new FixtureUpdateClient("0.1.0-beta.1", "0.1.0-alpha.9");
        using (var service = new UpdateService(older, TimeSpan.FromHours(6)))
            service.CheckNowAsync().GetAwaiter().GetResult();
        Assert(older.DownloadCalls == 0);

        var newer = new FixtureUpdateClient("0.1.0-beta.1", "0.1.0-beta.2");
        using (var service = new UpdateService(newer, TimeSpan.FromHours(6)))
            service.CheckNowAsync().GetAwaiter().GetResult();
        Assert(newer.DownloadCalls == 1);
    }),
    ("updater checks are single flight", () =>
    {
        var client = new FixtureUpdateClient("0.1.0-beta.1", "0.1.0-beta.2") { HoldCheck = true };
        using var service = new UpdateService(client, TimeSpan.FromHours(6));
        var first = service.CheckNowAsync();
        client.CheckStarted.Task.Wait(TimeSpan.FromSeconds(2));
        var second = service.CheckNowAsync();
        second.GetAwaiter().GetResult();
        Assert(client.CheckCalls == 1 && !first.IsCompleted);
        client.ReleaseCheck.TrySetResult();
        first.GetAwaiter().GetResult();
    }),
    ("updater lifetime is cancellable and cadence is six hours", () =>
    {
        Assert(UpdateService.DefaultCheckInterval == TimeSpan.FromHours(6));
        var client = new FixtureUpdateClient("0.1.0-beta.1", null) { HoldCheck = true };
        var service = new UpdateService(client, TimeSpan.FromHours(6));
        service.Start();
        Assert(client.CheckStarted.Task.Wait(TimeSpan.FromSeconds(2)));
        service.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        Assert(client.CheckCancelled);
    }),
    ("updater status never exposes exception details", () =>
    {
        var client = new FixtureUpdateClient("0.1.0-beta.1", null) { Failure = new InvalidOperationException("secret fixture path C:\\private\\token") };
        using var service = new UpdateService(client, TimeSpan.FromHours(6));
        service.CheckNowAsync().GetAwaiter().GetResult();
        Assert(service.Status.Phase == UpdatePhase.Error);
        Assert(!service.Status.Message.Contains("private", StringComparison.OrdinalIgnoreCase));
    })
};

var failures = new List<string>();
foreach (var test in tests) try { test.Run(); Console.WriteLine($"PASS {test.Name}"); } catch (Exception ex) { failures.Add($"FAIL {test.Name}: {ex.Message}"); }
foreach (var failure in failures) Console.Error.WriteLine(failure);
Console.WriteLine($"{tests.Length - failures.Count}/{tests.Length} tests passed");
return failures.Count == 0 ? 0 : 1;

static void Assert(bool condition) { if (!condition) throw new InvalidOperationException("assertion failed"); }
static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException($"expected {typeof(T).Name}"); }

sealed class FixtureUpdateClient(string currentVersion, string? offeredVersion) : IUpdateClient
{
    public string CurrentVersion { get; } = currentVersion;
    public bool HoldCheck { get; set; }
    public Exception? Failure { get; set; }
    public int CheckCalls { get; private set; }
    public int DownloadCalls { get; private set; }
    public bool CheckCancelled { get; private set; }
    public TaskCompletionSource CheckStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseCheck { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<UpdateRelease?> CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        CheckCalls++;
        CheckStarted.TrySetResult();
        if (Failure is not null) throw Failure;
        if (HoldCheck)
        {
            try { await ReleaseCheck.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { CheckCancelled = true; throw; }
        }
        return offeredVersion is null ? null : new UpdateRelease(offeredVersion, offeredVersion);
    }

    public Task DownloadUpdatesAsync(UpdateRelease release, Action<int>? progress, CancellationToken cancellationToken)
    {
        DownloadCalls++;
        progress?.Invoke(100);
        return Task.CompletedTask;
    }

    public bool HasPendingUpdate => DownloadCalls > 0;
    public void PrepareApply(bool restart) { }
}

sealed class DelayedCancelBackend : IBackendClient
{
    public int CancelCalls { get; private set; }
    public int PeakCancellations { get; private set; }
    private int activeCancellations;
    public TaskCompletionSource ReleaseCancel { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task CancelAsync()
    {
        CancelCalls++;
        activeCancellations++;
        PeakCancellations = Math.Max(PeakCancellations, activeCancellations);
        try { await ReleaseCancel.Task; } finally { activeCancellations--; }
    }
    public Task<NativeSnapshot> SnapshotAsync(CancellationToken token = default) => Task.FromResult(new NativeSnapshot(false, false, true, false, "", null));
    public Task<CommandResult> CommandAsync(string command, object payload, CancellationToken token = default) => Task.FromResult(new CommandResult(true));
    public Task<string> DiagnosticsAsync(CancellationToken token = default) => Task.FromResult("Fixture");
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

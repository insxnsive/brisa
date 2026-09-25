using System.IO;
using System.Text.Json;
using System.Windows;
using Brisa.Services;

internal static class Program
{
    private static int _checks;
    [STAThread]
    private static int Main()
    {
#pragma warning disable WPF0001
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
#pragma warning restore WPF0001
        try
        {
            SettingsFailures();
            PendingDownloadFailures().GetAwaiter().GetResult();
            LifecycleRaces().GetAwaiter().GetResult();
            FaultySubscriber().GetAwaiter().GetResult();
            Console.WriteLine($"PASS {_checks} lifecycle assertions");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Application.Current?.Shutdown(); }
    }

    private static void SettingsFailures()
    {
        var root = Path.Combine(Path.GetTempPath(), "brisa-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var original = new UserSettings(AppTheme.Light);
            var path = Path.Combine(root, "settings.json");
            File.WriteAllText(path, JsonSerializer.Serialize(original));
            var store = new SettingsStore(root, false);
            File.Delete(path);
            Directory.CreateDirectory(path);
            Throws(() => store.Save(new(AppTheme.Dark)), "failed file write");
            Check(store.Current == original && Directory.Exists(path), "failed write keeps Current and prior path");
            Directory.Delete(path);
            File.WriteAllText(path, JsonSerializer.Serialize(original));
            var before = File.ReadAllBytes(path);
            var registration = new SettingsStore(root, true, _ => throw new IOException("fixture registration failure"));
            Throws(() => registration.Save(new(AppTheme.Dark, true)), "failed startup registration");
            Check(registration.Current == original && before.SequenceEqual(File.ReadAllBytes(path)), "failed registration keeps file and Current");
            var startupChanges = new List<bool>();
            var replaceFailure = new SettingsStore(root, true, startupChanges.Add);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                Throws(() => replaceFailure.Save(new(AppTheme.Dark, true)), "failed atomic replacement");
            Check(startupChanges.SequenceEqual(new[] { true, false }), "failed replacement restores prior startup registration");
            Check(replaceFailure.Current == original && before.SequenceEqual(File.ReadAllBytes(path)), "failed replacement preserves file and Current");
            store.Save(new(AppTheme.Dark));
            Check(store.Current.Theme == AppTheme.Dark && JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path))?.Theme == AppTheme.Dark,
                "successful replacement updates file and Current");
            Check(!Directory.GetFiles(root, "*.tmp").Any(), "temporary files cleaned");
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task PendingDownloadFailures()
    {
        foreach (var cancel in new[] { false, true })
        {
            var client = new GatedClient { Pending = true, FailDownload = !cancel, CancelDownload = cancel };
            using var cancellation = new CancellationTokenSource();
            if (cancel) client.BeforeDownload = cancellation.Cancel;
            await using var service = new UpdateService(client);
            await service.CheckNowAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(3));
            Check(service.Status.Phase == UpdatePhase.Ready, "pending update survives " + (cancel ? "cancellation" : "failure"));
        }
    }

    private static async Task FaultySubscriber()
    {
        await using var service = new UpdateService(new GatedClient());
        var delivered = 0;
        service.StatusChanged += (_, _) => throw new InvalidOperationException("fixture subscriber failure");
        service.StatusChanged += (_, _) => delivered++;
        await service.CheckNowAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Check(delivered == 3 && service.Status.Phase == UpdatePhase.Ready, "faulty subscriber cannot break update completion or starve later subscribers");
    }

    private static async Task LifecycleRaces()
    {
        for (var i = 0; i < 24; i++)
        {
            var client = new GatedClient { GateCheck = true };
            var service = new UpdateService(client);
            var notifications = 0;
            service.StatusChanged += (_, _) => Interlocked.Increment(ref notifications);
            try
            {
                Task check;
                if (i % 2 == 0) check = service.CheckNowAsync();
                else { service.Start(); check = Task.CompletedTask; }
                await client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var atDisposal = Volatile.Read(ref notifications);
                var dispose1 = service.DisposeAsync().AsTask();
                var dispose2 = service.DisposeAsync().AsTask();
                Check(ReferenceEquals(dispose1, dispose2), "concurrent disposal joins same task");
                var late = service.CheckNowAsync();
                service.Start();
                client.Release.TrySetResult();
                await Task.WhenAll(check, late, dispose1, dispose2).WaitAsync(TimeSpan.FromSeconds(3));
                Check(Volatile.Read(ref notifications) == atDisposal, "disposal prevents late update notifications");
            }
            finally
            {
                client.Release.TrySetResult();
                await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            }
        }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); _checks++; }
    private static void Throws(Action action, string message)
    {
        try { action(); } catch (IOException) { return; } catch (UnauthorizedAccessException) { return; }
        throw new Exception("Expected " + message);
    }

    private sealed class GatedClient : IUpdateClient
    {
        public bool Pending, GateCheck, FailDownload, CancelDownload;
        public Action? BeforeDownload;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string CurrentVersion => "0.1.0";
        public bool HasPendingUpdate => Pending;
        public async Task<UpdateRelease?> CheckForUpdatesAsync(CancellationToken token)
        {
            Entered.TrySetResult();
            if (GateCheck) await Release.Task;
            return new UpdateRelease("0.2.0", new object());
        }
        public Task DownloadUpdatesAsync(UpdateRelease release, Action<int>? progress, CancellationToken token)
        {
            BeforeDownload?.Invoke();
            if (CancelDownload) throw new OperationCanceledException(token);
            if (FailDownload) throw new IOException("fixture download failure");
            Pending = true;
            return Task.CompletedTask;
        }
        public void PrepareApply(bool restart) { }
    }
}

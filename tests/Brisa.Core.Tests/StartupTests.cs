using System.IO;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Brisa.Localization;
using Brisa.Models;
using Brisa.Services;

/// <summary>
/// Restart as administrator, the sign-in registration, and the single-instance handshake. Nothing here
/// touches the real Local\Brisa, the real Run key or the real Task Scheduler: names are random, and
/// Windows is replaced by fakes.
/// </summary>
static class StartupTests
{
    private const string UserSidExample = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string OtherSidExample = "S-1-5-21-1111111111-2222222222-3333333333-1002";
    private const string BrisaPath = @"C:\Program Files\Brisa\current\Brisa.exe";

    public static (string Name, Action Run)[] All() =>
    [
        ("startup: limited with no tunnel offers Restart as administrator", () => English(() =>
        {
            var state = HomeState.FromSnapshot(new NativeSnapshot(false, false, false, true, "u", null, RequiresElevation: true));
            Check(state.Phase == ConnectionPhase.Limited && state.CanRestartElevated && state.PrimaryEnabled && state.PrimaryLabel == "Restart as administrator", "limited restart");
            Check(!state.StartupFailed && state.Detail!.Contains("Restart as administrator"), "the detail points at the button");
        })),
        ("startup: limited with a tunnel that may be ours stays disabled", () => English(() =>
        {
            foreach (var snapshot in new[]
            {
                new NativeSnapshot(false, false, true, true, "u", null, OwnedTunnelPresent: true, RequiresElevation: true),
                new NativeSnapshot(false, false, true, true, "u", null, TunnelActive: true, RequiresElevation: true),
                new NativeSnapshot(true, false, true, true, "u", null, RequiresElevation: true),
            })
            {
                var state = HomeState.FromSnapshot(snapshot);
                Check(state.Phase == ConnectionPhase.Limited && !state.CanRestartElevated && !state.PrimaryEnabled && state.PrimaryLabel == "Connect", "limited with an owned tunnel");
            }
        })),
        ("startup: limited beside another tunnel is blocked, and other phases never offer a restart", () => English(() =>
        {
            var external = HomeState.FromSnapshot(new NativeSnapshot(false, true, true, true, "u", null, RequiresElevation: true));
            Check(external.Phase == ConnectionPhase.Blocked && !external.CanRestartElevated && !external.PrimaryEnabled, "external tunnel");
            var ready = HomeState.FromSnapshot(new NativeSnapshot(false, false, true, true, "u", null));
            Check(ready.Phase == ConnectionPhase.Disconnected && !ready.CanRestartElevated && ready.PrimaryLabel == "Connect" && ready.PrimaryEnabled, "ordinary disconnected");
            var failed = new HomeState(ConnectionPhase.Error, null);
            Check(!failed.CanRestartElevated && failed.PrimaryEnabled && failed.StartupFailed, "a failed start still offers Exit");
        })),

        ("startup: the first instance is primary, later ones are not, and Dispose frees the name", () =>
        {
            var prefix = TestPrefix();
            var first = new SingleInstanceActivation(prefix);
            var second = new SingleInstanceActivation(prefix);
            Check(first.IsPrimary && !second.IsPrimary, "first and second");
            var third = new SingleInstanceActivation(prefix);
            Check(!third.IsPrimary, "third while first is alive");
            // Later launches exit; only then is the name really free.
            second.Dispose();
            third.Dispose();
            first.Dispose();
            using var fourth = new SingleInstanceActivation(prefix);
            Check(fourth.IsPrimary, "a new instance becomes primary once the first has released");
            first.Dispose();
        }),
        ("startup: a signal reaches the primary exactly once per Set", () =>
        {
            var prefix = TestPrefix();
            using var primary = new SingleInstanceActivation(prefix);
            var fired = 0;
            primary.Listen(() => Interlocked.Increment(ref fired));
            Check(SingleInstanceActivation.SignalPrimary(prefix), "signal accepted");
            SpinUntil(() => Volatile.Read(ref fired) == 1, "first signal");
            Thread.Sleep(200);
            Check(Volatile.Read(ref fired) == 1, "one signal, one callback");
            Check(SingleInstanceActivation.SignalPrimary(prefix), "second signal accepted");
            SpinUntil(() => Volatile.Read(ref fired) == 2, "second signal");
            Check(!SingleInstanceActivation.SignalPrimary(TestPrefix()), "no primary under another name means nothing to wake");
            primary.Dispose();
            Check(!SingleInstanceActivation.SignalPrimary(prefix), "a released primary cannot be signaled");
        }),
        ("startup: the activation event and the mutex carry a protected descriptor for the current user only", () =>
        {
            var prefix = TestPrefix();
            using var primary = new SingleInstanceActivation(prefix);
            var sid = SingleInstanceActivation.UserSid;
            var eventSddl = primary.ActivationSddl!;
            Check(eventSddl.StartsWith("D:P", StringComparison.Ordinal), "event descriptor is protected: " + eventSddl);
            Check(eventSddl.Contains("(A;;0x100002;;;" + sid + ")") || eventSddl.Contains("(A;;0x100002;;;LA)"), "event grants Modify and Synchronize to the user: " + eventSddl);
            Check(CountAces(eventSddl) == 1, "one entry on the event: " + eventSddl);
            var mutexSddl = primary.MutexSddl;
            Check(mutexSddl.StartsWith("D:P", StringComparison.Ordinal) && (mutexSddl.Contains(";;;" + sid + ")") || mutexSddl.Contains(";;;LA)")) && CountAces(mutexSddl) == 1, "mutex descriptor: " + mutexSddl);
            foreach (var sddl in new[] { eventSddl, mutexSddl })
            {
                Check(!sddl.Contains(";WD)") && !sddl.Contains("S-1-1-0") && !sddl.Contains(";AU)") && !sddl.Contains(";BU)") && !sddl.Contains(";BA)"),
                    "no Everyone, Authenticated Users, Users or Administrators entry: " + sddl);
            }
            Check(new SecurityIdentifier(sid).IsAccountSid(), "the entry is an account SID, not a well known group");
        }),
        ("startup: a relaunch waits for its predecessor and takes over when it leaves", () =>
        {
            var prefix = TestPrefix();
            var first = new SingleInstanceActivation(prefix);
            Check(first.IsPrimary, "predecessor is primary");
            // A mutex is re-entrant for the thread that owns it, so the second process is played by another thread.
            var impatient = Task.Run(() =>
            {
                using var late = new SingleInstanceActivation(prefix, waitForPredecessor: true, waitBudget: TimeSpan.FromMilliseconds(150));
                return late.IsPrimary;
            });
            Check(impatient.Wait(TimeSpan.FromSeconds(10)) && !impatient.Result, "gives up when the predecessor never leaves");
            // The new process waits on its own thread, which then owns the mutex and releases it.
            var successor = Task.Run(() =>
            {
                using var next = new SingleInstanceActivation(prefix, waitForPredecessor: true, waitBudget: TimeSpan.FromSeconds(10));
                return next.IsPrimary;
            });
            Thread.Sleep(250);
            Check(!successor.IsCompleted, "still waiting");
            first.Dispose();
            Check(successor.Wait(TimeSpan.FromSeconds(10)) && successor.Result, "takes over once the predecessor has released");
        }),
        ("startup: a predecessor that died without releasing still hands over the mutex", () =>
        {
            var prefix = TestPrefix();
            // A process that died while holding the mutex: its thread ends without releasing it. The raw mutex
            // stands in for it, because a dead process would also have closed its event.
            Mutex? orphan = null;
            var thread = new Thread(() => orphan = new Mutex(true, prefix));
            thread.Start();
            thread.Join();
            Check(orphan is not null, "the dead thread created the mutex");
            using var successor = new SingleInstanceActivation(prefix, waitForPredecessor: true, waitBudget: TimeSpan.FromSeconds(5));
            Check(successor.IsPrimary, "an abandoned mutex counts as acquired");
            orphan!.Dispose();
        }),

        ("startup: wait-for-pid must be digits only, above zero, and not this process", () =>
        {
            foreach (var bad in new[] { "0", "-1", "abc", "1e3", "", " 12", "12 ", "+12", "99999999999", "1_000", "0x10", ((char)0xFF11).ToString() + ((char)0xFF12), "12\n", "4294967296" })
                Check(!LaunchArguments.TryParsePid(bad, 4242, out _), "must refuse pid [" + bad + "]");
            Check(!LaunchArguments.TryParsePid("4242", 4242, out _), "own pid");
            Check(LaunchArguments.TryParsePid("1234", 4242, out var pid) && pid == 1234, "a plain pid");
        }),
        ("startup: only an ordinary account SID is accepted, and the current one must match", () =>
        {
            foreach (var bad in new[] { "S-1-1-0", "S-1-5-18", "S-1-5-32-544", "S-1-5-11", "S-1-5-21-", "S-1-5-21-1-2-3-1001\n", " S-1-5-21-1-2-3-1001", "s-1-5-21-1-2-3-1001", "S-1-5-21-1-2-3-1001;", "", "S-1-5-21-" + new string('1', 200) })
                Check(!LaunchArguments.IsAcceptableSid(bad), "must refuse SID [" + bad + "]");
            Check(LaunchArguments.IsAcceptableSid(UserSidExample), "a domain or machine account SID");
            var ok = LaunchArguments.Parse(["--startup", "--expect-user-sid", UserSidExample], 1, UserSidExample);
            Check(!ok.RefuseUser && ok.WaitForPid is null, "matching SID");
            Check(LaunchArguments.Parse(["--expect-user-sid", OtherSidExample], 1, UserSidExample).RefuseUser, "a different account is refused");
            Check(LaunchArguments.Parse(["--expect-user-sid", "S-1-1-0"], 1, UserSidExample).RefuseUser, "Everyone is refused");
            Check(LaunchArguments.Parse(["--expect-user-sid"], 1, UserSidExample).RefuseUser, "a missing value is refused");
            Check(!LaunchArguments.Parse(["--startup"], 1, UserSidExample).RefuseUser, "no flag, no refusal");
            var wait = LaunchArguments.Parse(["--wait-for-pid", "777", "--expect-user-sid", UserSidExample], 1, UserSidExample);
            Check(wait.WaitForPid == 777 && !wait.RefuseUser, "a pid and the matching SID");
            Check(LaunchArguments.Parse(["--wait-for-pid", "abc"], 1, UserSidExample).WaitForPid is null, "a bad pid is ignored");
            Check(LaunchArguments.Parse(["--wait-for-pid", "1"], 1, UserSidExample).WaitForPid is null, "own pid is ignored");
        }),
        ("startup: the helper accepts exactly one argument shape", () =>
        {
            Check(StartupTaskHelper.TryParse(["--sync-startup-task", "enable", "--expect-user-sid", UserSidExample], out var verb, out var sid) && verb == "enable" && sid == UserSidExample, "enable");
            Check(StartupTaskHelper.TryParse(["--SYNC-STARTUP-TASK", "Disable", "--EXPECT-USER-SID", UserSidExample], out verb, out _) && verb == "disable", "case is ignored");
            foreach (var bad in new[]
            {
                new[] { "--sync-startup-task", "enable", "--expect-user-sid", UserSidExample, "--extra" },
                new[] { "--sync-startup-task", "enable", "--expect-user-sid" },
                new[] { "--sync-startup-task", "enable" },
                new[] { "--sync-startup-task" },
                new[] { "--sync-startup-task", "delete", "--expect-user-sid", UserSidExample },
                new[] { "--sync-startup-task", "enable", "--expect-user-sid", "S-1-1-0" },
                new[] { "--sync-startup-task", "enable", "--wait-for-pid", UserSidExample },
                new[] { "--startup", "enable", "--expect-user-sid", UserSidExample },
                new string[0],
            })
                Check(!StartupTaskHelper.TryParse(bad, out _, out _), "must refuse: " + string.Join(" ", bad));
        }),

        ("startup: the task definition escapes its inputs and carries every required setting", () =>
        {
            const string Hostile = @"C:\A&B\<x>""y""'z'\Brisa.exe";
            var xml = StartupTaskHelper.BuildXml(Hostile, @"C:\A&B\<x>""y""'z'", UserSidExample);
            var doc = XDocument.Parse(xml);
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            Check(doc.Descendants(ns + "Command").Single().Value == Hostile, "the command survives a round trip, so it was escaped");
            Check(doc.Descendants(ns + "WorkingDirectory").Single().Value == @"C:\A&B\<x>""y""'z'", "working directory escaped");
            Check(!xml.Contains("<x>") && xml.Contains("&amp;B") && xml.Contains("&lt;x&gt;"), "markup characters are entities");
            Check(doc.Descendants(ns + "RunLevel").Single().Value == "HighestAvailable" && doc.Descendants(ns + "LogonType").Single().Value == "InteractiveToken", "elevation and logon type");
            Check(doc.Descendants(ns + "ExecutionTimeLimit").Single().Value == "PT0S" && doc.Descendants(ns + "Priority").Single().Value == "4", "no time limit, normal priority");
            Check(doc.Descendants(ns + "DisallowStartIfOnBatteries").Single().Value == "false" && doc.Descendants(ns + "StopIfGoingOnBatteries").Single().Value == "false", "battery flags");
            Check(doc.Descendants(ns + "MultipleInstancesPolicy").Single().Value == "IgnoreNew", "one copy only");
            Check(doc.Descendants(ns + "UserId").Select(e => e.Value).SequenceEqual([UserSidExample, UserSidExample]), "the user SID in the trigger and in the principal");
            Check(doc.Descendants(ns + "LogonTrigger").Count() == 1 && doc.Descendants(ns + "Arguments").Single().Value == "--startup", "one logon trigger with --startup");
            var injected = XDocument.Parse(StartupTaskHelper.BuildXml(BrisaPath, @"C:\Brisa", "S-1-5-21-1</UserId><Exec><Command>calc</Command></Exec><UserId>"));
            Check(injected.Descendants(ns + "Exec").Count() == 1 && !injected.Descendants(ns + "Command").Any(c => c.Value == "calc"), "a hostile SID cannot add an action");
        }),
        ("startup: the task file is UTF-16 with a byte order mark, and the task name is per user and ASCII", () =>
        {
            var xml = StartupTaskHelper.BuildXml(BrisaPath, @"C:\Program Files\Brisa\current", UserSidExample);
            var bytes = StartupTaskHelper.EncodeXml(xml);
            Check(bytes[0] == 0xFF && bytes[1] == 0xFE, "little endian byte order mark");
            using (var reader = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true)) Check(reader.ReadToEnd() == xml, "decodes to the same text");
            Check(XDocument.Load(new MemoryStream(bytes)).Root!.Name.LocalName == "Task", "the file parses as XML");
            var name = StartupTaskHelper.TaskName(UserSidExample);
            Check(name.StartsWith(@"\Brisa\Startup-", StringComparison.Ordinal) && name.Length == @"\Brisa\Startup-".Length + 12, name);
            Check(name[@"\Brisa\Startup-".Length..].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') && name.All(c => c < 128), "lower case hex, ASCII");
            Check(name == StartupTaskHelper.TaskName(UserSidExample) && name != StartupTaskHelper.TaskName(OtherSidExample), "stable per user, different between users");
        }),
        ("startup: the helper refuses bad arguments, the wrong user, a plain token and a foreign executable before touching anything", () => WithDirectory(dir =>
        {
            var host = new FakeHost(dir);
            Check(StartupTaskHelper.Run(["--sync-startup-task", "enable", "--expect-user-sid", UserSidExample, "extra"], host) == StartupTaskHelper.BadArguments, "extra arguments");
            Check(StartupTaskHelper.Run(["--sync-startup-task", "enable", "--expect-user-sid", "S-1-1-0"], host) == StartupTaskHelper.BadArguments, "Everyone");
            Check(StartupTaskHelper.Run(["--sync-startup-task", "enable", "--expect-user-sid", OtherSidExample], host) == StartupTaskHelper.WrongUser, "another account");
            host.Elevated = false;
            Check(StartupTaskHelper.Run(["--sync-startup-task", "enable", "--expect-user-sid", UserSidExample], host) == StartupTaskHelper.NotElevated, "not elevated");
            host.Elevated = true;
            foreach (var path in new[] { @"C:\Program Files\dotnet\dotnet.exe", null })
            {
                host.Path = path;
                Check(StartupTaskHelper.Run(["--sync-startup-task", "enable", "--expect-user-sid", UserSidExample], host) == StartupTaskHelper.WrongExecutable, "enable from " + (path ?? "nothing"));
                Check(StartupTaskHelper.Run(["--sync-startup-task", "disable", "--expect-user-sid", UserSidExample], host) == StartupTaskHelper.WrongExecutable, "disable from " + (path ?? "nothing"));
            }
            Check(host.Calls.Count == 0 && Directory.GetFiles(dir).Length == 0, "nothing ran and no file was written");
        })),
        ("startup: enable writes the task file, hands it to schtasks, and removes it again", () => WithDirectory(dir =>
        {
            var host = new FakeHost(dir);
            string? seenXml = null;
            byte[]? seenBytes = null;
            host.Handler = arguments =>
            {
                seenBytes = File.ReadAllBytes(arguments[4]);
                seenXml = new StreamReader(new MemoryStream(seenBytes), detectEncodingFromByteOrderMarks: true).ReadToEnd();
                return 0;
            };
            Check(StartupTaskHelper.Run(["--sync-startup-task", "enable", "--expect-user-sid", UserSidExample], host) == StartupTaskHelper.Ok, "ok");
            var call = host.Calls.Single();
            Check(call[0] == "/Create" && call[1] == "/TN" && call[2] == StartupTaskHelper.TaskName(UserSidExample) && call[3] == "/XML" && call[5] == "/F", "schtasks /Create arguments");
            Check(call[4].StartsWith(Path.Combine(dir, "brisa-task-"), StringComparison.Ordinal) && call[4].EndsWith(".xml", StringComparison.Ordinal), "a unique file in the temp folder");
            Check(seenBytes![0] == 0xFF && seenBytes[1] == 0xFE && seenXml == StartupTaskHelper.BuildXml(BrisaPath, @"C:\Program Files\Brisa\current", UserSidExample), "the file held exactly the generated task");
            Check(Directory.GetFiles(dir).Length == 0, "the file is deleted afterwards");
            host.Calls.Clear();
            host.Handler = _ => 1;
            Check(StartupTaskHelper.Run(["--sync-startup-task", "enable", "--expect-user-sid", UserSidExample], host) == StartupTaskHelper.SchtasksFailed, "schtasks failure is code 5");
            Check(Directory.GetFiles(dir).Length == 0, "the file is deleted after a failure too");
        })),
        ("startup: disable removes the task, and a task that is already gone still counts as removed", () => WithDirectory(dir =>
        {
            var host = new FakeHost(dir);
            Check(StartupTaskHelper.Run(["--sync-startup-task", "disable", "--expect-user-sid", UserSidExample], host) == StartupTaskHelper.Ok, "ok");
            Check(host.Calls.Single().SequenceEqual(["/Delete", "/TN", StartupTaskHelper.TaskName(UserSidExample), "/F"]), "schtasks /Delete arguments");
            host.Calls.Clear();
            host.Handler = _ => 1;
            Check(StartupTaskHelper.Run(["--sync-startup-task", "disable", "--expect-user-sid", UserSidExample], host) == StartupTaskHelper.Ok, "deleting a missing task");
            Check(host.Calls.Select(c => c[0]).SequenceEqual(["/Delete", "/Query"]), "it asked whether the task exists");
            host.Handler = arguments => arguments[0] == "/Delete" ? 1 : 0;
            Check(StartupTaskHelper.Run(["--sync-startup-task", "disable", "--expect-user-sid", UserSidExample], host) == StartupTaskHelper.SchtasksFailed, "a task that exists but could not be deleted");
        })),

        ("startup: modes follow the settings", () =>
        {
            Check(SettingsStore.ModeOf(new UserSettings()) == StartupMode.Off, "off");
            Check(SettingsStore.ModeOf(new UserSettings(StartWithWindows: true)) == StartupMode.RunKey, "sign in only");
            Check(SettingsStore.ModeOf(new UserSettings(StartWithWindows: true, AutoConnect: true)) == StartupMode.ElevatedTask, "sign in and connect");
            Check(SettingsStore.ModeOf(new UserSettings(AutoConnect: true)) == StartupMode.Off, "connect alone does not start anything at sign in");
            Check(SettingsStore.StartupCommand() == "\"" + Environment.ProcessPath + "\" --startup", "the Run value text is unchanged");
        }),
        ("startup: saving applies a mode only when the mode changes", () => WithDirectory(dir =>
        {
            var registrar = new FakeRegistrar();
            var store = new SettingsStore(dir, true, null, registrar);
            store.Save(new UserSettings(StartWithWindows: true));
            store.Save(new UserSettings(StartWithWindows: true, AutoConnect: true));
            store.Save(new UserSettings(StartWithWindows: true, AutoConnect: true, ShowDetails: false));
            store.Save(new UserSettings(StartWithWindows: true, AutoConnect: false, ShowDetails: false));
            store.Save(new UserSettings(StartWithWindows: false, AutoConnect: false));
            store.Save(new UserSettings(StartWithWindows: false, AutoConnect: true));
            store.Save(new UserSettings(AppTheme.Dark, false, true, "pt-BR", true, "SV"));
            Check(registrar.Calls.SequenceEqual([StartupMode.RunKey, StartupMode.ElevatedTask, StartupMode.RunKey, StartupMode.Off]), "calls: " + string.Join(",", registrar.Calls));
            var saved = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(Path.Combine(dir, "settings.json")))!;
            Check(saved.PreferredRouteCountry == "SV" && saved.Language == "pt-BR" && store.Current == saved, "the new field is saved and Current follows");
        })),
        ("startup: a refused or failed registration changes nothing and does not roll back", () => WithDirectory(dir =>
        {
            foreach (var failure in new Exception[]
            {
                new StartupRegistrationCanceledException(),
                new StartupElevationNeedsAdminException(),
                new StartupRegistrationFailedException("failed"),
                new IOException("disk"),
            })
            {
                var registrar = new FakeRegistrar { Failure = failure };
                var store = new SettingsStore(dir, true, null, registrar);
                var before = store.Current;
                var raised = false;
                try { store.Save(new UserSettings(StartWithWindows: true)); }
                catch (Exception ex) when (ex.GetType() == failure.GetType()) { raised = true; }
                Check(raised, "the same exception reaches the caller: " + failure.GetType().Name);
                Check(store.Current == before && !File.Exists(Path.Combine(dir, "settings.json")), "nothing was saved: " + failure.GetType().Name);
                Check(registrar.Calls.Count == 1, "no second call to undo what never happened: " + failure.GetType().Name);
                Check(Directory.GetFiles(dir, "*.tmp").Length == 0, "temporary file cleaned");
            }
        })),
        ("startup: a failed file replace puts the previous mode back", () => WithDirectory(dir =>
        {
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new UserSettings()));
            var registrar = new FakeRegistrar();
            var store = new SettingsStore(dir, true, null, registrar);
            var original = store.Current;
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var raised = false;
                try { store.Save(new UserSettings(StartWithWindows: true, AutoConnect: true)); } catch (IOException) { raised = true; }
                Check(raised, "the replace failed");
            }
            Check(registrar.Calls.SequenceEqual([StartupMode.ElevatedTask, StartupMode.Off]), "applied, then undone: " + string.Join(",", registrar.Calls));
            Check(store.Current == original, "Current is unchanged");
            // A rollback that fails too must not hide the error that started it.
            var stubborn = new FakeRegistrar { FailOnCall = 2 };
            var stubbornStore = new SettingsStore(dir, true, null, stubborn);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Exception? caught = null;
                try { stubbornStore.Save(new UserSettings(StartWithWindows: true)); } catch (Exception ex) { caught = ex; }
                Check(caught is IOException && stubborn.Calls.Count == 2, "the original error survives a rollback that also fails: " + caught?.GetType().Name);
            }
        })),
        ("startup: isolated stores never reach a registrar, and the older delegate still works", () => WithDirectory(dir =>
        {
            var registrar = new FakeRegistrar();
            var isolated = new SettingsStore(dir, false, null, registrar);
            isolated.Save(new UserSettings(StartWithWindows: true, AutoConnect: true));
            isolated.Save(new UserSettings(StartWithWindows: false));
            Check(registrar.Calls.Count == 0, "allowStartupRegistration false is a no-op");
            var changes = new List<bool>();
            var legacy = new SettingsStore(dir, true, changes.Add);
            legacy.Save(new UserSettings(StartWithWindows: true));
            legacy.Save(new UserSettings(StartWithWindows: true, AutoConnect: true));
            legacy.Save(new UserSettings());
            Check(changes.SequenceEqual([true, true, false]), "delegate calls: " + string.Join(",", changes));
        })),
        ("startup: older settings files load without the new field", () => WithDirectory(dir =>
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"Theme\":2,\"StartWithWindows\":true,\"ShowDetails\":false,\"Language\":\"pt-BR\",\"AutoConnect\":false}");
            var store = new SettingsStore(dir, false);
            Check(store.Current.PreferredRouteCountry is null && store.Current.Theme == AppTheme.Dark && store.Current.StartWithWindows && !store.Current.ShowDetails, "an old file loads");
        })),

        ("startup: the production registrar only touches the Run value after the helper has succeeded", () =>
        {
            var system = new FakeSystem();
            var registrar = new ProductionStartupRegistrar(() => "CMD", system);
            registrar.Apply(StartupMode.ElevatedTask);
            Check(system.Log.SequenceEqual(["helper:enable", "DeleteRunKey"]), "enable then delete: " + string.Join(",", system.Log));
            system.Log.Clear();
            system.HelperCancels = true;
            Throws<StartupRegistrationCanceledException>(() => registrar.Apply(StartupMode.ElevatedTask), "a refused prompt");
            Check(system.Log.SequenceEqual(["helper:enable"]), "the Run value was left alone: " + string.Join(",", system.Log));
        }),
        ("startup: leaving the task needs the helper only when a task exists", () =>
        {
            var system = new FakeSystem();
            var registrar = new ProductionStartupRegistrar(() => "CMD", system);
            registrar.Apply(StartupMode.RunKey);
            Check(system.Log.SequenceEqual(["TaskExists", "SetRunKey:CMD"]), "no task, no prompt: " + string.Join(",", system.Log));
            system.Log.Clear();
            registrar.Apply(StartupMode.Off);
            Check(system.Log.SequenceEqual(["TaskExists", "DeleteRunKey"]), "off without a task: " + string.Join(",", system.Log));
            system.Log.Clear();
            system.HasTask = true;
            registrar.Apply(StartupMode.RunKey);
            Check(system.Log.SequenceEqual(["TaskExists", "helper:disable", "SetRunKey:CMD"]), "helper first, then the Run value: " + string.Join(",", system.Log));
            system.Log.Clear();
            registrar.Apply(StartupMode.Off);
            Check(system.Log.SequenceEqual(["TaskExists", "helper:disable", "DeleteRunKey"]), "off with a task: " + string.Join(",", system.Log));
            system.Log.Clear();
            system.HelperCancels = true;
            Throws<StartupRegistrationCanceledException>(() => registrar.Apply(StartupMode.RunKey), "a refused prompt while leaving the task");
            Check(system.Log.SequenceEqual(["TaskExists", "helper:disable"]), "no Run value was written: " + string.Join(",", system.Log));
        }),
        ("startup: helper exit codes become the right exceptions", () =>
        {
            foreach (var (code, expected) in new[] { (1, typeof(StartupRegistrationFailedException)), (2, typeof(StartupRegistrationFailedException)), (5, typeof(StartupRegistrationFailedException)), (6, typeof(StartupRegistrationFailedException)), (-1, typeof(StartupRegistrationFailedException)), (3, typeof(StartupElevationNeedsAdminException)), (4, typeof(StartupElevationNeedsAdminException)) })
            {
                var system = new FakeSystem { HelperExit = code };
                Exception? caught = null;
                try { new ProductionStartupRegistrar(() => "CMD", system).Apply(StartupMode.ElevatedTask); } catch (Exception ex) { caught = ex; }
                Check(caught?.GetType() == expected, "exit " + code + " gave " + caught?.GetType().Name);
                Check(!system.Log.Contains("DeleteRunKey"), "exit " + code + " never touched the Run value");
            }
        }),
        ("startup: a program that is not Brisa.exe is never registered", () =>
        {
            foreach (var path in new[] { @"C:\Program Files\dotnet\dotnet.exe", @"C:\Tools\Brisa.Tests.exe", null })
            {
                var system = new FakeSystem { ProcessPath = path };
                var registrar = new ProductionStartupRegistrar(() => "CMD", system);
                foreach (var mode in new[] { StartupMode.RunKey, StartupMode.ElevatedTask })
                {
                    Exception? caught = null;
                    try { registrar.Apply(mode); } catch (Exception ex) { caught = ex; }
                    Check(caught is StartupRegistrationFailedException and not StartupElevationNeedsAdminException, mode + " from " + (path ?? "nothing"));
                }
                Check(system.Log.Count == 0, "nothing ran for " + (path ?? "nothing"));
                registrar.Apply(StartupMode.Off);
                Check(system.Log.Contains("DeleteRunKey"), "turning startup off is always allowed");
            }
        }),
        ("startup: the current mode is read from the task first, then the Run value", () =>
        {
            var system = new FakeSystem();
            var registrar = new ProductionStartupRegistrar(() => "CMD", system);
            Check(registrar.Current() == StartupMode.Off, "nothing");
            system.HasRunKey = true;
            Check(registrar.Current() == StartupMode.RunKey, "Run value");
            system.HasTask = true;
            Check(registrar.Current() == StartupMode.ElevatedTask, "task");
        }),
        ("startup: uninstall cleanup tries both steps and ignores failures", () =>
        {
            var system = new FakeSystem { ThrowOnDelete = true };
            ProductionStartupRegistrar.RemoveRunKeyAndTryDeleteTask(system);
            Check(system.Log.SequenceEqual(["DeleteRunKey", "TryDelete"]), "both ran: " + string.Join(",", system.Log));
        }),
    ];

    // ---- helpers -------------------------------------------------------------------------

    private static void Check(bool ok, string what) { if (!ok) throw new InvalidOperationException(what); }

    private static void Throws<T>(Action action, string what) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("expected " + typeof(T).Name + ": " + what);
    }

    private static string TestPrefix() => @"Local\BrisaTest." + Guid.NewGuid().ToString("N");

    private static int CountAces(string sddl) => sddl.Split('(').Length - 1;

    private static void English(Action run)
    {
        var loc = LocalizationManager.Current;
        var saved = loc.Preference;
        loc.Apply(LocalizationManager.English);
        try { run(); }
        finally { loc.Apply(saved); }
    }

    private static void WithDirectory(Action<string> run)
    {
        var dir = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "brisa-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { run(dir); }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static void SpinUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("timed out waiting for " + what);
            Thread.Sleep(10);
        }
    }

    private sealed class FakeRegistrar : IStartupRegistrar
    {
        public List<StartupMode> Calls { get; } = [];
        public Exception? Failure { get; set; }
        public int FailOnCall { get; set; }
        public StartupMode Mode { get; private set; }
        public StartupMode Current() => Mode;

        public void Apply(StartupMode mode)
        {
            Calls.Add(mode);
            if (Failure is not null) throw Failure;
            if (FailOnCall == Calls.Count) throw new InvalidOperationException("rollback failed");
            Mode = mode;
        }
    }

    private sealed class FakeSystem : IStartupSystem
    {
        public List<string> Log { get; } = [];
        public string? ProcessPath { get; set; } = BrisaPath;
        public bool HasTask { get; set; }
        public bool HasRunKey { get; set; }
        public int HelperExit { get; set; }
        public bool HelperCancels { get; set; }
        public bool ThrowOnDelete { get; set; }

        public bool TaskExists() { Log.Add("TaskExists"); return HasTask; }
        public bool RunKeyPresent() => HasRunKey;
        public void SetRunKey(string command) { Log.Add("SetRunKey:" + command); HasRunKey = true; }

        public void DeleteRunKey()
        {
            Log.Add("DeleteRunKey");
            if (ThrowOnDelete) throw new IOException("registry");
            HasRunKey = false;
        }

        public int RunElevatedHelper(string verb)
        {
            Log.Add("helper:" + verb);
            if (HelperCancels) throw new StartupRegistrationCanceledException();
            return HelperExit;
        }

        public bool TryDeleteTaskWithoutElevation() { Log.Add("TryDelete"); return true; }
    }

    private sealed class FakeHost(string tempDirectory) : IStartupTaskHost
    {
        public string CurrentSid => UserSidExample;
        public bool Elevated { get; set; } = true;
        public bool IsElevated => Elevated;
        public string? Path { get; set; } = BrisaPath;
        public string? ProcessPath => Path;
        public string TempDirectory => tempDirectory;
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public Func<IReadOnlyList<string>, int> Handler { get; set; } = _ => 0;

        public int RunSchtasks(IReadOnlyList<string> arguments)
        {
            Calls.Add(arguments.ToList());
            return Handler(arguments);
        }
    }
}

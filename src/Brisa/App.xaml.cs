using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using Brisa.Models;
using Brisa.Services;
using Velopack;

namespace Brisa;

public partial class App : System.Windows.Application
{
    private SingleInstanceActivation? _instance;
    private TrayService? _tray;
    private IUpdateService? _updates;
    private INotificationService? _notifications;
    private NotificationFixture? _notificationFixture;
    private bool _exiting;

    [STAThread]
    private static void Main(string[] args)
    {
        // The elevated helper that creates or removes the sign-in task. It runs before anything else:
        // no mutex, no window, no backend, and it exits with its own code.
        if (args.Length > 0 && args[0].Equals(StartupTaskHelper.Flag, StringComparison.OrdinalIgnoreCase))
            Environment.Exit(StartupTaskHelper.Run(args));
        // Velopack must run before WPF creates Application or any windows. The
        // isolated harnesses skip it so they cannot invoke update/install work.
        if (UpdateLaunchPolicy.ShouldStart(args))
            VelopackApp.Build().SetAutoApplyOnStartup(false).OnBeforeUninstallFastCallback(_ => StartupCleanup.RemoveRunKeyAndTryDeleteTask()).Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    private async void OnStartup(object sender, StartupEventArgs e)
    {
       // A interface é em inglês até o seletor de idioma existir. Fixar a cultura mantém datas e
        // números consistentes com esse idioma; os nomes de país usam EnglishName pelo mesmo motivo.
        // A cultura de formatação neutra evita herdar a do Windows; o idioma real é aplicado
        // abaixo, quando o SettingsStore com o diretório certo já existe.
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        var testMode = e.Args.Contains("--ui-test", StringComparer.OrdinalIgnoreCase);
        var smokeIndex = Array.FindIndex(e.Args, a => a.Equals("--smoke-test", StringComparison.OrdinalIgnoreCase));
        var smokeOutput = smokeIndex >= 0 && smokeIndex + 1 < e.Args.Length ? Path.GetFullPath(e.Args[smokeIndex + 1]) : null;

        if (!testMode && smokeOutput is null)
        {
            var launch = LaunchArguments.Parse(e.Args, Environment.ProcessId, SingleInstanceActivation.UserSid);
            if (launch.RefuseUser)
            {
                // An elevated relaunch under another account would use that account's settings and session.
                Brisa.Localization.LocalizationManager.Current.Apply(Brisa.Localization.LocalizationManager.System);
                MessageBox.Show(Brisa.Localization.LocalizationManager.Current["ElevationWrongUser"], "Brisa", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown();
                return;
            }
            if (launch.WaitForPid is { } waitPid) WaitForPredecessor(waitPid);
            _instance = new SingleInstanceActivation(waitForPredecessor: launch.WaitForPid is not null);
            if (!_instance.IsPrimary)
            {
                // Another copy is running, possibly elevated: hand it the foreground and leave.
                SingleInstanceActivation.SignalPrimary();
                ReleaseSingleInstance();
                Shutdown();
                return;
            }
        }

        var isolated = testMode || smokeOutput is not null;
        // "Start When I Sign In" writes --startup. It is a logon launch, so it must stay on
        // Home and never request elevation on its own.
        var startupLaunch = !isolated && e.Args.Contains("--startup", StringComparer.OrdinalIgnoreCase);
        var settings = isolated
            ? new SettingsStore(Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "BrisaUiTests", Guid.NewGuid().ToString("N")), false)
            : new SettingsStore();
        if (!isolated) Security.VerificationProfileStore.Cleanup();
        if (isolated && e.Args.FirstOrDefault(a => a.StartsWith("--theme=", StringComparison.Ordinal)) is { } themeArg && Enum.TryParse<AppTheme>(themeArg[8..], true, out var theme))
            settings.Save(settings.Current with { Theme = theme });
        // Fixture do idioma: o Windows desta máquina está em inglês, então "System" não mostra o
        // português. Só existe em modo isolado.
        if (isolated && e.Args.FirstOrDefault(a => a.StartsWith("--language=", StringComparison.Ordinal)) is { } languageArg)
            settings.Save(settings.Current with { Language = languageArg[11..] });
        if (isolated && e.Args.FirstOrDefault(a => a.StartsWith("--language=", StringComparison.Ordinal)) is { } languageApply)
            Brisa.Localization.LocalizationManager.Current.Apply(languageApply[11..]);
        // Fixture da preferência do painel: item 9 acrescenta a caixa em Settings, aqui só o estado.
        if (isolated && e.Args.Contains("--no-details", StringComparer.OrdinalIgnoreCase))
            settings.Save(settings.Current with { ShowDetails = false });
        settings.ApplyTheme();
        // Idioma salvo, aplicado depois que o SettingsStore (e o diretório de teste, quando houver)
        // estão definidos. Nenhuma janela existe ainda, então nada é reconstruído.
        Brisa.Localization.LocalizationManager.Current.Apply(settings.Current.Language);
        // Visual fixtures only: --route=HT gives the isolated backend a route so the Home card can be
        // photographed in a real state instead of "Automatic".
        var routeArg = isolated ? e.Args.FirstOrDefault(a => a.StartsWith("--route=", StringComparison.Ordinal)) : null;
        var fixtureRoute = routeArg is { Length: > 8 }
            ? new RouteInfo($"{routeArg[8..].ToUpperInvariant()}#28", routeArg[8..].ToUpperInvariant(), 34) : null;
        IBackendClient backend;
        if (isolated && e.Args.Contains("--backend-unavailable", StringComparer.OrdinalIgnoreCase)) backend = new UnavailableBackendClient();
        else if (testMode || smokeOutput is not null) backend = new UiTestBackendClient(
            initial: e.Args.Contains("--limited", StringComparer.OrdinalIgnoreCase)
                // Isolated visual check of the unelevated Limited Home; never a real launch.
                ? new(false, false, false, true, "UI test account", null, RequiresElevation: true)
                : smokeOutput is not null || e.Args.Contains("--signed-in", StringComparer.OrdinalIgnoreCase)
                    ? new(false, false, true, true, "UI test account", null) : null,
            commandDelay: testMode ? TimeSpan.FromSeconds(6) : TimeSpan.Zero, route: fixtureRoute);
        else
        {
            try { backend = new BackendClient(AppContext.BaseDirectory, settings.DataDirectory); }
            catch { backend = new UnavailableBackendClient(); }
        }
        if (!isolated)
        {
            // Cheap here: the client builds Velopack's UpdateManager on first use. Start() runs after
            // the first frame, and the first GitHub check waits DefaultStartupDelay on top of that.
            try { _updates = new UpdateService(new VelopackUpdateClient(), initialDelay: UpdateService.DefaultStartupDelay); }
            catch { _updates = null; }
        }
        _notifications = CreateNotifications(e.Args, isolated, settings);
        // Sessão isolada não mede tráfego de verdade: o contador é simulado, e o título diz isso
        // para que nenhuma captura pareça um número real.
        var traffic = isolated ? new SimulatedTrafficSource() : null;
        var window = new MainWindow(backend, settings, smokeOutput, _updates, traffic: traffic, autoRouteScan: !isolated, autoConnect: !isolated, notifications: _notifications);
        MainWindow = window;
        _instance?.Listen(() => Dispatcher.BeginInvoke(new Action(() => ShowFromActivation(window))));
        if (isolated) window.Title = traffic is null ? "Brisa — UI Test (No Network)" : "Brisa — UI Test (No Network, simulated counters)";
        window.Show();
        // The tray (Windows Forms plus the exe icon) and the updater are not needed to paint.
        // ContextIdle runs after Render and Loaded, so the first frame is already on screen, and
        // ahead of ApplicationIdle work. An exit that started first wins: no tray after OnExit.
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (_exiting) return;
            _tray = new TrayService(window, settings);
            try { _updates?.Start(); } catch { }
            // Loads the saved feed off the UI thread and waits 20 s before the first request.
            try { _notifications?.Start(); } catch { }
        }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        await window.InitializeAsync(openAccount: !startupLaunch);
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        _exiting = true;
        _tray?.Dispose();
        _updates?.Dispose();
        _notifications?.Dispose();
        _notificationFixture?.Dispose();
        ReleaseSingleInstance();
    }

    /// <summary>Drops the mutex and the activation event. Safe to call twice, and it must run on the window thread.</summary>
    internal void ReleaseSingleInstance() => Interlocked.Exchange(ref _instance, null)?.Dispose();

    // The same restore the tray uses, so a second launch and Open behave identically.
    private void ShowFromActivation(MainWindow window)
    {
        if (_exiting) return;
        window.ShowFromActivation();
    }

    /// <summary>The unelevated copy is leaving. Wait for it, up to 30 s, so the mutex is free when this one asks for it.</summary>
    private static void WaitForPredecessor(int pid)
    {
        Process process;
        try { process = Process.GetProcessById(pid); }
        catch (ArgumentException) { return; }
        using (process)
        {
            var isBrisa = true;
            // A refused look at the process image is not a reason to skip the wait.
            try { isBrisa = Path.GetFileName(process.MainModule?.FileName ?? "").Equals("Brisa.exe", StringComparison.OrdinalIgnoreCase); }
            catch (Win32Exception) { }
            catch (InvalidOperationException) { return; }
            if (!isBrisa) return;
            try { process.WaitForExit(30_000); } catch (Win32Exception) { } catch (InvalidOperationException) { }
        }
    }

    private INotificationService? CreateNotifications(string[] args, bool isolated, SettingsStore settings)
    {
        try
        {
            var store = new NotificationStateStore(settings.DataDirectory);
            if (isolated)
            {
                // Test and smoke launches never open a socket. The fixture serves a feed signed in
                // memory with a throwaway key, and only when the flag is passed.
                var flag = args.FirstOrDefault(a => a.StartsWith("--notifications-fixture=", StringComparison.Ordinal));
                _notificationFixture = NotificationFixtures.Create(flag?["--notifications-fixture=".Length..], DateTimeOffset.UtcNow);
                return _notificationFixture is null
                    ? new NotificationService(new NullNotificationFeedClient(), store, _updates)
                    : new NotificationService(_notificationFixture.Client, store, _updates, _notificationFixture.Verifier, startupDelay: TimeSpan.Zero);
            }
            return new NotificationService(new HttpNotificationFeedClient(), store, _updates);
        }
        catch { return null; }
    }
}

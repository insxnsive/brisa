using System.IO;
using System.Threading;
using System.Windows;
using Brisa.Services;
using Velopack;

namespace Brisa;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private TrayService? _tray;
    private IUpdateService? _updates;

    [STAThread]
    private static void Main(string[] args)
    {
        // Velopack must run before WPF creates Application or any windows. The
        // isolated harnesses skip it so they cannot invoke update/install work.
        if (UpdateLaunchPolicy.ShouldStart(args)) VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        var testMode = e.Args.Contains("--ui-test", StringComparer.OrdinalIgnoreCase);
        var smokeIndex = Array.FindIndex(e.Args, a => a.Equals("--smoke-test", StringComparison.OrdinalIgnoreCase));
        var smokeOutput = smokeIndex >= 0 && smokeIndex + 1 < e.Args.Length ? Path.GetFullPath(e.Args[smokeIndex + 1]) : null;

        if (!testMode && smokeOutput is null)
        {
            _mutex = new Mutex(true, @"Local\Brisa", out var created);
            if (!created) { Shutdown(); return; }
        }

        var isolated = testMode || smokeOutput is not null;
        var settings = isolated
            ? new SettingsStore(Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "BrisaUiTests", Guid.NewGuid().ToString("N")), false)
            : new SettingsStore();
        if (!isolated) Security.VerificationProfileStore.Cleanup();
        if (isolated && e.Args.FirstOrDefault(a => a.StartsWith("--theme=", StringComparison.Ordinal)) is { } themeArg && Enum.TryParse<AppTheme>(themeArg[8..], true, out var theme))
            settings.Save(settings.Current with { Theme = theme });
        settings.ApplyTheme();
        IBackendClient backend;
        if (testMode || smokeOutput is not null) backend = new UiTestBackendClient(
            initial: smokeOutput is not null || e.Args.Contains("--signed-in", StringComparer.OrdinalIgnoreCase)
                ? new(false, false, true, true, "UI test account", null) : null,
            commandDelay: testMode ? TimeSpan.FromSeconds(6) : TimeSpan.Zero);
        else
        {
            try { backend = new BackendClient(AppContext.BaseDirectory, settings.DataDirectory); }
            catch { backend = new UnavailableBackendClient(); }
        }
        if (!isolated)
        {
            try
            {
                _updates = new UpdateService(new VelopackUpdateClient());
                _updates.Start();
            }
            catch { _updates = null; }
        }
        var window = new MainWindow(backend, settings, smokeOutput, _updates);
        MainWindow = window;
        _tray = new TrayService(window, settings);
        if (isolated) window.Title = "Brisa — UI Test (No Network)";
        window.Show();
        await window.InitializeAsync();
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        _tray?.Dispose();
        _updates?.Dispose();
        _mutex?.Dispose();
    }
}

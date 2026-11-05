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
            _mutex = new Mutex(true, @"Local\Brisa", out var created);
            if (!created) { Shutdown(); return; }
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
            try
            {
                _updates = new UpdateService(new VelopackUpdateClient());
                _updates.Start();
            }
            catch { _updates = null; }
        }
        // Sessão isolada não mede tráfego de verdade: o contador é simulado, e o título diz isso
        // para que nenhuma captura pareça um número real.
        var traffic = isolated ? new SimulatedTrafficSource() : null;
        var window = new MainWindow(backend, settings, smokeOutput, _updates, traffic: traffic);
        MainWindow = window;
        _tray = new TrayService(window, settings);
        if (isolated) window.Title = traffic is null ? "Brisa — UI Test (No Network)" : "Brisa — UI Test (No Network, simulated counters)";
        window.Show();
        await window.InitializeAsync(openAccount: !startupLaunch);
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        _tray?.Dispose();
        _updates?.Dispose();
        _mutex?.Dispose();
    }
}

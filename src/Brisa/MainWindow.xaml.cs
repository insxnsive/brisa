using System.Globalization;
using Brisa.Localization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Brisa.Models;
using Brisa.Security;
using Brisa.Services;

namespace Brisa;

internal enum AdvancedOrigin { None, Settings, Account }

public partial class MainWindow : Window
{
    private readonly IBackendClient _backend;
    private readonly SettingsStore _settings;
    private readonly string? _smokeOutput;
    private readonly IUpdateService? _updates;
    private readonly INotificationService? _notifications;
    private long _notificationsClosedAt;
    private readonly Action<string> _exitWarning;
    private readonly Action<string> _notice;
    private readonly Func<string, bool> _exitConfirm;
    // Ãšltimo instante em que o instalador visÃ­vel do fornecedor estava na tela, em ticks (0 =
    // nenhum). A UI escreve e a tarefa do prazo lÃª, entÃ£o o valor Ã© lido e escrito com Volatile:
    // um DateTime? de 16 bytes poderia ser lido pela metade.
    private long _installerStageSeenTicks;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private HomeState _state = new(ConnectionPhase.Loading, null);
    private CancellationTokenSource _lifetime = new();
    private bool _closing, _canClose, _explicitExit, _navigationBusy, _applyUpdateOnExit;
    private UserControl? _currentPage;
    private Task? _returnHomeTask;
    private bool _refreshing;
    private bool _wasMinimized;
    private bool _nativeOperationMayBeActive;
    private bool _terminalBackendFailure;
    private bool _sdkSetupFailed;
    private bool _confirmDiscordRestart;
    internal Func<bool>? IsDiscordRunning { get; set; }
    private readonly bool _autoRouteScan;
    private readonly bool _autoConnect;
    private bool _routeScanClaimed;
    private Task? _routeScan;
    // Snapshot commit bookkeeping. Every snapshot-derived state write goes through one gate:
    // a stale response must never overwrite a newer operation's state, and an operation-owned
    // final refresh must be distinguishable from a background poll that merely looks current.
    private long _operationEpoch;
    private long _navigationEpoch;
    private long _requestSequence;
    private long _appliedSequence;
    private bool _operationOwnsUi;
    private readonly System.Windows.Threading.DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    // Painel de detalhes: um Ãºnico timer de 1 s, que sÃ³ roda com o painel na tela e a janela
    // visÃ­vel. Sessenta amostras dÃ£o uma janela de um minuto, que Ã© o que cabe no grÃ¡fico.
    private const int ChartSamples = 60;
    // O paÃ­s recebe o espaÃ§o que sobra depois do servidor e do ping, e sÃ³ ele trima. Lido da coluna
    // que contÃ©m a linha, nÃ£o da prÃ³pria linha, senÃ£o o cÃ¡lculo se alimenta do prÃ³prio resultado.
    private void FitRouteText()
    {
        var serverWidth = RouteServerText.Visibility == Visibility.Visible
            ? RouteServerText.ActualWidth + RouteServerText.Margin.Left
            : 0;
        var available = Math.Max(0, RouteDetails.ActualWidth - serverWidth - 12);
        if (Math.Abs(RouteText.MaxWidth - available) > 0.5) RouteText.MaxWidth = available;
    }

    // Altura fixa do cartÃ£o de trÃ¡fego: cabeÃ§alho mais grÃ¡fico. Nenhum nÃºmero novo, e nada que dependa
    // dos valores, para o cartÃ£o nÃ£o pular enquanto os contadores sobem.
    private const double TrafficCardHeight = 108;

    private readonly System.Windows.Threading.DispatcherTimer _detailsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly double[] _downSamples = new double[ChartSamples];
    private readonly double[] _upSamples = new double[ChartSamples];
    private readonly ITrafficSource _traffic;
    private TrafficReading? _lastReading;
    private DateTime _connectedSince;
    private ulong _totalReceived, _totalSent;
    private int _sampleCount;
    private bool _detailsVisible;
    private bool _keyboardInput;
    private readonly System.ComponentModel.PropertyChangedEventHandler _languageChanged;
    private string? _renderedStatus;
    private string? _renderedGlyph;
    private readonly Func<bool>? _allowMotion;

    private readonly record struct SnapshotTicket(long Operation, long Navigation, long Sequence, bool FromOperation);

    // One definition of "a UI mutation is in flight", used by handlers as well as controls:
    // disabling a button does not stop a queued or directly raised event.
    private bool UiMutationBusy => _operationOwnsUi || _operation.CurrentCount == 0
        || _state.Phase is ConnectionPhase.Connecting or ConnectionPhase.Disconnecting or ConnectionPhase.Optimizing;

    private SnapshotTicket IssueTicket(bool fromOperation = false) =>
        new(_operationEpoch, _navigationEpoch, Interlocked.Increment(ref _requestSequence), fromOperation);

    private bool CanCommit(SnapshotTicket ticket)
    {
        if (_closing || _canClose) return false;
        if (ticket.Operation != _operationEpoch || ticket.Navigation != _navigationEpoch) return false;
        return ticket.FromOperation ? _operationOwnsUi : !_operationOwnsUi && _operation.CurrentCount != 0;
    }

    private bool ApplySnapshot(NativeSnapshot snapshot, SnapshotTicket ticket)
    {
        if (!CanCommit(ticket) || ticket.Sequence < _appliedSequence) return false;
        _appliedSequence = ticket.Sequence;
        SetState(HomeState.FromSnapshot(snapshot));
        return true;
    }

    private async Task<bool> RefreshSnapshotAsync(SnapshotTicket ticket, TimeSpan budget)
    {
        using var token = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        token.CancelAfter(budget);
        try
        {
            var snapshot = await _backend.SnapshotAsync(token.Token);
            return ApplySnapshot(snapshot, ticket);
        }
        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
        {
            // This budget expiring is our own deadline, not the window shutting down. The
            // transport sees a cancellable token here, so it reports cancellation rather than
            // TimeoutException; the deadline has to be classified on this side.
            if (CanCommit(ticket)) SetError(new TimeoutException(BackendClient.TimeoutMessage), _state.Snapshot);
            return false;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            // A stale failure must not replace a newer healthy state or stop its timer.
            if (CanCommit(ticket)) SetError(ex, _state.Snapshot);
            return false;
        }
    }

    private void BeginUiMutation()
    {
        CloseNotifications();
        _operationOwnsUi = true;
        _operationEpoch++;
    }

    private void EndUiMutation()
    {
        _operationEpoch++;
        _operationOwnsUi = false;
    }

    // A rejected final refresh must not leave the window stuck in a busy phase with every
    // control disabled, so the last known snapshot is republished instead.
    private void LeaveBusyPhase()
    {
        if (_state.Phase is not (ConnectionPhase.Connecting or ConnectionPhase.Disconnecting or ConnectionPhase.Optimizing)) return;
        SetState(_state.Snapshot is { } snapshot ? HomeState.FromSnapshot(snapshot) : new(ConnectionPhase.Disconnected, null));
    }

    public MainWindow(IBackendClient backend, SettingsStore settings, string? smokeOutput = null, IUpdateService? updates = null, Action<string>? exitWarning = null, Action<string>? notice = null, Func<string, bool>? exitConfirm = null, ITrafficSource? traffic = null, Func<bool>? allowMotion = null, bool autoRouteScan = false, bool autoConnect = false, INotificationService? notifications = null)
    {
        InitializeComponent(); TrafficCard.Height = TrafficCardHeight; _backend = backend;
        // O servidor encosta no paÃ­s; quando o nome do paÃ­s nÃ£o cabe, ele trima e o servidor nÃ£o.
        RouteValueRow.SizeChanged += (_, _) => FitRouteText();
        RouteDetails.SizeChanged += (_, _) => FitRouteText(); _settings = settings; _smokeOutput = smokeOutput; _updates = updates;
        _traffic = traffic ?? new InterfaceTrafficSource();
        _exitWarning = exitWarning ?? (message => { MessageBox.Show(this, message, "Brisa", MessageBoxButton.OK, MessageBoxImage.Warning); });
        // Leaving with an unknown installer state is allowed, but only after the user says so:
        // the window may still be showing a vendor installer, and Discord was already closed.
        _exitConfirm = exitConfirm ?? (message => MessageBox.Show(this, message, "Exit Brisa", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);
        _notice = notice ?? (message => { MessageBox.Show(this, message, "Route", MessageBoxButton.OK, MessageBoxImage.Warning); });
        SetState(_state);
        // Troca de idioma repinta esta janela, que continua a mesma: nada Ã© fechado, entÃ£o o tÃºnel
        // nÃ£o passa por OnClosing nem pelo ExitGuard.
        _languageChanged = (_, _) =>
        {
            if (_closing) return;
            SetState(_state);
            RenderDetails();
            // The bell's spoken unread count is text too: refresh it with the language.
            if (_notifications is not null) ApplyNotificationBadge(_notifications.Snapshot);
            // O tÃ­tulo da pÃ¡gina aberta tambÃ©m Ã© texto do produto: sem isto ele ficaria no idioma
            // anterior atÃ© a prÃ³xima navegaÃ§Ã£o.
            var loc = Localization.LocalizationManager.Current;
            PageTitle.Text = PageHost.Content switch
            {
                SettingsView => loc["SettingsTitle"],
                AccountView => loc["Account"],
                AdvancedView => loc["Advanced"],
                _ => PageTitle.Text,
            };
        };
        Localization.LocalizationManager.Current.PropertyChanged += _languageChanged;
        _statusTimer.Tick += RefreshStatus;
        _detailsTimer.Tick += (_, _) => RenderDetails();
        ChartHost.SizeChanged += (_, _) => DrawChart();
        // A minimized window is still IsVisible, so the status timer skips it by WindowState.
        // Leaving Minimized refreshes once, like coming back from the tray does.
        StateChanged += (_, _) =>
        {
            var restored = _wasMinimized && WindowState != WindowState.Minimized;
            _wasMinimized = WindowState == WindowState.Minimized;
            if (restored && IsVisible) RefreshStatus(this, EventArgs.Empty);
            SyncDetailsTimer();
        };
        Closed += (_, _) =>
        {
            _statusTimer.Stop(); _detailsTimer.Stop();
            // O gerenciador Ã© um singleton: um handler anÃ´nimo manteria esta janela viva depois de
            // fechada, e um Apply posterior pintaria uma Ã¡rvore jÃ¡ descartada.
            Localization.LocalizationManager.Current.PropertyChanged -= _languageChanged;
            if (_notifications is not null) _notifications.SnapshotChanged -= OnNotificationsChanged;
        };
        Closing += OnClosing;
        // The timer is paused while hidden in the tray, so refresh once when the window comes back.
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) { RefreshStatus(this, EventArgs.Empty); SyncDetailsTimer(); } else SyncDetailsTimer(); };
        _allowMotion = allowMotion;
        _autoRouteScan = autoRouteScan;
        _autoConnect = autoConnect;
        PreviewKeyDown += OnNavigationKeyDown;
        // A Ãºltima entrada decide se um anel de foco Ã© cortesia ou ruÃ­do.
        PreviewKeyDown += (_, _) => _keyboardInput = true;
        PreviewMouseDown += (_, _) => _keyboardInput = false;
        _notifications = notifications;
        if (_notifications is not null)
        {
            NotificationsPanel.Bind(_notifications);
            NotificationsPanel.CloseRequested += (_, _) => { CloseNotifications(); NotificationsButton.Focus(); };
            NotificationsPanel.InstallRequested += (_, _) => { CloseNotifications(); RequestUpdateRestart(); };
            _notifications.SnapshotChanged += OnNotificationsChanged;
            ApplyNotificationBadge(_notifications.Snapshot);
        }
        UpdateNotificationsHost();
    }

    public async Task InitializeAsync(bool openAccount = true)
    {
        var ticket = IssueTicket();
        var accepted = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var snapshot = await _backend.SnapshotAsync(timeout.Token).WaitAsync(timeout.Token);
            accepted = ApplySnapshot(snapshot, ticket);
        }
        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
        {
            if (CanCommit(ticket)) SetError(new TimeoutException(BackendClient.TimeoutMessage), null);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (CanCommit(ticket)) SetError(ex, null);
        }
        // A logon launch stays on Home: the limited-access state says what is possible
        // instead of jumping the user into a sign-in they did not ask for. Onboarding also
        // waits for an accepted snapshot, so a stale or rejected one cannot open Account.
        if (openAccount && accepted && _state.RequiresSignIn && _currentPage is null && !_closing) OpenAccount();
        if (accepted && !StartAutoConnect()) StartRouteScan();
        if (!_terminalBackendFailure) _statusTimer.Start();
        if (_smokeOutput is not null) await RunSmokeAsync(_smokeOutput);
    }

    private bool StartAutoConnect()
    {
        if (!_autoConnect || !_settings.Current.AutoConnect || _routeScanClaimed || _closing || _canClose || _smokeOutput is not null) return false;
        if (_currentPage is not null || _navigationBusy || UiMutationBusy) return false;
        if (_state.Phase != ConnectionPhase.Disconnected || _state.RequiresSignIn) return false;
        if (_state.Snapshot is not { HasOwnedTunnel: false, ExternalTunnel: false, TunnelActive: false, Connected: false, Reliable: true, RequiresElevation: false }) return false;
        if (!_operation.Wait(0)) return false;
        _routeScanClaimed = true;
        BeginUiMutation();
        var scanFirst = _autoRouteScan && _state.Snapshot.Mode == "proton";
        SetState(_state with { Phase = ConnectionPhase.Connecting, DetailKey = scanFirst ? "StagePreparingRoute" : null });
        _ = RunPrimaryMutationAsync("connect", scanFirst);
        return true;
    }

    // F1: the saved automatic route is reused by every connect until the user opens Route, so one
    // stale pick can outlive weeks of launches. One quiet scan at launch keeps it current.
    private void StartRouteScan()
    {
        if (!_autoRouteScan || _routeScanClaimed || _closing || _smokeOutput is not null) return;
        // The manual Route rule, plus proton mode: never while a tunnel may be ours or someone
        // else's, and never on an unreadable inspection.
        if (_state.Snapshot is not { SignedIn: true, Mode: "proton", HasOwnedTunnel: false, ExternalTunnel: false, TunnelActive: false, Reliable: true }) return;
        _routeScanClaimed = true;
        _routeScan = RunRouteScanAsync(_lifetime.Token);
    }

    private async Task RunRouteScanAsync(CancellationToken lifetime)
    {
        try
        {
            // Not ExecuteWithVerificationAsync: a verification challenge must not open a window
            // the user did not ask for. Any failure keeps the previous route and profile.
            var result = await _backend.CommandAsync("optimize", RoutePayload(), lifetime);
            if (!result.Success || _closing) return;
            var ticket = IssueTicket();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            ApplySnapshot(await _backend.SnapshotAsync(timeout.Token), ticket);
        }
        catch { }
    }

    // A user mutation must never meet MUTATION_BUSY because of the quiet scan: cancel it and wait
    // for the backend to report idle first. A failed or slow cancel abandons the scan; the user's
    // command still goes out. Exit needs no call because ExitGuard starts with CancelAsync.
    private async Task PreemptRouteScanAsync()
    {
        _routeScanClaimed = true;
        if (_routeScan is not { IsCompleted: false } scan) return;
        try { await _backend.CancelAsync().WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
        try { await scan.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
    }

    private async void RefreshStatus(object? sender, EventArgs e)
    {
        if (_refreshing || _closing || !IsVisible || WindowState == WindowState.Minimized || _currentPage is not null || UiMutationBusy ||
            _state.Phase is ConnectionPhase.Loading) return;
        _refreshing = true;
        try { await RefreshSnapshotAsync(IssueTicket(), TimeSpan.FromSeconds(30)); }
        finally { _refreshing = false; }
    }

    private void SetError(Exception error, NativeSnapshot? snapshot)
    {
        if (error is BackendUnavailableException) { _terminalBackendFailure = true; _statusTimer.Stop(); }
        // Uma chave: HomeState resolve o texto na pintura, entÃ£o o erro tambÃ©m acompanha
        // uma troca de idioma. A frase pronta continua existindo para quem chama MessageBox.
        SetState(new(ConnectionPhase.Error, snapshot, SafeMessageKey(error)));
    }

    private void SetState(HomeState state)
    {
        if (state.Snapshot?.HasOwnedTunnel == true) _nativeOperationMayBeActive = true;
        else if (_operation.CurrentCount != 0 && state.Phase is ConnectionPhase.Disconnected or ConnectionPhase.Blocked
            && state.Snapshot is { Reliable: true, HasOwnedTunnel: false, Stage: "idle" }) _nativeOperationMayBeActive = false;
        _state = state; RenderStatusText(state.StatusText); DetailText.Text = state.Detail ?? "";
        if (state.Phase == ConnectionPhase.Error && _backend.StartupCode != "INIT_UNKNOWN" && StartupDiagnostics.IsKnownCode(_backend.StartupCode))
            DetailText.Text += $" ({_backend.StartupCode})";
        // Missing SDK is a prerequisite state, not a connection error. The banner appears only
        // after this session's automatic install failed: a launch that merely sees SdkMissing
        // stays quiet, and a later snapshot that proves the SDK present clears the gate.
        if (state.Snapshot is { SdkMissing: false }) _sdkSetupFailed = false;
        SdkBanner.Visibility = _sdkSetupFailed
            && state.Snapshot is { SdkMissing: true, Stage: not "installing-wiresock", HasOwnedTunnel: false }
            && state.Phase is not (ConnectionPhase.Connected or ConnectionPhase.Limited)
            ? Visibility.Visible : Visibility.Collapsed;
        // Discord restarts on connect and disconnect, so the primary action asks once before it
        // restarts a client that is already open. The arm is dropped as soon as the state no
        // longer allows a connect, so a stale confirmation cannot survive a page or a status
        // change.
        if (state.Phase != ConnectionPhase.Disconnected || state.RequiresSignIn || !state.PrimaryEnabled
            || (state.Snapshot is not null && !CheckDiscordOpen()))
            _confirmDiscordRestart = false;
        PrimaryButton.Content = _confirmDiscordRestart
            ? Localization.LocalizationManager.Current["ConnectConfirmDiscordRestart"]
            : state.PrimaryLabel;
        var busy = state.Phase is ConnectionPhase.Loading or ConnectionPhase.Connecting or ConnectionPhase.Disconnecting or ConnectionPhase.Optimizing;
        SetStatusRing(busy);
        UpdateActionAvailability();

        var loc = Localization.LocalizationManager.Current;
        if (state.Snapshot?.Route is { } route)
        {
            var country = CountryName(route.Country);
            RouteText.Text = country;
            // PaÃ­s e servidor dividem uma linha: o paÃ­s trima, o servidor e o ping nunca. O peso do
            // texto separa os dois, entÃ£o nÃ£o entra pontuaÃ§Ã£o nenhuma na tela.
            var server = route.Server?.Trim() ?? "";
            if (server.Length == 0)
            {
                RouteServerText.Text = "";
                RouteServerText.Visibility = Visibility.Collapsed;
                System.Windows.Automation.AutomationProperties.SetName(RouteButton, country);
            }
            else
            {
                RouteServerText.Text = route.PingMs is int ping
                    ? string.Format(loc["ServerWithPing"], server, ping)
                    : string.Format(loc["ServerOnly"], server);
                RouteServerText.Visibility = Visibility.Visible;
                System.Windows.Automation.AutomationProperties.SetName(RouteButton, route.PingMs is int spokenPing
                    ? string.Format(loc["RouteAccessibleNameWithPing"], country, server, spokenPing)
                    : string.Format(loc["RouteAccessibleName"], country, server));
            }
        }
        else
        {
            RouteText.Text = loc["Automatic"];
            RouteServerText.Text = "";
            RouteServerText.Visibility = Visibility.Collapsed;
            System.Windows.Automation.AutomationProperties.SetName(RouteButton, loc["ChooseRoute"]);
        }
        // Enquanto existe um tÃºnel a rota nÃ£o pode mudar, e o botÃ£o desabilitado sozinho nÃ£o diz por
        // quÃª: o chevron vira cadeado, o texto esmaece e a linha explica o que fazer.
        var routeLocked = state.Snapshot?.HasOwnedTunnel == true
            || state.Phase is ConnectionPhase.Connecting or ConnectionPhase.Disconnecting or ConnectionPhase.Connected;
        RouteChevron.Text = routeLocked ? "\uE72E" : "\uE76C";
        // A rota continua travada enquanto hÃ¡ tÃºnel ativo, mas o motivo fica no hover: uma linha de
        // texto dentro do cartÃ£o sÃ³ aumentava o bloco do servidor.
        var routeReason = routeLocked ? Localization.LocalizationManager.Current["DisconnectToChangeRoute"] : null;
        // O cartÃ£o Ã© quem recebe o hover: um botÃ£o desabilitado nÃ£o Ã© um alvo confiÃ¡vel de tooltip.
        RouteCard.ToolTip = routeReason;
        RouteButton.ToolTip = routeReason;
        System.Windows.Automation.AutomationProperties.SetHelpText(RouteButton, routeReason ?? string.Empty);
        // O esmaecimento vem do prÃ³prio botÃ£o desabilitado. Somar opacidade aqui escurecia a linha de
        // ajuda abaixo do mÃ­nimo de contraste, e a instruÃ§Ã£o Ã© justamente o que precisa ser lido.
        // Conectado Ã© o Ãºnico estado com acento: o resto fica neutro, e o Limited usa o escudo de
        // administrador porque um cadeado aberto lÃª como "livre", o oposto do que ele significa.
        var connected = state.Phase == ConnectionPhase.Connected;
        var glyph = connected ? "\uE73E"
            : state.Phase == ConnectionPhase.Limited ? "\uEA18"
            : state.Phase is ConnectionPhase.Blocked or ConnectionPhase.Error ? "\uE7BA" : "\uE785";
        if (_renderedGlyph != glyph)
        {
            if (_renderedGlyph is not null) StatusMotion.FadeIn(StatusBadge, _allowMotion);
            _renderedGlyph = glyph;
        }
        StatusIcon.Text = glyph;
        StatusBadge.SetResourceReference(Border.BackgroundProperty, connected ? "NativeStatusConnectedSurface" : "NativeIconSurface");
        StatusIcon.SetResourceReference(TextBlock.ForegroundProperty, connected ? "NativeStatusConnected" : "NativeForeground");

        // Conectado e com a preferÃªncia ligada, o painel substitui o bloco centralizado. Em
        // qualquer outro estado ele nÃ£o existe: zero e travessÃ£o mentem mais do que a ausÃªncia.
        var showDetails = connected && _settings.Current.ShowDetails;
        if (showDetails && !_detailsVisible)
        {
            // A sessÃ£o conta a partir do momento em que o painel aparece, entÃ£o o nÃºmero na tela Ã©
            // o mesmo intervalo que o grÃ¡fico cobre.
            _connectedSince = DateTime.UtcNow;
            _lastReading = _traffic.Read();
            _totalReceived = _totalSent = 0;
            Array.Clear(_downSamples);
            Array.Clear(_upSamples);
            _sampleCount = 0;
            RenderDetails();
        }
        if (showDetails != _detailsVisible)
        {
            if (showDetails)
            {
                StatusBlock.Visibility = Visibility.Collapsed;
                DetailsHost.Visibility = Visibility.Visible;
                PageTransition.Show(DetailsHost, _allowMotion);
            }
            else
            {
                // Some primeiro, recolhe depois: o bloco centralizado sÃ³ volta quando o painel
                // terminou de sair, senÃ£o os dois aparecem ao mesmo tempo.
                StatusBlock.Visibility = Visibility.Collapsed;
                StatusMotion.FadeOut(DetailsHost, () =>
                {
                    DetailsHost.Visibility = Visibility.Collapsed;
                    StatusBlock.Visibility = Visibility.Visible;
                }, _allowMotion);
            }
        }
        else
        {
            StatusBlock.Visibility = showDetails ? Visibility.Collapsed : Visibility.Visible;
            if (showDetails) DetailsHost.Visibility = Visibility.Visible;
        }
        _detailsVisible = showDetails;
        CompactStatusText.Text = state.StatusText;
        SyncDetailsTimer();
        HomeStateChanged?.Invoke(this, state);
    }

    /// <summary>Raised after Home has finished showing a new state. The tray menu follows it.</summary>
    public event EventHandler<HomeState>? HomeStateChanged;

    // A palavra do estado troca com um cross-fade: o texto antigo sai por baixo enquanto o novo
    // entra, no mesmo lugar, e o valor real Ã© escrito na hora para que nenhuma leitura dependa do
    // fim da animaÃ§Ã£o.
    private void RenderStatusText(string text)
    {
        StatusText.Text = text;
        var previous = _renderedStatus;
        _renderedStatus = text;
        if (previous is null || previous == text || !StatusMotion.Allowed(_allowMotion)) return;
        StatusTextGhost.Text = previous;
        StatusTextGhost.Visibility = Visibility.Visible;
        StatusMotion.CrossFade(StatusText, StatusTextGhost, _allowMotion);
    }

    // O anel sÃ³ existe enquanto hÃ¡ trabalho a caminho. Com o anel desligado ele fica
    // parado, mas continua visÃ­vel: o estado ainda precisa ser lido sem movimento.
    private void SetStatusRing(bool busy)
    {
        var wanted = busy ? Visibility.Visible : Visibility.Collapsed;
        if (StatusRing.Visibility != wanted)
        {
            StatusRing.Visibility = wanted;
            if (!busy) StatusRingRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        }
        if (!busy) return;
        if (!StatusMotion.Allowed(_allowMotion)) { StatusRingRotation.Angle = 0; return; }
        if (StatusRingRotation.HasAnimatedProperties) return;
        StatusRingRotation.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, new Duration(TimeSpan.FromMilliseconds(1100))) { RepeatBehavior = RepeatBehavior.Forever });
    }

    // O nome do pais vem do .NET, sem pacote e sem tabela. EnglishName e nao DisplayName: o ICU
    // desta maquina devolve o nome nativo (Brasil, Deutschland, Haiti), o que misturaria idiomas
    // numa tela em ingles. Quando o seletor de idioma existir, ele troca esta chamada.
    // Um Ãºnico lugar resolve o nome do paÃ­s, como no item 2: a tabela CLDR em portuguÃªs e o
    // RegionInfo em inglÃªs, porque DisplayName devolve o idioma do prÃ³prio paÃ­s.
    private static string CountryName(string code) =>
        Localization.CountryNames.Name(code, Localization.LocalizationManager.Current.Language);

    // Um timer sÃ³, e sÃ³ enquanto o painel estÃ¡ na tela: minimizado ou na bandeja ele para, porque
    // ninguÃ©m estÃ¡ lendo os nÃºmeros.
    private void SyncDetailsTimer()
    {
        var shouldRun = _detailsVisible && IsVisible && WindowState != WindowState.Minimized && !_closing;
        if (shouldRun && !_detailsTimer.IsEnabled) { _detailsTimer.Start(); RenderDetails(); }
        else if (!shouldRun && _detailsTimer.IsEnabled) _detailsTimer.Stop();
    }

    private void RenderDetails()
    {
        if (!_detailsVisible) return;
        var reading = _traffic.Read();
        double down = 0, up = 0;
        if (reading is { } current && _lastReading is { } previous)
        {
            // Os contadores sÃ£o cumulativos e voltam a zero quando o adaptador sobe; um decrÃ©scimo
            // vira zero, nunca um pico negativo no grÃ¡fico.
            down = current.Received >= previous.Received ? current.Received - previous.Received : 0;
            up = current.Sent >= previous.Sent ? current.Sent - previous.Sent : 0;
            _totalReceived += (ulong)down;
            _totalSent += (ulong)up;
        }
        _lastReading = reading ?? _lastReading;
        _downSamples[_sampleCount % ChartSamples] = down;
        _upSamples[_sampleCount % ChartSamples] = up;
        _sampleCount++;
        DurationValue.Text = MetricsFormat.Duration(DateTime.UtcNow - _connectedSince);
        TrafficValue.Text = MetricsFormat.Size(_totalReceived + _totalSent);
        DownRateText.Text = MetricsFormat.Rate(down);
        UpRateText.Text = MetricsFormat.Rate(up);
        DrawChart();
    }

    private void DrawChart()
    {
        var width = ChartHost.ActualWidth;
        var height = ChartHost.ActualHeight;
        if (width <= 0 || height <= 0) return;
        var count = Math.Min(_sampleCount, ChartSamples);
        if (count < 2)
        {
            DownArea.Points = new PointCollection();
            DownLine.Points = new PointCollection();
            UpLine.Points = new PointCollection();
            return;
        }
        var down = Ordered(_downSamples, count, _sampleCount);
        var up = Ordered(_upSamples, count, _sampleCount);
        var peak = Math.Max(MetricsFormat.MinimumChartScale, Math.Max(down.Max(), up.Max()) * MetricsFormat.ChartHeadroom);
        // EspaÃ§amento fixo: a amostra mais nova encosta na direita e a linha anda para a esquerda,
        // em vez de esticar e comprimir a cada segundo.
        var step = width / (ChartSamples - 1);
        var area = new PointCollection(count + 2) { new System.Windows.Point(width - (count - 1) * step, height) };
        var line = new PointCollection(count);
        var downLine = new PointCollection(count);
        for (var i = 0; i < count; i++)
        {
            var x = width - (count - 1 - i) * step;
            var downPoint = new System.Windows.Point(x, height - Math.Min(height, down[i] / peak * height));
            area.Add(downPoint);
            // A linha de baixo Ã© um traÃ§o prÃ³prio: o polÃ­gono do preenchimento nÃ£o tem contorno,
            // senÃ£o as arestas de fechamento desenhavam uma barra vertical nas bordas.
            downLine.Add(downPoint);
            line.Add(new System.Windows.Point(x, height - Math.Min(height, up[i] / peak * height)));
        }
        area.Add(new System.Windows.Point(width, height));
        DownArea.Points = area;
        DownLine.Points = downLine;
        UpLine.Points = line;
    }

    // O anel guarda a amostra mais nova na posiÃ§Ã£o escrita por Ãºltimo. Quando encheu, a mais velha
    // Ã© a prÃ³xima a ser sobrescrita, que Ã© onde a leitura comeÃ§a.
    private static double[] Ordered(double[] ring, int count, int written)
    {
        var result = new double[count];
        var start = written >= ring.Length ? written % ring.Length : 0;
        for (var i = 0; i < count; i++) result[i] = ring[(start + i) % ring.Length];
        return result;
    }


    // Manual fallback for the missing SDK: the automatic installer is the normal path, and this
    // link is what a user has left when the download or the elevation prompt cannot run.
    private void SdkLink_Click(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://wiresock.net/wiresock-sdk") { UseShellExecute = true }); }
        catch { }
    }

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPage is not null || _navigationBusy || _closing) return;
        if (_state.StartupFailed) { RequestExit(); return; }
        if (_state.CanRestartElevated) { await (RestartElevatedOverride is not null ? RestartElevatedOverride() : RestartElevatedAsync()); return; }
        if (UiMutationBusy || !await _operation.WaitAsync(0)) return;
        BeginUiMutation();
        if (_state.RequiresSignIn)
        {
            try { OpenAccount(); }
            finally { EndUiMutation(); LeaveBusyPhase(); _operation.Release(); UpdateActionAvailability(); }
            return;
        }
        var command = _state.Snapshot?.HasOwnedTunnel == true ? "disconnect" : "connect";
        if (command == "connect" && !_confirmDiscordRestart && CheckDiscordOpen())
        {
            try
            {
                _confirmDiscordRestart = true;
                PrimaryButton.Content = Localization.LocalizationManager.Current["ConnectConfirmDiscordRestart"];
                StatusMotion.FadeIn(PrimaryButton, _allowMotion);
            }
            finally { EndUiMutation(); _operation.Release(); UpdateActionAvailability(); }
            return;
        }
        _confirmDiscordRestart = false;
        SetState(_state with { Phase = command == "connect" ? ConnectionPhase.Connecting : ConnectionPhase.Disconnecting, DetailKey = null });
        await RunPrimaryMutationAsync(command, scanFirst: false);
    }

    // A live Discord client turns Connect into a two-click action: the first click explains that
    // the client will restart. Tests inject the seam; production also checks the process list.
    private bool CheckDiscordOpen() => IsDiscordRunning?.Invoke()
        ?? (_state.Snapshot?.DiscordRunning == true || (_backend is BackendClient && IsAnyDiscordProcessRunning()));

    private static bool IsAnyDiscordProcessRunning()
    {
        foreach (var name in new[] { "Discord", "DiscordPTB", "DiscordCanary", "Vesktop", "Equibop", "Legcord" })
        {
            System.Diagnostics.Process[] processes;
            try { processes = System.Diagnostics.Process.GetProcessesByName(name); }
            catch { continue; }
            try
            {
                if (processes.Length > 0) return true;
            }
            finally
            {
                foreach (var process in processes)
                {
                    try { process.Dispose(); } catch { }
                }
            }
        }
        return false;
    }

    internal Action<string?, string?>? CommandFailureNotice { get; init; }

    private async Task RunPrimaryMutationAsync(string command, bool scanFirst)
    {
        var lifetime = _lifetime.Token;
        try
        {
            if (scanFirst)
            {
                try
                {
                    using var scanBudget = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                    scanBudget.CancelAfter(TimeSpan.FromSeconds(75));
                    var scan = await _backend.CommandAsync("optimize", RoutePayload(), scanBudget.Token);
                    if (scan.Success && scan.Route is { } freshRoute && !_closing && _state.Snapshot is { } current)
                        SetState(_state with { Snapshot = current with { Route = freshRoute } });
                }
                catch (OperationCanceledException)
                {
                    var cancelTimedOut = false;
                    try { await _backend.CancelAsync().WaitAsync(TimeSpan.FromSeconds(12)); }
                    catch (TimeoutException) { cancelTimedOut = true; }
                    catch { }
                    if (lifetime.IsCancellationRequested || _closing) throw;
                    if (cancelTimedOut) { await RefreshSnapshotAsync(IssueTicket(fromOperation: true), TimeSpan.FromSeconds(20)); return; }
                }
                catch (BackendUnavailableException) { throw; }
                catch { }
            }
            if (_closing) return;
            if (command == "connect") _nativeOperationMayBeActive = true;
            var pending = ExecuteWithVerificationAsync(command, (token, method) => new { humanVerificationToken = token, humanVerificationMethod = method });
            await TrackProgressAsync(pending, _operationEpoch);
            var result = await pending;
            // The SDK banner is an onboarding hint for an automatic install that failed, not a
            // launch warning: only these codes arm it for this session.
            if (result.Code is "WIRESOCK_SETUP_REQUIRED" or "SDK_INSTALL_STAYS_OPEN" or "SDK_INSTALL_DISCORD_CLOSED" or "SDK_INSTALL_SUPERVISION_LOST")
                _sdkSetupFailed = true;
            await RefreshSnapshotAsync(IssueTicket(fromOperation: true), TimeSpan.FromSeconds(20));
            if (!result.Success && !(result.Code == "ROUTE_UNVERIFIED" && _state.Phase == ConnectionPhase.Connected))
            {
                if (CommandFailureNotice is not null) CommandFailureNotice(result.Code, result.Message);
                else Localization.BackendText.Show(this, result.Code, result.Message, "Brisa");
            }
        }
        catch (OperationCanceledException) { if (!_closing) await RefreshSnapshotAsync(IssueTicket(fromOperation: true), TimeSpan.FromSeconds(20)); }
        catch (Exception ex) { if (CanCommit(IssueTicket(fromOperation: true))) SetError(ex, _state.Snapshot); }
        finally { EndUiMutation(); LeaveBusyPhase(); _operation.Release(); UpdateActionAvailability(); }
    }

    // Test seam only: production always runs the real hardened window.
    internal Func<VerificationChallenge, CancellationToken, Task<VerificationAnswer?>>? VerificationDialogFactory { get; init; }

    private async Task<VerificationAnswer?> ShowVerificationAsync(VerificationChallenge challenge, CancellationToken token)
    {
        var dialog = new VerificationWindow(challenge, token) { Owner = this };
        dialog.Show();
        return await dialog.Completion;
    }

    private async Task TrackProgressAsync(Task pending, long ownerEpoch)
    {
        while (!pending.IsCompleted && !_closing)
        {
            await Task.WhenAny(pending, Task.Delay(400));
            if (pending.IsCompleted || _closing) break;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                var progress = await _backend.CommandAsync("progress", new { }, timeout.Token);
                // Uma chave, nÃ£o uma frase: o detalhe Ã© resolvido na pintura, entÃ£o uma troca de
                // idioma no meio da operaÃ§Ã£o alcanÃ§a o texto que jÃ¡ estÃ¡ na tela.
                var detailKey = progress.Stage switch
                {
                    "preparing-profile" => "StagePreparingRoute",
                    "installing-wiresock" => "SdkPreparing",
                    "closing-discord" => "DiscordClosing",
                    "starting-tunnel" => "StageStartingTunnel",
                    "starting-discord" => "DiscordReopening",
                    "verifying-route" => "StageVerifyingRoute",
                    "stopping-tunnel" => "RestoringConnection",
                    _ => null
                };
                // Advisory only, and only while the operation that started it still owns the UI:
                // a late poll must not repaint a state the user has already left.
                Volatile.Write(ref _installerStageSeenTicks, progress.Stage == "installing-wiresock" ? DateTime.UtcNow.Ticks : 0L);
                if (detailKey is not null && !pending.IsCompleted && !_closing
                    && _operationOwnsUi && _operationEpoch == ownerEpoch) SetState(_state with { DetailKey = detailKey });
            }
            catch { /* Progress is advisory; the command still controls its outcome. */ }
        }
    }

    public async Task<CommandResult> ExecuteWithVerificationAsync(string command, Func<string?, string?, object> payloadFactory, CancellationToken cancellationToken = default)
    {
        await PreemptRouteScanAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        // Distinguish our own deadline from a caller or lifetime cancellation, so a timeout can be
        // reported instead of vanishing as a silent refresh.
        var deadlineExpired = false;
        using var deadline = timeout.Token.Register(() => deadlineExpired = true);
        var ceilingExpired = false;
        var connectExpired = false;
        Volatile.Write(ref _installerStageSeenTicks, 0L);
        // O prazo de trÃªs minutos mede o tempo em que o app estÃ¡ trabalhando. Enquanto o
        // instalador visÃ­vel do fornecedor estÃ¡ aberto, esse tempo nÃ£o corre; o teto de trinta
        // minutos continua correndo e Ã© o que impede uma janela ocupada para sempre.
        var startedAt = DateTime.UtcNow;
        var waited = TimeSpan.Zero;
        var lastTick = startedAt;
        // O laÃ§o tem o prÃ³prio CTS: ele pode terminar depois que este mÃ©todo retornar e descartar
        // `timeout`, e cancelar um CTS descartado lanÃ§aria em uma tarefa nÃ£o observada.
        using var watchdog = new CancellationTokenSource();
        // Descartar nÃ£o cancela: sem o Cancel no finally do try abaixo, o laÃ§o ainda pode passar
        // pela checagem e cancelar um `timeout` jÃ¡ descartado em uma tarefa nÃ£o observada.
        _ = Task.Run(async () =>
        {
            while (!watchdog.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromMilliseconds(500), watchdog.Token); } catch { return; }
                var now = DateTime.UtcNow;
                var ticks = Volatile.Read(ref _installerStageSeenTicks);
                var seenAt = ticks == 0 ? (DateTime?)null : new DateTime(ticks, DateTimeKind.Utc);
                var installerActive = InstallerDeadline.IsInstallerActive(seenAt, now);
                if (!installerActive) waited += now - lastTick;
                lastTick = now;
                var expiry = InstallerDeadline.Decide(startedAt, waited, now, installerActive);
                if (expiry == InstallerDeadline.Expiry.None) continue;
                if (watchdog.IsCancellationRequested) return;
                ceilingExpired = expiry == InstallerDeadline.Expiry.Ceiling;
                connectExpired = expiry == InstallerDeadline.Expiry.Connect;
                timeout.Cancel();
                return;
            }
        });
        var presented = false;
        string? token = null, method = null;
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var result = await _backend.CommandAsync(command, payloadFactory(token, method), timeout.Token);
                if (result.Success || result.CaptchaUrl is null) return result;
                if (!VerificationPolicy.TryParseChallenge(result.CaptchaUrl, out var challenge) || challenge is null)
                    return result with { Message = LocalizationManager.Current["VerificationRejected"] };
                VerificationAnswer? answer;
                try
                {
                    presented = true;
                    answer = VerificationDialogFactory is null
                        ? await ShowVerificationAsync(challenge, timeout.Token)
                        : await VerificationDialogFactory(challenge, timeout.Token);
                }
                catch (VerificationUnavailableException)
                {
                    // A window that never started is not a cancelled verification.
                    await _backend.CancelAsync();
                    return new(false, "VERIFICATION_UNAVAILABLE", "The verification window could not start. Install or repair the WebView2 Runtime, then try again.");
                }
                if (answer is null) throw new OperationCanceledException(timeout.Token);
                token = answer.Token; method = answer.Method;
            }
            return new(false, "VERIFICATION_LIMIT", "Verification could not be completed after two retries.");
        }
        catch (OperationCanceledException) when (deadlineExpired && ceilingExpired)
        {
            // The installer never closed. Do not let this look like an ordinary cancel: say what
            // is on screen and leave the vendor process alone, since the backend waits for it.
            // Only an acknowledgement is asked for: the full cancel waits for idle, which cannot
            // happen while the installer is open, so it would block the message for 45 seconds.
            using var ack = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await _backend.CommandAsync("cancel", new { }, ack.Token); } catch { }
            return new(false, "SDK_INSTALLER_STILL_OPEN", LocalizationManager.Current["SdkInstallerOpen"]);
        }
        catch (OperationCanceledException) when (deadlineExpired && connectExpired)
        {
            // The app spent its working budget and gave up. The user did not cancel, so this must
            // not read as a quiet refresh.
            try { await _backend.CancelAsync(); } catch { }
            return new(false, "CONNECT_TIMEOUT", "Connecting took too long and was stopped. Check the connection status, then try again.");
        }
        catch (OperationCanceledException) when (deadlineExpired && presented)
        {
            await _backend.CancelAsync();
            return new(false, "VERIFICATION_TIMEOUT", "Verification timed out. Try again.");
        }
        catch (OperationCanceledException) { await _backend.CancelAsync(); throw; }
        finally { watchdog.Cancel(); }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_navigationBusy || _closing || UiMutationBusy) return;
        OpenSettings();
    }
    private void OpenSettings()
    {
        ShowPage(CreateSettingsView(), Localization.LocalizationManager.Current["SettingsTitle"]);
    }

    private SettingsView CreateSettingsView()
    {
        var carried = _settingsDraft;
        _settingsDraft = null;
        var view = new SettingsView(_settings, draft => _ = ShowAdvancedAsync(AdvancedOrigin.Settings, draft), carried, _allowMotion);
        view.CloseRequested += async (_, _) =>
        {
            // A preferÃªncia do painel pode ter mudado: repinta o Home antes de voltar, senÃ£o o
            // painel sÃ³ apareceria no prÃ³ximo snapshot.
            if (_currentPage is SettingsView) SetState(_state);
            await ReturnHomeAsync();
        };
        view.Saved += (_, _) =>
        {
            // Saving stays on the page, so Home and the page title have to follow immediately.
            SetState(_state);
            PageTitle.Text = Localization.LocalizationManager.Current["SettingsTitle"];
        };
        return view;
    }
    private void Account_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPage is not null || _navigationBusy || _closing || UiMutationBusy || !AccountButton.IsEnabled) return;
        OpenAccount();
    }
    private void OpenAccount() => ShowPage(new AccountView(_backend, this, _state.Snapshot), Localization.LocalizationManager.Current["Account"]);
    private void ShowPage(UserControl page, string title)
    {
        // Any page change invalidates snapshots issued for the previous surface.
        _navigationEpoch++;
        _confirmDiscordRestart = false;
        PageTransition.Stop(HomeContent);
        _currentPage = page; PageHost.Content = page;
        PageTitle.Text = title;
        PageTitle.Visibility = Visibility.Visible;
        HomeContent.Visibility = HomeActions.Visibility = Visibility.Collapsed;
        PageHost.Visibility = BackButton.Visibility = Visibility.Visible;
        CloseNotifications();
        UpdateNotificationsHost();
        PageTransition.Show(PageHost);
        // O foco vai para a pÃ¡gina, nÃ£o para o Voltar: quem abre pelo mouse nÃ£o deve ver um anel
        // de foco no botÃ£o, e quem usa teclado continua com Shift+Tab chegando nele.
        page.Focusable = true;
        page.Focus();
    }
    private async void Back_Click(object sender, RoutedEventArgs e) => await GoBackAsync();

    /// <summary>
    /// Advanced fica um passo Ã  frente de Settings: voltar leva de volta Ã  pÃ¡gina que o abriu,
    /// nÃ£o ao inÃ­cio, senÃ£o o caminho de volta perde um passo.
    /// </summary>
    private async Task GoBackAsync()
    {
        // Advanced fica um passo Ã  frente da pÃ¡gina que o abriu. Settings e Account voltam por
        // troca direta: passar por Home piscaria a tela e esperaria um snapshot Ã  toa.
        var origin = _currentPage is AdvancedView ? _advancedOrigin : AdvancedOrigin.None;
        _advancedOrigin = AdvancedOrigin.None;
        if (origin == AdvancedOrigin.Settings)
        {
            await SwapPageAsync(CreateSettingsView, Localization.LocalizationManager.Current["SettingsTitle"]);
            return;
        }
        if (origin == AdvancedOrigin.Account)
        {
            // Importing a .conf changes the mode: read it before deciding whether the account
            // page still applies. This wait is on the page path only, never on Home.
            await RefreshSnapshotAsync(IssueTicket(), TimeSpan.FromSeconds(5));
            if (_state.RequiresSignIn)
            {
                await SwapPageAsync(() => new AccountView(_backend, this, _state.Snapshot), Localization.LocalizationManager.Current["Account"]);
                return;
            }
        }
        await ReturnHomeAsync();
    }
    private async void OnNavigationKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (_currentPage is null || !(key == Key.Escape || key == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt)) return;
        e.Handled = true;
        await GoBackAsync();
    }
    private Task ReturnHomeAsync()
    {
        if (_returnHomeTask is { IsCompleted: false }) return _returnHomeTask;
        return _returnHomeTask = ReturnHomeCoreAsync();
    }
    private async Task ReturnHomeCoreAsync()
    {
        if (_currentPage is null || _navigationBusy || _closing) return;
        var page = _currentPage;
        _navigationBusy = true; BackButton.IsEnabled = false; page.IsEnabled = false;
        UpdateActionAvailability();
        try
        {
            if (page is IAsyncDisposable disposable) await disposable.DisposeAsync();
            if (_closing) return;
            PageTransition.Stop(PageHost);
            PageHost.Content = null; _currentPage = null;
            PageHost.Visibility = BackButton.Visibility = Visibility.Collapsed;
            HomeContent.Visibility = HomeActions.Visibility = Visibility.Visible;
            UpdateNotificationsHost();
            PageTransition.Show(HomeContent);
            // A home nÃ£o tem tÃ­tulo: o nome do app jÃ¡ estÃ¡ na barra, e qualquer palavra acima do
            // estado ("Connected") sÃ³ repete o estado.
            PageTitle.Text = "";
            PageTitle.Visibility = Visibility.Collapsed;
            _navigationEpoch++;
        }
        catch { if (!_closing) MessageBox.Show(this, Localization.LocalizationManager.Current["AccountOperationStopping"], "Brisa", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally
        {
            _navigationBusy = false; BackButton.IsEnabled = true; UpdateActionAvailability();
            if (_currentPage is null && !_closing)
            {
                // Voltar o foco para o botÃ£o que abriu a pÃ¡gina Ã© certo no teclado. No mouse, o anel
                // de foco apareceria sem o usuÃ¡rio ter pedido: nesse caso o foco vai para a pÃ¡gina,
                // que nÃ£o pinta retÃ¢ngulo.
                if (_keyboardInput) (page is SettingsView ? SettingsButton : AccountButton).Focus();
                else HomeContent.Focus();
            }
        }
        // Outside _returnHomeTask: hiding the window must not wait for a status poll, and an
        // immediate Connect must not be delayed by one either. Snapshot commits keep the ticket
        // gate, so this background refresh can never overwrite a newer operation.
        _ = RefreshAfterReturnAsync(IssueTicket());
    }

    private async Task RefreshAfterReturnAsync(SnapshotTicket ticket)
    {
        try { await RefreshSnapshotAsync(ticket, TimeSpan.FromSeconds(5)); } catch { }
    }
    private void UpdateActionAvailability()
    {
        var available = !_navigationBusy && !_closing;
        PrimaryButton.IsEnabled = available && _state.PrimaryEnabled && !UiMutationBusy;
        RouteButton.IsEnabled = available && CanChooseRoute;
        AccountButton.IsEnabled = available && !UiMutationBusy;
        // Settings must not be reachable mid-mutation, but it has to come back afterwards.
        SettingsButton.IsEnabled = available && !UiMutationBusy;
    }

    // One eligibility definition shared by the control and the handler: a queued or directly
    // raised event can still fire while the button is disabled.
    private bool CanChooseRoute => !_state.RequiresSignIn && _state.Phase == ConnectionPhase.Disconnected
        && _state.Snapshot is { ExternalTunnel: false, Reliable: true, HasOwnedTunnel: false };

    // The saved country feeds every scan, so a launch never silently replaces a route the user chose.
    private object RoutePayload() => string.IsNullOrEmpty(_settings.Current.PreferredRouteCountry)
        ? (object)new { } : new { country = _settings.Current.PreferredRouteCountry };

    // Automatic is saved as null. Only the route changes here, so the startup mode is the same and
    // the registrar is never asked, which means no administrator prompt from a route choice.
    private void RememberRoute(string? country)
    {
        try { if (_settings.Current.PreferredRouteCountry != country) _settings.Save(_settings.Current with { PreferredRouteCountry = country }); }
        catch { }
    }

    private async void Route_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPage is not null || _navigationBusy || _closing || !CanChooseRoute) return;
        // Opening Route ends the previous Connect intent, even when the dialog is cancelled.
        _confirmDiscordRestart = false;
        PrimaryButton.Content = _state.PrimaryLabel;
        var dialog = new RouteWindow(this, _settings.Current.PreferredRouteCountry, _state.Snapshot?.Route) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        // The modal pumps the dispatcher, so eligibility can change while it is open. The
        // selection is never queued or replayed later; the user is told instead.
        if (_currentPage is not null || _navigationBusy || _closing || !CanChooseRoute)
        { _notice(Localization.LocalizationManager.Current["RouteStateChanged"]); return; }
        if (!await _operation.WaitAsync(0))
        { _notice(Localization.LocalizationManager.Current["RouteStateChanged"]); return; }
        BeginUiMutation();
        try
        {
            // Optimization can take minutes, so the window says so instead of looking idle.
            // The detail stays generic: the backend does not promise a useful optimize stage.
            SetState(new HomeState(ConnectionPhase.Optimizing, _state.Snapshot, "DetailOptimizing"));
            var pending = ExecuteWithVerificationAsync("optimize", (token, method) => new { country = dialog.SelectedCountry, humanVerificationToken = token, humanVerificationMethod = method });
            await TrackProgressAsync(pending, _operationEpoch);
            var result = await pending;
            if (result.Success) RememberRoute(dialog.SelectedCountry);
            else Localization.BackendText.Show(this, result.Code, result.Message, Localization.LocalizationManager.Current["Route"]);
            await RefreshSnapshotAsync(IssueTicket(fromOperation: true), TimeSpan.FromSeconds(20));
        }
        catch (OperationCanceledException) { if (!_closing) await RefreshSnapshotAsync(IssueTicket(fromOperation: true), TimeSpan.FromSeconds(20)); }
        catch (Exception ex) { MessageBox.Show(this, SafeMessage(ex), "Route", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { EndUiMutation(); LeaveBusyPhase(); _operation.Release(); UpdateActionAvailability(); }
    }
    private UserSettings? _settingsDraft;
    private AdvancedOrigin _advancedOrigin;

    internal async Task ShowAdvancedAsync(AdvancedOrigin origin, UserSettings? draft = null)
    {
        // Advanced Ã© pÃ¡gina, nÃ£o diÃ¡logo: mesma janela, mesmo botÃ£o Voltar do cabeÃ§alho. O guarda
        // continua porque navegar durante uma transiÃ§Ã£o ou uma mutaÃ§Ã£o trabalharia com um snapshot
        // que jÃ¡ nÃ£o corresponde ao estado.
        if (_navigationBusy || _closing || UiMutationBusy || _currentPage is not (SettingsView or AccountView)) return;
        _advancedOrigin = origin;
        _settingsDraft = origin == AdvancedOrigin.Settings ? draft : null;
        await SwapPageAsync(
            () => new AdvancedView(_backend, _state.Snapshot, ImportConfigAsync, updates: _updates, restartForUpdate: RequestUpdateRestart),
            Localization.LocalizationManager.Current["Advanced"]);
    }

    /// <summary>
    /// Replaces one page with another without showing Home in between. The page being left is
    /// disposed first, so its lifetime and any pending work end before the next page starts.
    /// </summary>
    private async Task SwapPageAsync(Func<UserControl> next, string title)
    {
        if (_navigationBusy || _closing || UiMutationBusy || _currentPage is null) return;
        var page = _currentPage;
        _navigationBusy = true; BackButton.IsEnabled = false; page.IsEnabled = false;
        UpdateActionAvailability();
        try
        {
            if (page is IAsyncDisposable disposable) await disposable.DisposeAsync();
            if (_closing) return;
            PageHost.Content = null; _currentPage = null;
            ShowPage(next(), title);
            PageTransition.Show(PageHost);
        }
        finally
        {
            _navigationBusy = false; BackButton.IsEnabled = true; UpdateActionAvailability();
        }
    }

    /// <summary>
    /// Advanced's import runs through the same UI mutation gate as Connect, so a dialog opened
    /// from a stale snapshot cannot issue a command mid-connect.
    /// </summary>
    internal async Task<CommandResult> ImportConfigAsync(string path)
    {
        const string busy = "Another operation is still running. Wait for it to finish and try importing again.";
        if (_closing || UiMutationBusy) return new(false, "UI_BUSY", busy);
        if (!await _operation.WaitAsync(0)) return new(false, "UI_BUSY", busy);
        BeginUiMutation();
        try
        {
            if (_closing) return new(false, "UI_BUSY", busy);
            await PreemptRouteScanAsync();
            return await _backend.CommandAsync("importConfig", new { path }, _lifetime.Token);
        }
        finally { EndUiMutation(); _operation.Release(); UpdateActionAvailability(); }
    }

    private async Task RunSmokeAsync(string output)
    {
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (ActualWidth < 440 || ActualHeight < 520 || PrimaryButton is null || RouteButton is null) throw new InvalidOperationException("Loaded-window assertions failed.");
        var directory = Path.GetDirectoryName(output); if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        void Capture(Window view, string file)
        {
            view.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(view);
            var bitmap = new RenderTargetBitmap((int)(view.ActualWidth * dpi.DpiScaleX), (int)(view.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(view); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(file); encoder.Save(stream);
        }
        Capture(this, output);
        foreach (var button in new[] { SettingsButton, AccountButton })
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            if (_currentPage is null || System.Windows.Application.Current.Windows.Count != 1 || HomeContent.IsVisible)
                throw new InvalidOperationException("Inline navigation must use the same window.");
            Capture(this, Path.Combine(directory ?? ".", Path.GetFileNameWithoutExtension(output) + "-" + _currentPage.GetType().Name + ".png"));
            await ReturnHomeAsync();
        }
        var surfaces = new Window[] { new RouteWindow(this) };
        foreach (var view in surfaces)
        {
            view.Owner = this; view.Show();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            if (view.ActualWidth < 200 || view.ActualHeight < 100) throw new InvalidOperationException(LocalizationManager.Current["SecondaryWindowFailed"]);
            Capture(view, Path.Combine(directory ?? ".", Path.GetFileNameWithoutExtension(output) + "-" + view.GetType().Name + ".png"));
            view.Close();
        }
        RequestExit();
    }
    private void Notifications_Click(object sender, RoutedEventArgs e)
    {
        if (_notifications is null || _closing || _currentPage is not null) return;
        if (NotificationsPopup.IsOpen) { NotificationsPopup.IsOpen = false; return; }
        // A click on the bell while the popup is open closes it on mouse down; the click that
        // follows must not open it again.
        if (NotificationsButton.IsMouseOver && Environment.TickCount64 - _notificationsClosedAt < 250) return;
        NotificationsPopup.IsOpen = true;
    }
    private void NotificationsPopup_Opened(object? sender, EventArgs e)
    {
        if (_notifications is null) return;
        _notifications.BeginPopoverSession();
        NotificationsPanel.Render(_notifications.Snapshot);
        Dispatcher.BeginInvoke(NotificationsPanel.FocusFirst, System.Windows.Threading.DispatcherPriority.Input);
    }
    private void NotificationsPopup_Closed(object? sender, EventArgs e)
    {
        _notificationsClosedAt = Environment.TickCount64;
        _notifications?.EndPopoverSession();
    }
    private void CloseNotifications() { if (NotificationsPopup.IsOpen) NotificationsPopup.IsOpen = false; }
    private void UpdateNotificationsHost() =>
        NotificationsHost.Visibility = _notifications is not null && _currentPage is null ? Visibility.Visible : Visibility.Collapsed;
    private void OnNotificationsChanged(object? sender, NotificationSnapshot snapshot)
    {
        if (_closing) return;
        ApplyNotificationBadge(snapshot);
        NotificationsPanel.Render(snapshot);
    }
    private void ApplyNotificationBadge(NotificationSnapshot snapshot)
    {
        var count = snapshot.BadgeCount;
        NotificationsBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NotificationsBadgeText.Text = count > 9 ? "9+" : count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        System.Windows.Automation.AutomationProperties.SetHelpText(NotificationsButton, count > 0
            ? string.Format(Localization.LocalizationManager.Current["NotificationsUnreadCount"], count) : "");
    }
    public void RequestExit() { _explicitExit = true; Close(); }
    public void RequestUpdateRestart()
    {
        if (_updates?.HasPendingUpdate != true || _closing) return;
        _applyUpdateOnExit = true;
        _explicitExit = true;
        Close();
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_canClose) return;
        _confirmDiscordRestart = false;
        PrimaryButton.Content = _state.PrimaryLabel;
        CloseNotifications();
        PageTransition.Stop(PageHost);
        PageTransition.Stop(HomeContent);
        e.Cancel = true;
        if (!_explicitExit && _smokeOutput is null &&
            !(_state.StartupFailed && _terminalBackendFailure && !_nativeOperationMayBeActive))
        {
            if (_currentPage is AccountView || _returnHomeTask is { IsCompleted: false }) await ReturnHomeAsync();
            if (!_closing && !_canClose) Hide();
            return;
        }
        if (_closing) return;
        _closing = true;
        var mayBeActive = _nativeOperationMayBeActive || _state.Snapshot?.HasOwnedTunnel == true || _state.Phase == ConnectionPhase.Connecting;
        _lifetime.Cancel();
        var acquired = false;
        try
        {
            if (_currentPage is IAsyncDisposable disposable) await disposable.DisposeAsync();
            // ExitGuard performs the final native cancel only after active work has joined.
            acquired = await _operation.WaitAsync(TimeSpan.FromSeconds(15));
            if (!acquired) throw new InvalidOperationException("The current operation is still stopping. Please try exiting again.");
            var decision = await ExitGuard.DecideAsync(_backend, mayBeActive);
            var safeToApplyUpdate = decision.SafeToApplyUpdate;
            if (decision.Warning is { } warning && !_exitConfirm(warning))
            {
                _closing = false; _explicitExit = false; _applyUpdateOnExit = false;
                _lifetime.Dispose(); _lifetime = new();
                return;
            }
            await _backend.DisposeAsync();
            if (_updates is not null) await _updates.DisposeAsync();
            if (_notifications is not null) await _notifications.DisposeAsync();
            if (safeToApplyUpdate && _updates?.HasPendingUpdate == true)
            {
                // The updater process is launched only after account work, tunnel
                // ownership checks, and backend disposal have all completed.
                try { _updates?.PrepareApply(restart: _applyUpdateOnExit); } catch { }
            }
            _canClose = true;
            _ = Dispatcher.BeginInvoke(Close);
        }
        catch (Exception ex)
        {
            _closing = false; _explicitExit = false; _applyUpdateOnExit = false;
            _lifetime.Dispose(); _lifetime = new();
            _exitWarning(ex is BackendUnavailableException
                ? LocalizationManager.Current["ServiceStoppedUnsafe"]
                : ex is InvalidOperationException ? ex.Message : LocalizationManager.Current["ServiceStoppingRetry"]);
        }
        finally { if (acquired) _operation.Release(); }
    }

    private string SafeMessage(Exception ex) => LocalizationManager.Current[SafeMessageKey(ex)];

    private string SafeMessageKey(Exception ex) => ex switch
    {
        TimeoutException => "ServiceTimeout",
        InvalidDataException => "ServiceUnreadableResponse",
        FileNotFoundException => "BackendNotInstalled",
        BackendUnavailableException => _nativeOperationMayBeActive ? "ServiceStoppedUnsafe" : "ServiceStoppedRetry",
        _ => "ServiceActionIncomplete"
    };
}

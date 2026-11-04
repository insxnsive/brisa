using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Brisa;
using Brisa.Models;
using Brisa.Services;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        // Load the production resources, not App's startup handlers: this harness
        // must never construct the real backend or inspect saved account state.
#pragma warning disable WPF0001
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, ThemeMode = ThemeMode.Dark };
#pragma warning restore WPF0001
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Brisa;component/Themes/NativeTheme.xaml") });
        var root = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "native-appearance-" + Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(root, false);
        var assertions = 0;
        // WPF can clone templates when switching theme dictionaries. Verify
        // inheritance instead of comparing reference identity across themes.
        bool MatchesTemplate(Control control, string key)
        {
            for (var style = control.Style?.BasedOn; style is not null; style = style.BasedOn)
                foreach (var setter in style.Setters.OfType<Setter>())
                    if (setter.Property == Control.TemplateProperty && setter.Value is ControlTemplate inherited)
                        return control.Template is not null && inherited.TargetType == Template(app, key).TargetType && TemplateShape(control.Template) == TemplateShape(Template(app, key));
            return false;
        }
        try
        {
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                store.Save(new(theme));
                var backend = new UiTestBackendClient(new(false, true, true, false, "", null));
                var main = new MainWindow(backend, store);
                main.Show();
                main.InitializeAsync().GetAwaiter().GetResult();
                main.UpdateLayout();
                var primary = (Button)main.FindName("PrimaryButton");
                var route = (Button)main.FindName("RouteButton");
                Check(!primary.IsEnabled && !route.IsEnabled, "blocked state remains disabled");
                Check(main.FindName("ConnectionSurface") is Border { CornerRadius.TopLeft: 8 }, "Home uses one quiet, rounded connection surface");
                Check(MatchesTemplate(primary, "AccentButtonStyle"), "primary action must use Microsoft's Fluent accent template");
                Check(MatchesTemplate(route, "DefaultButtonStyle"), "route must retain Microsoft's Fluent button template");
                Check(primary.ActualWidth >= 300 && primary.ActualHeight >= 40, "primary action retains its full hit target");
                var border = (Border?)primary.Template.FindName("ContentBorder", primary);
                Check(border is not null && border.CornerRadius.TopLeft >= 4, "disabled primary keeps native rounded corners");
                var surface = border!.Background as SolidColorBrush;
                var canvas = (SolidColorBrush)main.Background;
                var effectiveRed = surface is null ? 255 : surface.Color.R * (surface.Color.A / 255.0) + canvas.Color.R * (1 - surface.Color.A / 255.0);
                Check(surface is not null && (theme == AppTheme.Dark ? effectiveRed < 150 : effectiveRed > 150), "disabled surface follows the actual theme");
                var original = theme;
                foreach (var next in new[] { theme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark, original })
                {
                    store.Save(new(next));
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    main.UpdateLayout();
                    var fill = (SolidColorBrush)((Border)primary.Template.FindName("ContentBorder", primary)).Background;
                    var backdrop = (SolidColorBrush)main.Background;
                    var red = fill.Color.R * (fill.Color.A / 255.0) + backdrop.Color.R * (1 - fill.Color.A / 255.0);
                    Check(fill.Color == ((SolidColorBrush)primary.FindResource("AccentButtonBackgroundDisabled")).Color, "disabled fill follows the Fluent resource");
                    Check(next == AppTheme.Dark ? red < 150 : red > 150, "already-open disabled controls update on " + next + " switch");
                    Check(((Border)primary.Template.FindName("ContentBorder", primary)).CornerRadius.TopLeft >= 4, "theme switch keeps Fluent corners");
                }
                Capture(main, theme + "-Blocked");
                main.Width = main.MinWidth; main.Height = main.MinHeight; main.UpdateLayout();
                var routeBottom = route.TranslatePoint(new Point(0, route.ActualHeight), main).Y;
                var primaryTop = primary.TranslatePoint(new Point(), main).Y;
                Check(routeBottom + 12 <= primaryTop, "minimum-size Home retains route/action separation");
                Check(((TextBlock)main.FindName("DetailText")).ActualHeight >= 38, "blocked explanation keeps its space at minimum size");
                Capture(main, theme + "-Blocked-Minimum");
                foreach (var pageName in new[] { "Settings", "Account", "Route", "Advanced" })
                {
                    var inline = pageName is "Settings" or "Account" or "Advanced";
                    Window window;
                    FrameworkElement view;
                    if (inline)
                    {
                        // Advanced fica um passo à frente de Settings: abre pelo botão de lá.
                        if (pageName == "Advanced")
                        {
                            ((Button)main.FindName("SettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                            main.UpdateLayout();
                            var settingsView = (FrameworkElement)((ContentControl)main.FindName("PageHost")).Content;
                            ((Button)settingsView.FindName("AdvancedButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        }
                        else ((Button)main.FindName(pageName + "Button")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        window = main;
                        view = (FrameworkElement)((ContentControl)main.FindName("PageHost")).Content;
                    }
                    else
                    {
                        window = new RouteWindow(main);
                        window.Owner = main; window.Show(); view = window;
                    }
                    window.UpdateLayout();
                    if (view is AccountView)
                    {
                        ((StackPanel)view.FindName("TwoFactorPanel")).Visibility = Visibility.Visible;
                        ((TextBlock)view.FindName("Subtitle")).Text = "Enter your password again with your two-factor code.";
                        window.UpdateLayout();
                        var codeBox = (PasswordBox)view.FindName("TwoFactorInput");
                        var action = (Button)view.FindName("ActionButton");
                        Check(codeBox.TranslatePoint(new Point(0, codeBox.ActualHeight), window).Y + 12 <= action.TranslatePoint(new Point(), window).Y, "2FA field clears the account footer");
                    }
                    if (view is SettingsView)
                    {
                        ((CheckBox)view.FindName("StartupBox")).IsChecked = true;
                        Check(view.FindName("TrayBox") is null, "close-to-tray is unconditional, not an opt-in setting");
                        var settingsText = Descendants(view).OfType<TextBlock>().Select(t => t.Text).ToList();
                        Check(!settingsText.Contains("Appearance, startup, and updates."), "Settings carries no subtitle");
                        Check(settingsText.Contains("General"), "the startup section is called General");
                        Check(!settingsText.Contains("Behavior"), "the old Behavior heading is gone");
                        Check(((CheckBox)view.FindName("StartupBox")).Content as string == "Start with Windows",
                            "the startup checkbox says Start with Windows");
                        Check(settingsText.Contains("Closing the window keeps Brisa running in the tray. Use Exit in the tray menu to quit."),
                            "the tray note is the new copy");
                        Check(Descendants(view).OfType<ScrollViewer>().First().ScrollableHeight == 0,
                            "Settings does not scroll at the default size");
                        // Item 9: a preferência do painel mora em General, ao lado do início com o Windows.
                        var details = (CheckBox)view.FindName("DetailsBox");
                        Check(details.Content as string == "Show connection details", "the details preference is labelled Show connection details");
                        Check(details.IsChecked == true, "the details panel is on by default");
                        Check(details.TranslatePoint(new Point(), view).Y > ((CheckBox)view.FindName("StartupBox")).TranslatePoint(new Point(), view).Y,
                            "the details checkbox sits under Start with Windows in General");
                        // O anel de foco só aparece com foco de teclado: UIA SetFocus não pinta,
                        // então a evidência do anel sai daqui e os estados checados saem do app real.
                        var startup = (CheckBox)view.FindName("StartupBox");
                        startup.Focus();
                        System.Windows.Input.Keyboard.Focus(startup);
                        window.UpdateLayout(); Spin(120);
                        Check(startup.IsKeyboardFocused, "the startup checkbox takes keyboard focus");
                        Capture(window, theme + "-SettingsFocus");
                        startup.IsChecked = false;
                        window.UpdateLayout();
                        foreach (var buttonName in new[] { "AdvancedButton", "CancelButton", "SaveButton" })
                        {
                            var button = (Button)view.FindName(buttonName);
                            var center = button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), window);
                            var hit = window.InputHitTest(center) as DependencyObject;
                            while (hit is not null && hit != button) hit = VisualTreeHelper.GetParent(hit);
                            Check(hit == button, buttonName + " stays reachable on Settings");
                        }
                    }
                    if (view is AdvancedView)
                    {
                        var credits = (Expander)view.FindName("CreditsExpander");
                        Check(!credits.IsExpanded, "credits start collapsed");
                        Check(!Descendants(credits).OfType<TextBlock>().Any(t => t.IsVisible && t.Text.Contains("GPL-3.0")),
                            "collapsed credits hide the licence text");
                        credits.IsExpanded = true;
                        view.UpdateLayout();
                        Check(Descendants(credits).OfType<TextBlock>().Any(t => t.Text.Contains("GPL-3.0")),
                            "the GPL notice is displayed inside the credits");
                        Check(Descendants(credits).OfType<TextBlock>().Any(t => t.Text.Contains("GoLiveBypass")),
                            "the upstream attribution is displayed inside the credits");
                        // Hyperlink é TextElement, não Visual: não aparece no walk da árvore visual.
                        var attribution = Descendants(credits).OfType<TextBlock>().First(t => t.Text.Contains("GoLiveBypass"));
                        var link = attribution.Inlines.OfType<System.Windows.Documents.Hyperlink>().First();
                        Check(((SolidColorBrush)link.Foreground).Color == ((SolidColorBrush)Application.Current.Resources["NativeLinkForeground"]).Color,
                            "the licence link uses the pinned accent, not the system hyperlink colour");
                        var status = (TextBlock)view.FindName("UpdateStatusText");
                        status.Text = "A versioned Brisa update is ready. Review the release details before restarting. " +
                            "The current connection remains available while the update is prepared in this isolated fixture.";
                        window.Width = 440; window.Height = 520; window.UpdateLayout();
                        Check(status.TranslatePoint(new Point(status.ActualWidth, 0), view).X <= view.ActualWidth + 1,
                            "long update status wraps inside Advanced at minimum size");
                        foreach (var text in Descendants(credits).OfType<TextBlock>().Where(t => t.IsVisible && t.TextWrapping == TextWrapping.Wrap))
                            Check(text.TranslatePoint(new Point(text.ActualWidth, 0), view).X <= view.ActualWidth + 1,
                                $"credit line {text.Name} wraps inside Advanced");
                        // A escala de fonte aproxima pressão de conteúdo; não mexe no DPI do sistema.
                        foreach (var simulatedScale in new[] { 1d, 1.25d, 1.5d, 2d })
                        {
                            status.FontSize = 14 * simulatedScale;
                            window.UpdateLayout();
                            Check(status.TranslatePoint(new Point(status.ActualWidth, 0), view).X <= view.ActualWidth + 1,
                                $"update status wraps at simulated {simulatedScale:P0} scale on Advanced");
                        }
                        status.FontSize = 14;
                        var pageScroll = Descendants(view).OfType<ScrollViewer>().First();
                        pageScroll.ScrollToBottom(); window.UpdateLayout();
                        var checkButton = (Button)view.FindName("CheckUpdateButton");
                        checkButton.IsEnabled = true; // Hit-test the disabled isolated-session action without invoking it.
                        var center = checkButton.TranslatePoint(new Point(checkButton.ActualWidth / 2, checkButton.ActualHeight / 2), window);
                        var hit = window.InputHitTest(center) as DependencyObject;
                        while (hit is not null && hit != checkButton) hit = VisualTreeHelper.GetParent(hit);
                        Check(hit == checkButton, "the update action stays reachable on Advanced");
                        checkButton.IsEnabled = false;
                        credits.IsExpanded = false;
                        // Volta ao estado real antes da captura: o texto longo é só para medir.
                        status.Text = "Updates are unavailable in this isolated session.";
                        window.Width = 460; window.Height = 540;
                        pageScroll.ScrollToTop(); window.UpdateLayout();
                        credits.IsExpanded = true; window.UpdateLayout(); Spin(120);
                        Capture(window, theme + "-Advanced-Credits");
                        credits.IsExpanded = false; window.UpdateLayout();
                    }
                    foreach (var button in Descendants(view).OfType<Button>().Where(b => b.IsVisible && b.Content is string && b.Name != "CheckUpdateButton"))
                    {
                        Check(button.ActualHeight >= 30, $"{window.GetType().Name}: {button.Content} keeps its height");
                        var center = button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), window);
                        var hit = window.InputHitTest(center) as DependencyObject;
                        while (hit is not null && hit != button) hit = VisualTreeHelper.GetParent(hit);
                        Check(hit == button, $"{window.GetType().Name}: {button.Content} must not be clipped or covered");
                    }
                    foreach (var control in Descendants(view).OfType<Control>())
                    {
                        var key = control switch { PasswordBox => "DefaultPasswordBoxStyle", TextBox => "DefaultTextBoxStyle", ListBox => "DefaultListBoxStyle", CheckBox => "DefaultCheckBoxStyle", RadioButton => "DefaultRadioButtonStyle", _ => null };
                        if (key is not null) Check(MatchesTemplate(control, key), control.GetType().Name + " retains its Fluent template");
                    }
                    // A transição de página anima a opacidade: capturar antes do fim entrega uma
                    // página lavada, que não serve para julgar contraste.
                    Spin(320);
                    Capture(window, theme + "-" + view.GetType().Name);
                    if (inline)
                    {
                        // Advanced devolve a Settings antes de voltar ao início, então precisa de dois Voltar.
                        var backs = pageName == "Advanced" ? 2 : 1;
                        for (var i = 0; i < backs; i++)
                        {
                            ((Button)main.FindName("BackButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                        }
                        Check(((ContentControl)main.FindName("PageHost")).Content is null, "inline Back restores Home");
                    }
                    else window.Close();
                }
                foreach (var (phase, message, label) in new[]
                {
                    (UpdatePhase.Current, "Brisa is up to date.", "UpToDate"),
                    (UpdatePhase.Ready, "Version 0.1.0-beta.10 is ready. Restart to update.", "UpdateAvailable"),
                })
                {
                    // Fixture render, rotulado: o app isolado só produz o estado "unavailable".
                    var fixture = new FixtureUpdates(new(phase, message, "0.1.0-beta.10"));
                    var host = new Window { Width = 460, Height = 540, Background = main.Background, Content = new AdvancedView(backend, null, updates: fixture) };
                    host.Show(); host.UpdateLayout(); Spin(200);
                    Capture(host, theme + "-Advanced-" + label);
                    host.Close();
                }
                var connectedBackend = new UiTestBackendClient(new(true, false, true, true, "", new("", "Country")));
                var connected = new MainWindow(connectedBackend, store);
                connected.Show(); connected.InitializeAsync().GetAwaiter().GetResult(); connected.UpdateLayout();
                Check(((Button)connected.FindName("PrimaryButton")).Content as string == "Disconnect", "connected action stays Disconnect");
                Check(!((Button)connected.FindName("RouteButton")).IsEnabled, "connected route selection stays disabled");
                Check(((TextBlock)connected.FindName("RouteChevron")).Text == "\uE72E", "connected route row shows the lock glyph");
                Check(((TextBlock)connected.FindName("RouteHelper")).Visibility == Visibility.Visible,
                    "connected route row says how to unlock it");
                Check(((TextBlock)connected.FindName("RouteServerText")).Visibility == Visibility.Collapsed,
                    "a placeholder server label is never shown as a value");
                Check(((Button)connected.FindName("RouteButton")).IsTabStop == false || !((Button)connected.FindName("RouteButton")).IsEnabled,
                    "locked route row is not a tab stop");
                Check(((TextBlock)main.FindName("RouteChevron")).Text == "\uE76C"
                    && ((TextBlock)main.FindName("RouteHelper")).Visibility == Visibility.Collapsed,
                    "idle route row keeps the chevron and no helper");
                var toolbarButton = (Button)connected.FindName("SettingsButton");
                var accountButton = (Brisa.Controls.ToolbarButton)connected.FindName("AccountButton");
                Grid Root(Button b) => (Grid)b.Template.FindName("Root", b);
                Border Layer(Button b, string layer) => (Border)b.Template.FindName(layer, b);

                toolbarButton.IsEnabled = false;
                Settle(connected);
                Check(Root(toolbarButton).Opacity < 1.0, "disabled toolbar action fades instead of filling");
                Capture(connected, theme + "-ToolbarDisabled");
                toolbarButton.IsEnabled = true;
                Settle(connected);
                Check(Root(toolbarButton).Opacity > 0.9, "enabling the action restores it");

                // O estado é a mesma propriedade que os eventos de mouse escrevem no app.
                accountButton.State = "MouseOver";
                Settle(connected);
                Check(Layer(accountButton, "Hover").Opacity > 0.5, "hover paints the toolbar action");
                Capture(connected, theme + "-ToolbarHover");

                accountButton.State = "Pressed";
                Settle(connected);
                Check(Layer(accountButton, "Press").Opacity > 0.5 && Layer(accountButton, "Hover").Opacity < 0.5,
                    "press paints the toolbar action");

                Capture(connected, theme + "-ToolbarPressed");
                accountButton.State = "Normal";
                Settle(connected);
                Check(Layer(accountButton, "Hover").Opacity < 0.5 && Layer(accountButton, "Press").Opacity < 0.5,
                    "leaving the action clears the hover and press paint");

                connected.Activate();
                accountButton.Focus();
                System.Windows.Input.Keyboard.Focus(accountButton);
                Settle(connected);
                Check(accountButton.IsKeyboardFocused, "toolbar action takes keyboard focus");
                Capture(connected, theme + "-ToolbarFocus");
                connected.Width = connected.MinWidth; connected.Height = connected.MinHeight;
                ((Button)connected.FindName("AccountButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var signedInAccount = (AccountView)((ContentControl)connected.FindName("PageHost")).Content;
                connected.UpdateLayout();
                Check(signedInAccount.ActualWidth > 0 && signedInAccount.ActualHeight > 0,
                    "signed-in account view loads at minimum window size");
                ((Button)connected.FindName("BackButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Check(((ContentControl)connected.FindName("PageHost")).Content is null,
                    "signed-in account returns to Connection");
                // O painel de detalhes existe só conectado: com o bloco centralizado no lugar, os
                // três rótulos de métrica e o protocolo ficam escondidos.
                var detailsHost = (StackPanel)connected.FindName("DetailsHost");
                var statusBlock = (StackPanel)connected.FindName("StatusBlock");
                Check(detailsHost.Visibility == Visibility.Visible, "a connected window shows the details panel");
                Check(statusBlock.Visibility == Visibility.Collapsed, "the centred status block steps aside while the panel shows");
                Check(Descendants(detailsHost).OfType<TextBlock>().Any(t => t.Text == "WireGuard (UDP)"), "the protocol cell names WireGuard over UDP");
                Check(Descendants(detailsHost).OfType<TextBlock>().Any(t => t.Text == "Total traffic"), "the panel carries a total traffic cell");
                Check(Descendants(detailsHost).OfType<Grid>().Any(g => System.Windows.Automation.AutomationProperties.GetName(g) == "Discord traffic chart"),
                    "the chart is one named object for a screen reader");
                var rateGlyphs = Descendants(detailsHost).OfType<TextBlock>()
                    .Where(t => System.Windows.Automation.AutomationProperties.GetName(t) is "Download" or "Upload").ToList();
                Check(rateGlyphs.Count == 2, "each rate carries a direction glyph with an accessible name");
                Capture(connected, theme + "-Connected");
                connected.RequestExit();
                main.RequestExit();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Console.WriteLine("PASS loaded Fluent controls: " + theme);
            }
            Console.WriteLine($"{assertions} loaded-window assertions passed");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { app.Shutdown(); if (Directory.Exists(root)) Directory.Delete(root, true); }

        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); assertions++; }
    }

    // Storyboards de duração zero só pintam no próximo ciclo de render: sem esse flush, a
    // verificação lê a opacidade anterior e o teste mente.
    // Deixa a animação de transição terminar: sem isso a captura pega a página em fade.
    private static void Spin(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            System.Threading.Thread.Sleep(15);
        }
    }

    private sealed class FixtureUpdates : IUpdateService
    {
        public FixtureUpdates(UpdateStatus status) => Status = status;
        public UpdateStatus Status { get; private set; }
        public bool HasPendingUpdate => Status.Phase == UpdatePhase.Ready;
        public event EventHandler<UpdateStatus>? StatusChanged;
        public void Start() { }
        public Task CheckNowAsync(CancellationToken cancellationToken = default) { StatusChanged?.Invoke(this, Status); return Task.CompletedTask; }
        public void PrepareApply(bool restart) { }
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static void Settle(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
    }

    private static void Capture(Window window, string name)
    {
        var output = Environment.GetEnvironmentVariable("BRISA_APPEARANCE_OUTPUT");
        if (string.IsNullOrEmpty(output)) return;
        Directory.CreateDirectory(output);
        // Render the whole window so root-grid margins cannot crop the right
        // and bottom edges. Transparent non-client padding can be cropped later.
        var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(output, name + ".png")); png.Save(stream);
    }

    private static string TemplateShape(ControlTemplate template)
    {
        // Compare the built-in visual structure and named parts, not just the
        // target control type: legacy Aero templates cannot satisfy this check.
        var root = (DependencyObject)template.LoadContent();
        return string.Join("|", new[] { root }.Concat(Descendants(root)).Select(node => node.GetType().FullName + ":" + (node as FrameworkElement)?.Name));
    }

    private static ControlTemplate Template(Application app, string key)
    {
        for (var style = (Style)app.FindResource(key); style is not null; style = style.BasedOn)
            foreach (var setter in style.Setters.OfType<Setter>())
                if (setter.Property == Control.TemplateProperty) return (ControlTemplate)setter.Value;
        throw new InvalidOperationException("No Fluent template: " + key);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}

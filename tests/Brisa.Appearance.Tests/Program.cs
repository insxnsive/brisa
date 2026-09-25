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
                    var inline = pageName is "Settings" or "Account";
                    Window window;
                    FrameworkElement view;
                    if (inline)
                    {
                        ((Button)main.FindName(pageName + "Button")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        window = main;
                        view = (FrameworkElement)((ContentControl)main.FindName("PageHost")).Content;
                    }
                    else
                    {
                        window = pageName == "Route" ? new RouteWindow(main) : new AdvancedWindow(backend, null);
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
                        Check(view.FindName("UpdatesExpander") is Expander { IsExpanded: false }, "Updates and legal detail start collapsed");
                        ((CheckBox)view.FindName("StartupBox")).IsChecked = true;
                        Check(view.FindName("TrayBox") is null, "close-to-tray is unconditional, not an opt-in setting");
                        window.UpdateLayout();
                    }
                    foreach (var button in Descendants(view).OfType<Button>().Where(b => b.IsVisible && b.Content is string))
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
                    Capture(window, theme + "-" + view.GetType().Name);
                    if (inline)
                    {
                        ((Button)main.FindName("BackButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                        Check(((ContentControl)main.FindName("PageHost")).Content is null, "inline Back restores Home");
                    }
                    else window.Close();
                }
                var connectedBackend = new UiTestBackendClient(new(true, false, true, true, "", new("Selected server", "Country")));
                var connected = new MainWindow(connectedBackend, store);
                connected.Show(); connected.InitializeAsync().GetAwaiter().GetResult(); connected.UpdateLayout();
                Check(((Button)connected.FindName("PrimaryButton")).Content as string == "Disconnect", "connected action stays Disconnect");
                Check(!((Button)connected.FindName("RouteButton")).IsEnabled, "connected route selection stays disabled");
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

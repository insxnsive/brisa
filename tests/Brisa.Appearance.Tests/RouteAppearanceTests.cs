using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Brisa;
using Brisa.Localization;
using Brisa.Models;
using Brisa.Services;

/// <summary>
/// The route page inside the real window at 440 by 520, with and without the current route caption,
/// in both themes and both languages: nothing overlaps, every country fits, and the ping slot reads.
/// </summary>
internal static class RouteAppearanceTests
{
    public static void Run(SettingsStore store, Action<bool, string> check, Action<Window, string> capture, Action<int> spin)
    {
        var loc = LocalizationManager.Current;
        var savedLanguage = loc.Preference;
        var savedTheme = store.Current.Theme;
        var savedPreference = store.Current.PreferredRouteCountry;
        try
        {
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            foreach (var language in new[] { LocalizationManager.English, LocalizationManager.PortugueseBrazil })
            foreach (var withCaption in new[] { false, true })
            {
                var label = theme + "-" + language + (withCaption ? "-Route-Caption" : "-Route");
                store.Save(new(theme));
                store.Save(store.Current with { PreferredRouteCountry = "JP" });
                loc.Apply(language);
                var current = withCaption ? new RouteInfo("NL#1234", "NL", 128) : null;
                var backend = new UiTestBackendClient(new(false, false, true, true, "fixture", current));
                var main = new MainWindow(backend, store);
                main.Show(); main.InitializeAsync().GetAwaiter().GetResult();
                main.Width = main.MinWidth; main.Height = main.MinHeight; main.UpdateLayout();
                var page = (ContentControl)main.FindName("PageHost");
                ((Button)main.FindName("RouteButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(() => page.Content is RouteView);
                main.UpdateLayout();
                var view = (RouteView)page.Content;
                spin(260);

                var helper = (TextBlock)view.FindName("HelperText");
                var caption = (TextBlock)view.FindName("CurrentRouteText");
                var list = (ListBox)view.FindName("CountryList");
                var use = (FrameworkElement)view.FindName("UseRouteButton");
                var listHost = (FrameworkElement)list.Parent;
                var indicator = (Border)view.FindName("SelectionIndicator");
                check(caption.IsVisible == withCaption, label + ": the caption shows only when there is a route");
                var helperBox = Box(helper, view);
                var hostBox = Box(listHost, view);
                if (withCaption)
                {
                    var captionBox = Box(caption, view);
                    check(caption.ActualHeight > 0 && caption.ActualHeight <= 40, label + ": the caption is at most two lines: " + caption.ActualHeight);
                    check(helperBox.Bottom <= captionBox.Top + 0.5 && captionBox.Bottom <= hostBox.Top + 0.5, label + ": helper, caption and list do not overlap");
                    check(captionBox.Left >= 0 && captionBox.Right <= view.ActualWidth + 0.5, label + ": the caption stays inside the page");
                    check(Contrast(Brush(view, "NativeSecondary"), Brush(view, "NativeSurface")) >= 4.5, label + ": the caption meets contrast");
                }
                check(helperBox.Bottom <= hostBox.Top + 0.5, label + ": the helper text ends above the list");
                var scroll = Descendants(list).OfType<ScrollViewer>().FirstOrDefault();
                check(scroll is null || scroll.ScrollableHeight == 0, label + ": all six countries fit without scrolling");
                var useBox = Box(use, view);
                check(useBox.Bottom <= view.ActualHeight + 0.5 && useBox.Right <= view.ActualWidth + 0.5 && hostBox.Bottom <= useBox.Top + 0.5,
                    label + ": the buttons sit below the list and inside the page");
                check(list.SelectedIndex == 3, label + ": the saved country is selected");
                check(indicator.CornerRadius == new CornerRadius(6), label + ": the selection indicator keeps the 6 px radius");
                if (list.ItemContainerGenerator.ContainerFromIndex(3) is ListBoxItem selected)
                {
                    var indicatorBox = Box(indicator, view);
                    var selectedBox = Box(selected, view);
                    check(Math.Abs(indicatorBox.Top - selectedBox.Top) <= 0.5 && Math.Abs(indicatorBox.Height - selectedBox.Height) <= 0.5,
                        label + ": the indicator box matches the selected row");
                }
                var pings = Descendants(list).OfType<TextBlock>()
                    .Where(t => t.Text.Length > 0).ToList();
                check(pings.Count >= 6, label + ": every country row shows a ping state: " + pings.Count);
                check(pings.Count > 0 && Contrast(((SolidColorBrush)pings[0].Foreground).Color, Brush(view, "NativeSurface")) >= 4.5,
                    label + ": the ping text meets contrast");
                capture(main, label);
                main.RequestExit();
                spin(200);
            }
        }
        finally
        {
            loc.Apply(savedLanguage);
            store.Save(new(savedTheme));
            store.Save(store.Current with { PreferredRouteCountry = savedPreference });
        }
    }

    private static void Pump(Func<bool> completed)
    {
        var end = DateTime.UtcNow.AddSeconds(3);
        while (!completed() && DateTime.UtcNow < end)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }
    }

    private static Rect Box(FrameworkElement element, Visual ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    private static Color Brush(FrameworkElement scope, string key) => ((SolidColorBrush)scope.FindResource(key)).Color;

    private static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            var x = value / 255.0;
            return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    private static double Contrast(Color a, Color b)
    {
        var (high, low) = (Luminance(a), Luminance(b));
        if (high < low) (high, low) = (low, high);
        return (high + 0.05) / (low + 0.05);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}

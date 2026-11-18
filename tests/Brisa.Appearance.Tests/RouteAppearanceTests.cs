using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Brisa;
using Brisa.Localization;
using Brisa.Models;
using Brisa.Services;

/// <summary>
/// The route picker at its fixed 420 by 520 size, with and without the current route caption, in both
/// themes and both languages: nothing overlaps, nothing is cut off, and every country is visible without scrolling.
/// </summary>
internal static class RouteAppearanceTests
{
    public static void Run(SettingsStore store, Action<bool, string> check, Action<Window, string> capture, Action<int> spin)
    {
        var loc = LocalizationManager.Current;
        var savedLanguage = loc.Preference;
        var savedTheme = store.Current.Theme;
        try
        {
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            foreach (var language in new[] { LocalizationManager.English, LocalizationManager.PortugueseBrazil })
            foreach (var withCaption in new[] { false, true })
            {
                var label = theme + "-" + language + (withCaption ? "-Route-Caption" : "-Route");
                store.Save(new(theme));
                loc.Apply(language);
                // The longest realistic server name, so the caption has its worst case to fit.
                var current = withCaption ? new RouteInfo("NL#1234", "NL", 128) : null;
                var window = new RouteWindow(null!, "JP", current)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = 20,
                    Top = 20,
                };
                window.Show();
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                spin(80);

                var root = (FrameworkElement)window.Content;
                var helper = (TextBlock)window.FindName("HelperText");
                var caption = (TextBlock)window.FindName("CurrentRouteText");
                var list = (ListBox)window.FindName("CountryList");
                var use = (FrameworkElement)window.FindName("UseRouteButton");
                var listHost = (FrameworkElement)list.Parent;
                check(window.Width == 420 && window.Height == 520, label + ": the window keeps its 420 by 520 size");
                check(caption.IsVisible == withCaption, label + ": the caption shows only when there is a route");
                var helperBox = Box(helper, root);
                var hostBox = Box(listHost, root);
                if (withCaption)
                {
                    var captionBox = Box(caption, root);
                    check(caption.ActualHeight > 0 && caption.ActualHeight <= 40, label + ": the caption is at most two lines: " + caption.ActualHeight);
                    check(helperBox.Bottom <= captionBox.Top + 0.5 && captionBox.Bottom <= hostBox.Top + 0.5, label + ": helper, caption and list do not overlap");
                    check(captionBox.Left >= 0 && captionBox.Right <= root.ActualWidth + 0.5, label + ": the caption stays inside the window");
                    check(Brush(window, "NativeSecondary") != Brush(window, "NativeSurface") && Contrast(Brush(window, "NativeSecondary"), Brush(window, "NativeBackground")) >= 4.5, label + ": the caption meets contrast on the window");
                }
                check(helperBox.Bottom <= hostBox.Top + 0.5, label + ": the helper text ends above the list");
                var scroll = Descendants(list).OfType<ScrollViewer>().FirstOrDefault();
                check(scroll is null || scroll.ScrollableHeight == 0, label + ": all six countries fit without scrolling");
                var useBox = Box(use, root);
                check(useBox.Bottom <= root.ActualHeight + 0.5 && useBox.Right <= root.ActualWidth + 0.5 && hostBox.Bottom <= useBox.Top + 0.5, label + ": the buttons sit below the list and inside the window");
                check(list.SelectedItem is ListBoxItem { Tag: "JP" } selected && selected.IsVisible, label + ": the saved country is selected and visible");
                capture(window, label);
                window.Close();
            }
        }
        finally
        {
            loc.Apply(savedLanguage);
            store.Save(new(savedTheme));
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

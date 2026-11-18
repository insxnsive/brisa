using System.Windows;
using System.Windows.Controls;
using Brisa.Localization;
using Brisa.Models;
namespace Brisa;
public partial class RouteWindow : Window
{
    // The picker shows the countries Proton offers, named from the same table the home card uses,
    // so a country name has one source in the product.
    private static readonly string[] Offered = ["", "CA", "DE", "JP", "NL", "US"];
    public string? SelectedCountry { get; private set; }

    /// <param name="preferred">The country saved by the last successful choice. It is preselected when it is still offered.</param>
    /// <param name="current">The route Home is showing, as a one line reminder. It is read from the snapshot, never measured again.</param>
    public RouteWindow(MainWindow _, string? preferred = null, RouteInfo? current = null)
    {
        InitializeComponent();
        Title = LocalizationManager.Current["ChooseRoute"];
        var language = LocalizationManager.Current.Language;
        var selected = 0;
        for (var i = 0; i < Offered.Length; i++)
        {
            var code = Offered[i];
            CountryList.Items.Add(new ListBoxItem
            {
                Content = code.Length == 0 ? LocalizationManager.Current["Automatic"] : CountryNames.Name(code, language),
                Tag = code
            });
            if (code.Length > 0 && preferred is { Length: > 0 } && code.Equals(preferred, StringComparison.OrdinalIgnoreCase)) selected = i;
        }
        CountryList.SelectedIndex = selected;
        if (CurrentRouteCaption(current, language) is { } caption)
        {
            CurrentRouteText.Text = caption;
            CurrentRouteText.Visibility = Visibility.Visible;
            // Room for the caption comes out of the gap above the list, so the window keeps its size.
            HelperText.Margin = new Thickness(0, 5, 0, 8);
        }
    }

    /// <summary>"Current route: Japan JP#28, 34 ms". Null when there is nothing worth saying.</summary>
    internal static string? CurrentRouteCaption(RouteInfo? current, string language)
    {
        if (current is null) return null;
        var country = string.IsNullOrWhiteSpace(current.Country) ? "" : CountryNames.Name(current.Country, language);
        var server = current.Server?.Trim() ?? "";
        if (country.Length == 0 && server.Length == 0) return null;
        var loc = LocalizationManager.Current;
        return current.PingMs is { } ping
            ? string.Format(loc["CurrentRouteWithPingFormat"], country, server, ping).Replace("  ", " ")
            : string.Format(loc["CurrentRouteFormat"], country, server).Replace("  ", " ");
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    private void Select_Click(object sender, RoutedEventArgs e) { SelectedCountry = (CountryList.SelectedItem as ListBoxItem)?.Tag?.ToString(); if (string.IsNullOrEmpty(SelectedCountry)) SelectedCountry = null; DialogResult = true; }
}

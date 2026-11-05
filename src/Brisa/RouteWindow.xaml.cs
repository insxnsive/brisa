using System.Windows;
using System.Windows.Controls;
using Brisa.Localization;
namespace Brisa;
public partial class RouteWindow : Window
{
    // The picker shows the countries Proton offers, named from the same table the home card uses,
    // so a country name has one source in the product.
    private static readonly string[] Offered = ["", "CA", "DE", "JP", "NL", "US"];
    public string? SelectedCountry { get; private set; }
    public RouteWindow(MainWindow _)
    {
        InitializeComponent();
        Title = LocalizationManager.Current["ChooseRoute"];
        var language = LocalizationManager.Current.Language;
        foreach (var code in Offered)
            CountryList.Items.Add(new ListBoxItem
            {
                Content = code.Length == 0 ? LocalizationManager.Current["Automatic"] : CountryNames.Name(code, language),
                Tag = code
            });
        CountryList.SelectedIndex = 0;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    private void Select_Click(object sender, RoutedEventArgs e) { SelectedCountry = (CountryList.SelectedItem as ListBoxItem)?.Tag?.ToString(); if (string.IsNullOrEmpty(SelectedCountry)) SelectedCountry = null; DialogResult = true; }
}

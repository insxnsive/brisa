using System.Windows;
using System.Windows.Controls;
namespace Brisa;
public partial class RouteWindow : Window
{
    public string? SelectedCountry { get; private set; }
    public RouteWindow(MainWindow _) => InitializeComponent();
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    private void Select_Click(object sender, RoutedEventArgs e) { SelectedCountry = ((ListBoxItem)CountryList.SelectedItem).Tag?.ToString(); if (string.IsNullOrEmpty(SelectedCountry)) SelectedCountry = null; DialogResult = true; }
}

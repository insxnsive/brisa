using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Brisa.Localization;
using Brisa.Models;
using Brisa.Services;

namespace Brisa;

public sealed class RouteChosenEventArgs(string? country) : EventArgs
{
    public string? Country { get; } = country;
}

public partial class RouteView : UserControl, IAsyncDisposable
{
    // The picker shows the countries Proton offers, named from the same table the home card uses,
    // so a country name has one source in the product.
    private static readonly string[] Offered = ["", "CA", "DE", "JP", "NL", "US"];
    private readonly Func<CancellationToken, Task<RoutePingResult>>? _loadPings;
    private readonly Func<bool>? _allowMotion;
    private readonly List<RouteOption> _options = [];
    private CancellationTokenSource? _pingLifetime;
    private bool _disposed;

    public event EventHandler<RouteChosenEventArgs>? Chosen;
    public event EventHandler? CancelRequested;
    public string? SelectedCountry { get; private set; }

    /// <param name="preferred">The country saved by the last successful choice. It is preselected when it is still offered.</param>
    /// <param name="current">The route Home is showing, as a one line reminder. It is read from the snapshot, never measured again.</param>
    public RouteView(string? preferred, RouteInfo? current, Func<CancellationToken, Task<RoutePingResult>>? loadPings, Func<bool>? allowMotion = null)
    {
        InitializeComponent();
        _loadPings = loadPings;
        _allowMotion = allowMotion;
        var language = LocalizationManager.Current.Language;
        var selected = 0;
        for (var i = 0; i < Offered.Length; i++)
        {
            var code = Offered[i];
            var automatic = code.Length == 0;
            var option = new RouteOption(code, automatic ? LocalizationManager.Current["Automatic"] : CountryNames.Name(code, language))
            {
                PingText = automatic ? LocalizationManager.Current["RoutePingAutomatic"] : "",
                PingVisibility = automatic ? Visibility.Visible : Visibility.Collapsed,
                DotsVisibility = automatic ? Visibility.Collapsed : Visibility.Visible,
            };
            _options.Add(option);
            if (code.Length > 0 && preferred is { Length: > 0 } && code.Equals(preferred, StringComparison.OrdinalIgnoreCase)) selected = i;
        }
        CountryList.ItemsSource = _options;
        CountryList.SelectedIndex = selected;
        if (CurrentRouteCaption(current, language) is { } caption)
        {
            CurrentRouteText.Text = caption;
            CurrentRouteText.Visibility = Visibility.Visible;
            HelperText.Margin = new Thickness(0, 0, 0, 0);
        }
        Loaded += OnLoaded;
        SizeChanged += (_, _) => UpdateSelectionIndicator(false);
        CountryList.SelectionChanged += (_, _) => UpdateSelectionIndicator(true);
        RouteTrack.MouseLeave += (_, _) => HideHover();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateSelectionIndicator(false);
        HookHover();
        if (_loadPings is null) return;
        _pingLifetime = new CancellationTokenSource();
        RoutePingResult result;
        try { result = await _loadPings(_pingLifetime.Token); }
        catch
        {
            if (_disposed) return;
            foreach (var option in _options.Where(option => option.Code.Length > 0))
                SetPing(option, LocalizationManager.Current["RoutePingNoReply"], false);
            return;
        }
        if (_disposed) return;
        foreach (var option in _options)
        {
            if (option.Code.Length == 0) continue;
            if (result.Unavailable.Contains(option.Code))
                SetPing(option, LocalizationManager.Current["RoutePingUnavailable"], false);
            else if (result.Pings.TryGetValue(option.Code, out var ms) && ms is int value)
                SetPing(option, string.Format(LocalizationManager.Current["RoutePingMeasuredFormat"], value), false);
            else
                SetPing(option, LocalizationManager.Current["RoutePingNoReply"], false);
        }
    }

    private void SetPing(RouteOption option, string text, bool measuring)
    {
        option.PingText = text;
        option.PingVisibility = measuring ? Visibility.Collapsed : Visibility.Visible;
        option.DotsVisibility = measuring ? Visibility.Visible : Visibility.Collapsed;
        // The row announces its state, not just the visible label.
        if (CountryList.ItemContainerGenerator.ContainerFromItem(option) is ListBoxItem item)
            System.Windows.Automation.AutomationProperties.SetName(item, option.Name + ", " + text);
    }

    private void HookHover()
    {
        foreach (var option in _options)
        {
            if (CountryList.ItemContainerGenerator.ContainerFromItem(option) is not ListBoxItem item) continue;
            item.MouseEnter += (_, _) => ShowHover(item);
        }
    }

    private void ShowHover(ListBoxItem item)
    {
        if (item.IsSelected) { HideHover(); return; }
        var target = item.TranslatePoint(new System.Windows.Point(0, 0), RouteTrack).Y;
        var from = HoverTranslate.Y;
        HoverIndicator.Height = item.ActualHeight;
        HoverTranslate.Y = target;
        HoverIndicator.Opacity = 1;
        if (!StatusMotion.Allowed(_allowMotion)) return;
        var slide = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(160))
        { FillBehavior = FillBehavior.Stop, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        slide.Completed += (_, _) => HoverTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        HoverTranslate.BeginAnimation(TranslateTransform.YProperty, slide);
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)) { FillBehavior = FillBehavior.Stop };
        fade.Completed += (_, _) => HoverIndicator.BeginAnimation(OpacityProperty, null);
        HoverIndicator.BeginAnimation(OpacityProperty, fade);
    }

    private void HideHover()
    {
        HoverIndicator.Opacity = 0;
        HoverTranslate.Y = 0;
    }

    private void UpdateSelectionIndicator(bool animate)
    {
        var selected = CountryList.SelectedItem as RouteOption;
        if (selected is null || CountryList.ItemContainerGenerator.ContainerFromItem(selected) is not ListBoxItem item)
        {
            SelectionIndicator.Opacity = 0;
            return;
        }
        CountryList.UpdateLayout();
        var fromY = SelectionTranslate.Y;
        var fromHeight = SelectionIndicator.Height;
        var targetY = item.TranslatePoint(new System.Windows.Point(0, 0), RouteTrack).Y;
        var height = item.ActualHeight;
        SelectionIndicator.Opacity = 1;
        SelectionIndicator.Height = height;
        SelectionTranslate.Y = targetY;
        if (!animate || !StatusMotion.Allowed(_allowMotion)) return;
        var duration = TimeSpan.FromMilliseconds(200);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var slide = new DoubleAnimation(fromY, targetY, duration) { FillBehavior = FillBehavior.Stop, EasingFunction = ease };
        slide.Completed += (_, _) => SelectionTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        var grow = new DoubleAnimation(fromHeight, height, duration) { FillBehavior = FillBehavior.Stop, EasingFunction = ease };
        grow.Completed += (_, _) => SelectionIndicator.BeginAnimation(HeightProperty, null);
        SelectionTranslate.BeginAnimation(TranslateTransform.YProperty, slide);
        SelectionIndicator.BeginAnimation(HeightProperty, grow);
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

    private void Cancel_Click(object sender, RoutedEventArgs e) => CancelRequested?.Invoke(this, EventArgs.Empty);

    private void UseRoute_Click(object sender, RoutedEventArgs e)
    {
        SelectedCountry = (CountryList.SelectedItem as RouteOption)?.Code is { Length: > 0 } code ? code : null;
        Chosen?.Invoke(this, new RouteChosenEventArgs(SelectedCountry));
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _pingLifetime?.Cancel();
        _pingLifetime?.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class RouteOption(string code, string name) : INotifyPropertyChanged
    {
        private string _pingText = "";
        private Visibility _pingVisibility = Visibility.Collapsed;
        private Visibility _dotsVisibility = Visibility.Collapsed;
        public string Code { get; } = code;
        public string Name { get; } = name;
        public string PingText { get => _pingText; set { _pingText = value; PropertyChanged?.Invoke(this, new(nameof(PingText))); } }
        public Visibility PingVisibility { get => _pingVisibility; set { _pingVisibility = value; PropertyChanged?.Invoke(this, new(nameof(PingVisibility))); } }
        public Visibility DotsVisibility { get => _dotsVisibility; set { _dotsVisibility = value; PropertyChanged?.Invoke(this, new(nameof(DotsVisibility))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}

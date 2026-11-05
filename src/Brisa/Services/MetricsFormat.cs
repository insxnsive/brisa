using System.Globalization;

namespace Brisa.Services;

/// <summary>
/// Uma regra de número para o painel inteiro: três algarismos significativos, unidades decimais
/// (1000) e duração que vira h:mm:ss na primeira hora cheia. A formatação é invariante porque a
/// interface é em inglês: sem isso, a cultura pt-BR da máquina escrevia "26,1 MB" na tela.
/// </summary>
public static class MetricsFormat
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>Piso da escala do gráfico, para tráfego parado não virar ruído de altura cheia.</summary>
    public const double MinimumChartScale = 64_000;

    /// <summary>Folga acima do pico, para o topo da linha não encostar no rótulo.</summary>
    public const double ChartHeadroom = 1.15;

    public static string Size(double bytes) => Scaled(bytes);

    public static string Rate(double bytesPerSecond) => Scaled(bytesPerSecond) + "/s";

    public static string Duration(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        // Uma hora troca para h:mm:ss: "65:00" pareceria um minuto com dois dígitos.
        return elapsed.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{elapsed.Minutes}:{elapsed.Seconds:00}");
    }

    private static string Scaled(double value)
    {
        if (double.IsNaN(value) || value < 0) value = 0;
        var unit = 0;
        while (value >= 999.5 && unit < Units.Length - 1) { value /= 1000; unit++; }
        if (unit == 0) return string.Create(NumberCulture, $"{(long)Math.Round(value)} {Units[0]}");
        // Três algarismos significativos: 430, 92.1, 9.07.
        var text = value >= 100 ? value.ToString("0", NumberCulture)
            : value >= 10 ? value.ToString("0.0", NumberCulture)
            : value.ToString("0.00", NumberCulture);
        return $"{text} {Units[unit]}";
    }

    /// <summary>
    /// Vírgula decimal em pt-BR e ponto em inglês: a formatação segue o idioma escolhido no aplicativo,
    /// não a cultura do Windows. Durações continuam invariantes para não mudar de forma em logs.
    /// </summary>
    private static CultureInfo NumberCulture => CultureInfo.GetCultureInfo(Localization.LocalizationManager.Current.Language);
}

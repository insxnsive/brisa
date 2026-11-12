using System;
using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace Brisa.Localization;

/// <summary>
/// Fonte única das strings de interface. XAML liga cada texto ao índice por chave, então trocar o
/// idioma no Save repinta a janela que já existe: nenhuma janela é reconstruída e nada de estado se
/// perde. Um ResourceDictionary trocado em tempo de execução não faria isso, porque o XAML já foi
/// carregado.
/// </summary>
public sealed class LocalizationManager : INotifyPropertyChanged
{
    public const string System = "system";
    public const string English = "en";
    public const string PortugueseBrazil = "pt-BR";

    private static readonly Lazy<LocalizationManager> Shared = new(() => new LocalizationManager());
    private static readonly ResourceManager Resources =
        new("Brisa.Localization.Strings", typeof(LocalizationManager).Assembly);

    private LocalizationManager() => Preference = System;

    public static LocalizationManager Current => Shared.Value;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Preferência salva: system, en ou pt-BR.</summary>
    public string Preference { get; private set; }

    /// <summary>Idioma em uso agora, já resolvido a partir da preferência.</summary>
    public string Language { get; private set; } = English;

    /// <summary>Índice usado pelo XAML: {loc:Loc HomeTitle} vira este indexador por chave.</summary>
    public string this[string key] => Get(key);

    public string Get(string key)
    {
        var value = Resources.GetString(key, CultureInfo.GetCultureInfo(Language));
        return string.IsNullOrEmpty(value) ? key : value;
    }

    public static string Resolve(string preference) => preference switch
    {
        PortugueseBrazil => PortugueseBrazil,
        English => English,
        _ => SystemLanguage(),
    };

    /// <summary>
    /// Idioma do Windows, reduzido aos que o aplicativo fala. Lido uma única vez, na inicialização
    /// do tipo: depois do primeiro Apply a cultura do processo passa a ser a escolhida pelo usuário,
    /// e ler CurrentUICulture devolveria a escolha em vez do idioma do sistema.
    /// </summary>
    private static readonly string OperatingSystemLanguage = Detect(CultureInfo.CurrentUICulture);

    public static string SystemLanguage() => OperatingSystemLanguage;

    private static string Detect(CultureInfo ui) =>
        // Brisa opens in English everywhere except Brazil and Portugal. Reading the exact culture
        // name keeps pt-AO, pt-MZ and any other Portuguese locale on the English page, which is
        // what the product promises; the setting and Settings still let anyone switch by hand.
        ui.Name.Equals(PortugueseBrazil, StringComparison.OrdinalIgnoreCase) ||
        ui.Name.Equals("pt-PT", StringComparison.OrdinalIgnoreCase) ? PortugueseBrazil : English;

    /// <summary>Aplica a preferência. Devolve true quando o idioma em uso mudou.</summary>
    public bool Apply(string preference)
    {
        var next = Resolve(preference);
        var changed = !string.Equals(next, Language, StringComparison.Ordinal) || !string.Equals(preference, Preference, StringComparison.Ordinal);
        Preference = preference;
        Language = next;
        ApplyCulture(next);
        if (changed) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        return changed;
    }

    /// <summary>
    /// Cultura de formatação acompanha o idioma escolhido, não a do Windows: em pt-BR os números
    /// usam vírgula decimal mesmo numa máquina em inglês. A cultura de interface também acompanha,
    /// para os textos que o próprio .NET gera.
    /// </summary>
    public void ApplyCulture(string language)
    {
        var culture = CultureInfo.GetCultureInfo(language);
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
    }
}

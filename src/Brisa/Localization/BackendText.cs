using System.Windows;

namespace Brisa.Localization;

/// <summary>
/// Texto que vem do backend. O Node devolve um código e uma frase em inglês; a interface mostra a
/// frase traduzida quando conhece o código, e a inglesa quando não conhece. Assim nenhuma versão
/// futura do backend produz uma caixa vazia, e a tradução nunca fica presa a uma frase antiga.
/// </summary>
public static class BackendText
{
    private static readonly System.Text.RegularExpressions.Regex SafeCode = new("^[A-Z][A-Z0-9_]{2,47}\\z");
    public static string For(string? code, string? englishFallback, string? unknownFallbackKey = null)
    {
        if (!string.IsNullOrWhiteSpace(code))
        {
            var translated = LocalizationManager.Current[$"Backend.{code}"];
            if (!string.Equals(translated, $"Backend.{code}", System.StringComparison.Ordinal))
                return translated;
        }
        // A frase do backend só serve quando a interface está em inglês. Em português ela chegaria
        // sem tradução, então um código desconhecido cai numa mensagem do próprio produto.
        if (LocalizationManager.Current.Language == LocalizationManager.English
            && !string.IsNullOrWhiteSpace(englishFallback))
            return englishFallback;
        var generic = LocalizationManager.Current[unknownFallbackKey ?? "BackendUnknown"];
        var shown = !string.IsNullOrWhiteSpace(code) && SafeCode.IsMatch(code) ? code : "NO_CODE";
        return $"{generic} ({shown})";
    }

    /// <summary>Mostra a falha do backend já traduzida, com o código como último recurso.</summary>
    public static MessageBoxResult Show(Window owner, string? code, string? englishFallback, string caption)
        => MessageBox.Show(owner, For(code, englishFallback), caption, MessageBoxButton.OK, MessageBoxImage.Warning);
}

using System.Windows;

namespace Brisa.Localization;

/// <summary>
/// Texto que vem do backend. O Node devolve um código e uma frase em inglês; a interface mostra a
/// frase traduzida quando conhece o código, e a inglesa quando não conhece. Assim nenhuma versão
/// futura do backend produz uma caixa vazia, e a tradução nunca fica presa a uma frase antiga.
/// </summary>
public static class BackendText
{
    public static string For(string? code, string? englishFallback, string? unknownFallbackKey = null)
    {
        if (!string.IsNullOrWhiteSpace(code))
        {
            var translated = LocalizationManager.Current[$"Backend.{code}"];
            if (!string.Equals(translated, $"Backend.{code}", System.StringComparison.Ordinal))
                return translated;
        }
        if (!string.IsNullOrWhiteSpace(englishFallback))
            return englishFallback;
        return unknownFallbackKey is null
            ? LocalizationManager.Current["BackendUnknown"]
            : LocalizationManager.Current[unknownFallbackKey];
    }

    /// <summary>Mostra a falha do backend já traduzida, com o código como último recurso.</summary>
    public static MessageBoxResult Show(Window owner, string? code, string? englishFallback, string caption)
        => MessageBox.Show(owner, For(code, englishFallback), caption, MessageBoxButton.OK, MessageBoxImage.Warning);
}

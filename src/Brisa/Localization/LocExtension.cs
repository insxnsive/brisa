using System;
using System.Windows.Markup;

namespace Brisa.Localization;

/// <summary>
/// Liga uma propriedade do XAML a uma chave de texto. A ligação aponta para o indexador do
/// LocalizationManager, então quando o idioma muda o aviso "Item[]" repinta todas elas de uma vez.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension() { }

    public LocExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => new System.Windows.Data.Binding($"[{Key}]")
    {
        Source = LocalizationManager.Current,
        Mode = System.Windows.Data.BindingMode.OneWay,
    }.ProvideValue(serviceProvider);
}

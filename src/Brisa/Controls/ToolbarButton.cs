using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Brisa.Controls;

/// <summary>
/// Botão de ícone da barra superior.
///
/// O ButtonBase do WPF não aciona os estados visuais de um template próprio, e storyboards do
/// VisualStateManager só pintam no ciclo de render seguinte, o que deixa a leitura do estado
/// instável em teste. Por isso o estado é uma propriedade do botão, escrita pelos eventos reais
/// do mouse e de habilitação: o template pinta por gatilho (síncrono) e o teste escreve a mesma
/// propriedade que o app escreve, sem simular mouse.
/// </summary>
public sealed class ToolbarButton : Button
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(string), typeof(ToolbarButton), new PropertyMetadata("Normal"));

    public string State
    {
        get => (string)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public ToolbarButton() => IsEnabledChanged += (_, _) => Apply();

    protected override void OnMouseEnter(System.Windows.Input.MouseEventArgs e) { base.OnMouseEnter(e); Apply(); }
    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e) { base.OnMouseLeave(e); Apply(); }
    protected override void OnIsPressedChanged(DependencyPropertyChangedEventArgs e) { base.OnIsPressedChanged(e); Apply(); }

    private void Apply() => State = !IsEnabled ? "Disabled"
        : IsPressed ? "Pressed"
        : IsMouseOver ? "MouseOver"
        : "Normal";
}

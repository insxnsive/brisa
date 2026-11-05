using System.Windows;
using System.Windows.Media.Animation;

namespace Brisa.Services;

/// <summary>
/// Movimento de estado: só onde algo muda, curto o bastante para não atrasar a leitura, e sempre
/// obedecendo "Mostrar animações" do Windows. A política pode ser injetada pelo teste isolado.
/// </summary>
public static class StatusMotion
{
    private static readonly Duration StateFade = new(TimeSpan.FromMilliseconds(170));
    private static readonly Duration ExitFade = new(TimeSpan.FromMilliseconds(140));

    public static bool Allowed(Func<bool>? allowMotion) => allowMotion?.Invoke() ?? SystemParameters.ClientAreaAnimation;

    /// <summary>Troca de estado: o texto antigo sai enquanto o novo entra, no mesmo lugar.</summary>
    public static void CrossFade(FrameworkElement incoming, FrameworkElement outgoing, Func<bool>? allowMotion = null)
    {
        if (!Allowed(allowMotion))
        {
            outgoing.Visibility = Visibility.Collapsed;
            incoming.Opacity = 1;
            return;
        }
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fadeIn = new DoubleAnimation(0, 1, StateFade) { EasingFunction = easing, FillBehavior = FillBehavior.Stop };
        fadeIn.Completed += (_, _) => incoming.Opacity = 1;
        var fadeOut = new DoubleAnimation(1, 0, StateFade) { EasingFunction = easing, FillBehavior = FillBehavior.Stop };
        fadeOut.Completed += (_, _) => { outgoing.Visibility = Visibility.Collapsed; outgoing.Opacity = 1; };
        incoming.Opacity = 0;
        incoming.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        outgoing.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    /// <summary>Um selo novo aparece sem piscar.</summary>
    public static void FadeIn(FrameworkElement element, Func<bool>? allowMotion = null)
    {
        if (!Allowed(allowMotion)) { element.Opacity = 1; return; }
        var fade = new DoubleAnimation(0, 1, StateFade) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
        fade.Completed += (_, _) => element.Opacity = 1;
        element.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>Some com o elemento e só então recolhe o espaço dele.</summary>
    public static void FadeOut(FrameworkElement element, Action after, Func<bool>? allowMotion = null)
    {
        if (!Allowed(allowMotion)) { after(); element.Opacity = 1; return; }
        var fade = new DoubleAnimation(1, 0, ExitFade) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }, FillBehavior = FillBehavior.Stop };
        fade.Completed += (_, _) => { after(); element.Opacity = 1; };
        element.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    public static void Stop(FrameworkElement element)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 1;
    }
}

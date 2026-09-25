using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Brisa.Services;

/// <summary>Finite entrance motion for inline pages; the policy can be supplied by an isolated UI test.</summary>
public static class PageTransition
{
    private static readonly Duration EntranceDuration = new(TimeSpan.FromMilliseconds(170));

    public static void Show(FrameworkElement element, Func<bool>? allowMotion = null)
    {
        Stop(element);
        if (!(allowMotion?.Invoke() ?? SystemParameters.ClientAreaAnimation)) return;

        var offset = new TranslateTransform(8, 0);
        element.RenderTransform = offset;
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation(0, 1, EntranceDuration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop };
        var slide = new DoubleAnimation(8, 0, EntranceDuration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop };
        fade.Completed += (_, _) =>
        {
            if (ReferenceEquals(element.RenderTransform, offset)) Stop(element);
        };
        element.BeginAnimation(UIElement.OpacityProperty, fade);
        offset.BeginAnimation(TranslateTransform.XProperty, slide);
    }

    public static void Stop(FrameworkElement element)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        if (element.RenderTransform is TranslateTransform offset)
            offset.BeginAnimation(TranslateTransform.XProperty, null);
        element.Opacity = 1;
        element.RenderTransform = Transform.Identity;
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Brisa.Services;

// Animated check: the template triggers hold the resting state, so a disabled-motion run is
// simply the static check. Every animation stops filling and detaches its clock on completion.
internal static class CheckMotion
{
    public static void Attach(System.Windows.Controls.CheckBox box, Func<bool>? allowMotion)
    {
        box.Checked += (_, _) => Play(box, allowMotion, true);
        box.Unchecked += (_, _) => Play(box, allowMotion, false);
    }

    private static void Play(System.Windows.Controls.CheckBox box, Func<bool>? allowMotion, bool on)
    {
        if (!StatusMotion.Allowed(allowMotion)) return;
        box.ApplyTemplate();
        if (box.Template.FindName("Glyph", box) is not FrameworkElement glyph) return;
        if (glyph.RenderTransform is not ScaleTransform scale) return;
        // Resting values first: the fill animation is only the visible transition.
        glyph.Opacity = on ? 1 : 0;
        scale.ScaleX = scale.ScaleY = on ? 1 : 1;
        var duration = TimeSpan.FromMilliseconds(on ? 160 : 120);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var opacity = new DoubleAnimation(on ? 0 : 1, on ? 1 : 0, duration)
        { FillBehavior = FillBehavior.Stop, EasingFunction = ease };
        var scaleX = new DoubleAnimation(on ? 0.6 : 1, on ? 1 : 0.6, duration)
        { FillBehavior = FillBehavior.Stop, EasingFunction = ease };
        opacity.Completed += (_, _) => glyph.BeginAnimation(UIElement.OpacityProperty, null);
        scaleX.Completed += (_, _) =>
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        };
        glyph.BeginAnimation(UIElement.OpacityProperty, opacity);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleX);
    }
}

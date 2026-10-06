using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Martlet.Desktop;

/// <summary>Small animation helpers. Looping motion only runs when Windows "Show animations" is on.</summary>
internal static class Motion
{
    /// <summary>False when Windows "Show animations" is off or MARTLET_REDUCE_MOTION=1; motion then completes instantly.</summary>
    internal static bool Enabled => SystemParameters.ClientAreaAnimation &&
        Environment.GetEnvironmentVariable("MARTLET_REDUCE_MOTION") != "1";

    private static readonly IEasingFunction EaseOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction Sine = Freeze(new SineEase { EasingMode = EasingMode.EaseInOut });

    private static IEasingFunction Freeze(EasingFunctionBase ease)
    {
        ease.Freeze();
        return ease;
    }

    private static TranslateTransform Translate(UIElement element)
    {
        if (element.RenderTransform is TranslateTransform existing && !existing.IsFrozen) return existing;
        var transform = new TranslateTransform();
        element.RenderTransform = transform;
        return transform;
    }

    private static ScaleTransform Scale(UIElement element)
    {
        if (element.RenderTransform is ScaleTransform existing && !existing.IsFrozen) return existing;
        var transform = new ScaleTransform(1, 1);
        element.RenderTransform = transform;
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        return transform;
    }

    private static RotateTransform Rotate(UIElement element)
    {
        if (element.RenderTransform is RotateTransform existing && !existing.IsFrozen) return existing;
        var transform = new RotateTransform();
        element.RenderTransform = transform;
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        return transform;
    }

    /// <summary>Fades an element in while it slides from an offset to rest.</summary>
    internal static void Enter(UIElement element, double dx = 0, double dy = 14, int milliseconds = 260, int delay = 0)
    {
        var move = Translate(element);
        if (!Enabled)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
            move.BeginAnimation(TranslateTransform.XProperty, null);
            move.BeginAnimation(TranslateTransform.YProperty, null);
            move.X = move.Y = 0;
            return;
        }
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        var begin = TimeSpan.FromMilliseconds(delay);
        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, duration) { BeginTime = begin, EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop });
        move.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(dx, 0, duration) { BeginTime = begin, EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop });
        move.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(dy, 0, duration) { BeginTime = begin, EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop });
    }

    /// <summary>Staggers <see cref="Enter"/> over a set of children.</summary>
    internal static void Cascade(IEnumerable<UIElement> elements, int step = 45)
    {
        var index = 0;
        foreach (var element in elements) Enter(element, delay: index++ * step);
    }

    /// <summary>Scales an element in with a small overshoot, for completion marks.</summary>
    internal static void Pop(UIElement element, int delay = 0)
    {
        var scale = Scale(element);
        if (!Enabled) { scale.ScaleX = scale.ScaleY = 1; return; }
        var animation = new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(320))
        {
            BeginTime = TimeSpan.FromMilliseconds(delay),
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 },
            FillBehavior = FillBehavior.Stop
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    /// <summary>Gentle vertical bobbing, forever.</summary>
    internal static void Float(UIElement element, double amplitude = 5, double seconds = 3.2)
    {
        var move = Translate(element);
        if (!Enabled) { move.BeginAnimation(TranslateTransform.YProperty, null); move.Y = 0; return; }
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-amplitude, amplitude, TimeSpan.FromSeconds(seconds / 2))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Sine
        });
    }

    /// <summary>Opacity twinkle, forever.</summary>
    internal static void Twinkle(UIElement element, double seconds = 1.6, double delay = 0)
    {
        if (!Enabled) { element.BeginAnimation(UIElement.OpacityProperty, null); element.Opacity = 1; return; }
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0.35, TimeSpan.FromSeconds(seconds / 2))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Sine,
            BeginTime = TimeSpan.FromSeconds(delay)
        });
    }

    /// <summary>One quick swell-and-fade back to rest, like a camera shutter, for a status dot.</summary>
    internal static void Blink(UIElement element)
    {
        if (!Enabled) return;
        var scale = Scale(element);
        var duration = TimeSpan.FromMilliseconds(650);
        var settle = new DoubleAnimation(1.8, 1, duration) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, settle);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, settle);
        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0.3, 1, duration) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop });
    }

    /// <summary>A gentle side-to-side tilt, forever, for the mascot.</summary>
    internal static void Sway(UIElement element, double degrees = 4, double seconds = 3.6)
    {
        var rotate = Rotate(element);
        if (!Enabled) { rotate.BeginAnimation(RotateTransform.AngleProperty, null); rotate.Angle = 0; return; }
        rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(-degrees, degrees, TimeSpan.FromSeconds(seconds / 2))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Sine
        });
    }

    /// <summary>A steady spin, forever, for a busy indicator; <paramref name="on"/> false stops it. Static when motion is off.</summary>
    internal static void Spin(UIElement element, bool on, double seconds = 1)
    {
        var rotate = Rotate(element);
        if (!on || !Enabled) { rotate.BeginAnimation(RotateTransform.AngleProperty, null); rotate.Angle = 0; return; }
        if (rotate.HasAnimatedProperties) return;
        rotate.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(seconds)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    /// <summary>An expanding, fading ring, forever. The element should be a ring drawn behind a node.</summary>
    internal static void PulseRing(UIElement element, double delay = 0, double to = 1.9)
    {
        var scale = Scale(element);
        if (!Enabled) { element.Opacity = 0; return; }
        var duration = TimeSpan.FromSeconds(2);
        var begin = TimeSpan.FromSeconds(delay);
        var grow = new DoubleAnimation(1, to, duration) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = EaseOut, BeginTime = begin };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0.6, 0, duration) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = EaseOut, BeginTime = begin });
    }

    /// <summary>A slow swell and settle, forever, for soft glows.</summary>
    internal static void Breathe(UIElement element, double seconds = 4)
    {
        var scale = Scale(element);
        if (!Enabled) return;
        var swell = new DoubleAnimation(0.94, 1.06, TimeSpan.FromSeconds(seconds / 2))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Sine
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, swell);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, swell);
    }

    /// <summary>Animates a horizontal scale to a new value, for progress fills.</summary>
    internal static void ScaleX(ScaleTransform scale, double to)
    {
        if (scale.IsFrozen) return;
        if (!Enabled) { scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); scale.ScaleX = to; return; }
        var from = scale.ScaleX;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.ScaleX = to;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(700)) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop });
    }

    /// <summary>Marching dashes along a connection, forever.</summary>
    internal static void Flow(Shape line, bool reverse = false)
    {
        if (!Enabled) return;
        var span = line.StrokeDashArray.Sum() * 2;
        line.BeginAnimation(Shape.StrokeDashOffsetProperty,
            new DoubleAnimation(reverse ? 0 : span, reverse ? span : 0, TimeSpan.FromSeconds(1.2)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    /// <summary>Animates a width change, for progress bars.</summary>
    internal static void Width(FrameworkElement element, double to)
    {
        if (!Enabled || double.IsNaN(element.Width)) { element.BeginAnimation(FrameworkElement.WidthProperty, null); element.Width = to; return; }
        var from = element.ActualWidth;
        element.BeginAnimation(FrameworkElement.WidthProperty, null);
        element.Width = to;
        element.BeginAnimation(FrameworkElement.WidthProperty,
            new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(600)) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop });
    }

    /// <summary>Fades an element out, then runs <paramref name="completed"/>.</summary>
    internal static void FadeOut(UIElement element, Action completed, int milliseconds = 220)
    {
        if (!Enabled) { completed(); return; }
        var fade = new DoubleAnimation(element.Opacity, 0, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop };
        fade.Completed += (_, _) => completed();
        element.BeginAnimation(UIElement.OpacityProperty, fade);
    }
}

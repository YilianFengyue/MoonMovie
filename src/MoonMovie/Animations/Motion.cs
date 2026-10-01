using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;

namespace MoonMovie.Animations;

/// <summary>
/// Storyboard helpers for elements that contain (or are) interactive controls.
/// </summary>
/// <remarks>
/// Never use XAML implicit transitions (OpacityTransition, BrushTransition…) in this app, and never drive an
/// element that hosts controls through ElementCompositionPreview: combining the two crashes Microsoft.UI.Xaml
/// inside pointer-input handling (0xC0000005 at Microsoft.UI.Xaml.dll+0x94C26F in WinAppSDK 2.5).
/// Composition animations are fine on purely visual elements (images, backdrops, glyphs).
/// </remarks>
public static class Motion
{
    private static readonly EasingFunctionBase Decelerate = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 };
    private static readonly EasingFunctionBase Accelerate = new CubicEase { EasingMode = EasingMode.EaseIn };

    /// <summary>Slide (via a TranslateTransform) and fade together; returns when finished.</summary>
    public static Task SlideFadeAsync(UIElement element, double fromX, double toX, double fromY, double toY,
        double? fromOpacity, double toOpacity, TimeSpan duration, bool decelerate = true)
    {
        if (element.RenderTransform is not Microsoft.UI.Xaml.Media.TranslateTransform translate)
        {
            translate = new Microsoft.UI.Xaml.Media.TranslateTransform();
            element.RenderTransform = translate;
        }

        var easing = decelerate ? Decelerate : Accelerate;
        var storyboard = new Storyboard();
        Add(storyboard, translate, "X", fromX, toX, duration, easing);
        Add(storyboard, translate, "Y", fromY, toY, duration, easing);
        Add(storyboard, element, nameof(UIElement.Opacity), fromOpacity, toOpacity, duration, easing);

        var tcs = new TaskCompletionSource();
        storyboard.Completed += (_, _) => tcs.TrySetResult();
        storyboard.Begin();
        return tcs.Task;
    }

    private static void Add(Storyboard storyboard, DependencyObject target, string property, double? from, double to,
        TimeSpan duration, EasingFunctionBase easing)
    {
        var animation = new DoubleAnimation { To = to, Duration = new Duration(duration), EasingFunction = easing };
        if (from is { } f) animation.From = f;
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }

    public static void FadeTo(UIElement element, double opacity, TimeSpan duration)
    {
        var animation = new DoubleAnimation
        {
            To = opacity,
            Duration = new Duration(duration),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, nameof(UIElement.Opacity));
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }
}

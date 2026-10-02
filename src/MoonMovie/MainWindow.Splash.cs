using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using MoonMovie.Animations;

namespace MoonMovie;

/// <summary>
/// The startup splash: the logo settles in at the centre while the first page loads (a ring joins if that
/// takes a while), then flies into the title-bar wordmark as the cover fades and the page shows through.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly TimeSpan SplashMinimum = TimeSpan.FromMilliseconds(950);
    private static readonly TimeSpan SplashMaximum = TimeSpan.FromSeconds(6);

    private DateTimeOffset _splashShownAt = DateTimeOffset.MaxValue;
    private bool _splashReleased;

    private void StartSplash()
    {
        var logo = ElementCompositionPreview.GetElementVisual(SplashLogo);
        ElementCompositionPreview.SetIsTranslationEnabled(SplashLogo, true);
        logo.CenterPoint = new Vector3(66, 66, 0);
        logo.Opacity = 0;
        logo.Scale = new Vector3(0.84f, 0.84f, 1);

        // The window's first frames come a moment after it is created: start (and time) the entrance once the
        // logo is actually on screen, or a quick first page would whisk it away before it was ever seen.
        SplashLogo.Loaded += (_, _) => DispatcherQueue.Enqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => EnterSplash(logo));
    }

    private void EnterSplash(Visual logo)
    {
        _splashShownAt = DateTimeOffset.Now;
        var compositor = logo.Compositor;
        var settle = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, 1, settle);
        fade.Duration = TimeSpan.FromMilliseconds(380);
        var grow = compositor.CreateVector3KeyFrameAnimation();
        grow.InsertKeyFrame(1, Vector3.One, settle);
        grow.Duration = TimeSpan.FromMilliseconds(760);
        logo.StartAnimation(nameof(Visual.Opacity), fade);
        logo.StartAnimation(nameof(Visual.Scale), grow);

        // Slow start: a ring says something is happening; past the limit the page shows regardless.
        var ring = DispatcherQueue.CreateTimer();
        ring.Interval = TimeSpan.FromMilliseconds(1200);
        ring.IsRepeating = false;
        ring.Tick += (_, _) => SafeDispatch.Run(() =>
        {
            if (_splashReleased) return;
            SplashRing.IsActive = true;
            Motion.FadeTo(SplashRing, 1, TimeSpan.FromMilliseconds(300));
        });
        ring.Start();

        var limit = DispatcherQueue.CreateTimer();
        limit.Interval = SplashMaximum;
        limit.IsRepeating = false;
        limit.Tick += (_, _) => SafeDispatch.Run(ReleaseSplash);
        limit.Start();
    }

    /// <summary>The first page has something to show (or is not the home page): hand over to it.</summary>
    public async void ReleaseSplash()
    {
        if (_splashReleased) return;
        _splashReleased = true;

        // Let the entrance finish so the logo never jumps away mid-animation.
        while (_splashShownAt == DateTimeOffset.MaxValue) await Task.Delay(50);
        var wait = SplashMinimum - (DateTimeOffset.Now - _splashShownAt);
        if (wait > TimeSpan.Zero) await Task.Delay(wait);

        Motion.FadeTo(SplashRing, 0, TimeSpan.FromMilliseconds(150));
        var logo = ElementCompositionPreview.GetElementVisual(SplashLogo);
        var compositor = logo.Compositor;
        var glide = compositor.CreateCubicBezierEasingFunction(new Vector2(0.55f, 0.05f), new Vector2(0.1f, 1f));
        var duration = TimeSpan.FromMilliseconds(620);

        // Into the wordmark when it is on screen (not in the player's immersive mode); otherwise a soft dissolve.
        var target = WordmarkTarget();
        var move = compositor.CreateVector3KeyFrameAnimation();
        var scale = compositor.CreateVector3KeyFrameAnimation();
        move.Duration = scale.Duration = duration;
        if (target is { } t)
        {
            move.InsertKeyFrame(1, new Vector3(t.Offset, 0), glide);
            scale.InsertKeyFrame(1, new Vector3(t.Scale, t.Scale, 1), glide);
        }
        else
        {
            move.InsertKeyFrame(1, Vector3.Zero, glide);
            scale.InsertKeyFrame(1, new Vector3(1.06f, 1.06f, 1), glide);
            var vanish = compositor.CreateScalarKeyFrameAnimation();
            vanish.InsertKeyFrame(1, 0);
            vanish.Duration = TimeSpan.FromMilliseconds(360);
            logo.StartAnimation(nameof(Visual.Opacity), vanish);
        }

        logo.StartAnimation("Translation", move);
        logo.StartAnimation(nameof(Visual.Scale), scale);
        await Task.Delay(120);
        Motion.FadeTo(SplashCover, 0, TimeSpan.FromMilliseconds(480));

        await Task.Delay(duration - TimeSpan.FromMilliseconds(140));
        if (target is not null)
        {
            // Landed: the real wordmark takes over; the name follows a beat later.
            WordmarkLogo.Opacity = 1;
            logo.Opacity = 0;
        }
        else
        {
            WordmarkLogo.Opacity = 1;
        }

        Motion.FadeTo(WordmarkText, 1, TimeSpan.FromMilliseconds(360));
        await Task.Delay(500);
        Splash.Visibility = Visibility.Collapsed;
        SplashRing.IsActive = false;
    }

    /// <summary>Translation and scale that put the splash logo exactly over the title-bar logo.</summary>
    private (Vector2 Offset, float Scale)? WordmarkTarget()
    {
        if (AppTitleBar.Visibility != Visibility.Visible || WordmarkLogo.ActualWidth <= 0 || SplashLogo.ActualWidth <= 0) return null;
        var from = SplashLogo.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(SplashLogo.ActualWidth / 2, SplashLogo.ActualHeight / 2));
        var to = WordmarkLogo.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(WordmarkLogo.ActualWidth / 2, WordmarkLogo.ActualHeight / 2));
        return (new Vector2((float)(to.X - from.X), (float)(to.Y - from.Y)), (float)(WordmarkLogo.ActualWidth / SplashLogo.ActualWidth));
    }
}

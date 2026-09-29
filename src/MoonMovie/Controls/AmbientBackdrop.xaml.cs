using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media.Animation;
using MoonMovie.Imaging;
using Windows.UI;

namespace MoonMovie.Controls;

/// <summary>
/// Full-bleed, slowly drifting artwork with a colour wash sampled from the image.
/// Call <see cref="Show"/> as often as you like; only the latest request wins.
/// </summary>
public sealed partial class AmbientBackdrop : UserControl
{
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(650);
    private static readonly TimeSpan DriftDuration = TimeSpan.FromSeconds(26);

    private readonly ImageLoader _images = App.Services.GetRequiredService<ImageLoader>();
    private Image _front;
    private Image _back;
    private string? _currentUrl;
    private int _version;

    public AmbientBackdrop()
    {
        InitializeComponent();
        _front = LayerA;
        _back = LayerB;
        foreach (var layer in new[] { LayerA, LayerB })
        {
            layer.Opacity = 1;
            ElementCompositionPreview.GetElementVisual(layer).Opacity = 0;
        }

        SizeChanged += (_, e) =>
        {
            foreach (var layer in new[] { LayerA, LayerB })
            {
                ElementCompositionPreview.GetElementVisual(layer).CenterPoint =
                    new Vector3((float)e.NewSize.Width * 0.6f, (float)e.NewSize.Height * 0.35f, 0);
            }
        };
    }

    /// <summary>The element whose visual opacity scroll-driven dimming should animate.</summary>
    public UIElement DimTarget => ArtLayer;

    public string? CurrentUrl => _currentUrl;

    /// <param name="url">Artwork to show.</param>
    /// <param name="previewUrl">
    /// A smaller rendition shown first when <paramref name="url"/> is not cached yet; the full-size image
    /// replaces it in place once decoded.
    /// </param>
    public async void Show(string? url, string? previewUrl = null)
    {
        if (string.IsNullOrEmpty(url) || url == _currentUrl)
        {
            return;
        }

        _currentUrl = url;
        var version = ++_version;
        var width = (int)Math.Max(ActualWidth, 1280);

        if (previewUrl is not null && !_images.IsCached(url))
        {
            var preview = await _images.LoadAsync(previewUrl, width, highPriority: true);
            if (version != _version)
            {
                return;
            }

            if (preview is not null)
            {
                Present(preview, version);
            }

            var full = await _images.LoadAsync(url, width, highPriority: true);
            if (version != _version || full is null)
            {
                return;
            }

            if (preview is null)
            {
                Present(full, version);
            }
            else
            {
                _front.Source = full;
            }
        }
        else
        {
            var bitmap = await _images.LoadAsync(url, width, highPriority: true);
            if (bitmap is null || version != _version)
            {
                return;
            }

            Present(bitmap, version);
        }

        var file = await _images.GetFileAsync(url);
        var tone = file is null ? null : await ColorExtractor.AmbientToneAsync(file);
        if (version == _version)
        {
            AnimateTone(tone ?? Color.FromArgb(255, 12, 13, 16));
        }
    }

    private void Present(Microsoft.UI.Xaml.Media.ImageSource bitmap, int version)
    {
        var incoming = _back;
        var outgoing = _front;
        incoming.Source = bitmap;
        Canvas.SetZIndex(incoming, 1);
        Canvas.SetZIndex(outgoing, 0);
        StartDrift(incoming);
        _front = incoming;
        _back = outgoing;

        // Fade the new layer in on top; hide the old one only once it is fully covered,
        // so the transition never dips through black.
        var incomingVisual = ElementCompositionPreview.GetElementVisual(incoming);
        var outgoingVisual = ElementCompositionPreview.GetElementVisual(outgoing);
        var compositor = incomingVisual.Compositor;
        var fadeIn = compositor.CreateScalarKeyFrameAnimation();
        fadeIn.InsertKeyFrame(0f, 0f);
        fadeIn.InsertKeyFrame(1f, 1f, compositor.CreateCubicBezierEasingFunction(new Vector2(0.25f, 0.1f), new Vector2(0.25f, 1f)));
        fadeIn.Duration = FadeDuration;

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        incomingVisual.StartAnimation("Opacity", fadeIn);
        batch.End();
        batch.Completed += (_, _) =>
        {
            if (version == _version)
            {
                outgoingVisual.Opacity = 0;
            }
        };
    }

    private static void StartDrift(Image layer)
    {
        var visual = ElementCompositionPreview.GetElementVisual(layer);
        var compositor = visual.Compositor;
        var drift = compositor.CreateVector3KeyFrameAnimation();
        drift.InsertKeyFrame(0f, new Vector3(1.0f, 1.0f, 1f));
        drift.InsertKeyFrame(1f, new Vector3(1.07f, 1.07f, 1f), compositor.CreateLinearEasingFunction());
        drift.Duration = DriftDuration;
        drift.Direction = AnimationDirection.Alternate;
        drift.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation("Scale", drift);
    }

    private void AnimateTone(Color tone)
    {
        var strong = Color.FromArgb(0xD9, tone.R, tone.G, tone.B);
        var clear = Color.FromArgb(0x00, tone.R, tone.G, tone.B);
        var storyboard = new Storyboard();
        storyboard.Children.Add(ColorTo(ToneStopStrong, strong));
        storyboard.Children.Add(ColorTo(ToneStopClear, clear));
        storyboard.Begin();
    }

    private static ColorAnimation ColorTo(DependencyObject target, Color to)
    {
        var animation = new ColorAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(900)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, "Color");
        return animation;
    }
}

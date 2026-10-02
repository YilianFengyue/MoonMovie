using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace MoonMovie.Imaging;

/// <summary>
/// <c>imaging:ImageEx.Url</c> — cached, size-aware, recycling-safe image loading with a soft fade-in.
/// </summary>
public static class ImageEx
{
    private static readonly ConditionalWeakTable<Image, CancellationTokenSource> Pending = new();
    private static ImageLoader? _loader;

    public static readonly DependencyProperty UrlProperty = DependencyProperty.RegisterAttached(
        "Url", typeof(string), typeof(ImageEx), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty DecodeWidthProperty = DependencyProperty.RegisterAttached(
        "DecodeWidth", typeof(int), typeof(ImageEx), new PropertyMetadata(0, OnChanged));

    public static string? GetUrl(Image image) => (string?)image.GetValue(UrlProperty);

    public static void SetUrl(Image image, string? value) => image.SetValue(UrlProperty, value);

    public static int GetDecodeWidth(Image image) => (int)image.GetValue(DecodeWidthProperty);

    public static void SetDecodeWidth(Image image, int value) => image.SetValue(DecodeWidthProperty, value);

    private static ImageLoader Loader => _loader ??= App.Services.GetRequiredService<ImageLoader>();

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image image)
        {
            return;
        }

        if (Pending.TryGetValue(image, out var previous))
        {
            previous.Cancel();
            Pending.Remove(image);
        }

        var url = GetUrl(image);
        if (string.IsNullOrEmpty(url))
        {
            image.Source = null;
            return;
        }

        // Both properties are usually set in the same layout pass; read them once they have settled.
        var width = GetDecodeWidth(image);
        var visual = ElementCompositionPreview.GetElementVisual(image);
        if (Loader.TryGetDecoded(url, width) is { } hit)
        {
            visual.StopAnimation("Opacity");
            visual.Opacity = 1;
            image.Source = hit;
            return;
        }

        var cts = new CancellationTokenSource();
        Pending.Add(image, cts);
        image.Source = null;
        visual.StopAnimation("Opacity");
        visual.Opacity = 0;

        image.DispatcherQueue.Enqueue(DispatcherQueuePriority.Low, () => _ = LoadAsync(image, cts.Token));
    }

    private static async Task LoadAsync(Image image, CancellationToken ct)
    {
        var url = GetUrl(image);
        if (ct.IsCancellationRequested || string.IsNullOrEmpty(url))
        {
            return;
        }

        var bitmap = await Loader.LoadAsync(url, GetDecodeWidth(image), ct);
        if (ct.IsCancellationRequested || bitmap is null)
        {
            return;
        }

        image.Source = bitmap;

        // Composition-driven fade: XAML OpacityTransition on recycled ItemsRepeater children is not safe.
        var visual = ElementCompositionPreview.GetElementVisual(image);
        var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f);
        fade.Duration = TimeSpan.FromMilliseconds(220);
        visual.StartAnimation("Opacity", fade);
    }
}

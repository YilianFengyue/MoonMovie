using CommunityToolkit.Mvvm.ComponentModel;
using MoonMovie.Core.Models;
using MoonMovie.Core.Tmdb;
using MoonMovie.Imaging;

namespace MoonMovie.ViewModels;

public sealed partial class SpotlightViewModel(MediaItem item, TmdbClient tmdb) : ObservableObject
{
    // Logos darker than this disappear on a dark backdrop; fall back to set type instead.
    private const double MinLogoLuminance = 0.32;

    private Task? _brandingTask;

    public MediaItem Item { get; } = item;

    public string Title => Item.Title;

    public string? Overview => Item.Overview;

    public string MetaLine => Item.MetaLine;

    public string? RatingText => Item.RatingText;

    public string Overline => Item.Kind == MediaKind.Movie ? "今日热门 · 电影" : "今日热门 · 剧集";

    public string? BackdropUrl => tmdb.ImageUrl(Item.BackdropPath, "original");

    /// <summary>Lightweight rendition shown while <see cref="BackdropUrl"/> downloads.</summary>
    public string? PreviewUrl => tmdb.ImageUrl(Item.BackdropPath, "w780");

    [ObservableProperty]
    public partial string? LogoUrl { get; private set; }

    /// <summary>Resolves the title logo once; safe to call repeatedly.</summary>
    public Task EnsureBrandingAsync(ImageLoader images) => _brandingTask ??= ResolveBrandingAsync(images);

    private async Task ResolveBrandingAsync(ImageLoader images)
    {
        var path = await tmdb.LogoPathAsync(Item.Kind, Item.TmdbId);
        var url = tmdb.ImageUrl(path, "w500");
        if (url is null)
        {
            return;
        }

        var file = await images.GetFileAsync(url, default, highPriority: true);
        if (file is null)
        {
            return;
        }

        var luminance = await ColorExtractor.OpaqueLuminanceAsync(file);
        if (luminance >= MinLogoLuminance)
        {
            LogoUrl = url;
        }
    }
}

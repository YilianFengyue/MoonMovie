using CommunityToolkit.Mvvm.Input;
using MoonMovie.Core.Models;
using MoonMovie.Core.Tmdb;
using MoonMovie.Services;

namespace MoonMovie.ViewModels;

public enum CardVariant
{
    Poster,
    Landscape,
}

public sealed partial class MediaCardViewModel(MediaItem item, TmdbClient tmdb, CardVariant variant = CardVariant.Poster)
{
    public MediaItem Item { get; } = item;

    public CardVariant Variant { get; } = variant;

    public string Title => Item.Title;

    /// <summary>"2024 · 电影 · 8.3"</summary>
    public string Subtitle
    {
        get
        {
            var parts = new List<string>(3);
            if (Item.Year is { } y) parts.Add(y.ToString());
            parts.Add(Item.KindLabel);
            if (Item.RatingText is { } r) parts.Add(r);
            return string.Join(" · ", parts);
        }
    }

    public string? PosterUrl => tmdb.ImageUrl(Item.PosterPath, "w342");

    public string? BackdropUrl => tmdb.ImageUrl(Item.BackdropPath ?? Item.PosterPath, "w780");

    /// <summary>Full-window backdrop used when this card drives the ambient background.</summary>
    public string? AmbientUrl => tmdb.ImageUrl(Item.BackdropPath, "w1280");

    [RelayCommand]
    private void Open() => Navigator.OpenMedia(Item);
}

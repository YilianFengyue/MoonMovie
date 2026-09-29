using MoonMovie.Core.Models;
using MoonMovie.Core.Tmdb;

namespace MoonMovie.Core.Home;

public enum HomeRowStyle
{
    Poster,
    Landscape,
}

public sealed record HomeRowDefinition(
    string Id,
    string Title,
    HomeRowStyle Style,
    Func<TmdbClient, CacheMode, CancellationToken, Task<IReadOnlyList<MediaItem>>> Load);

public sealed class HomeFeedService(TmdbClient tmdb)
{
    private const int SpotlightCount = 6;

    public IReadOnlyList<HomeRowDefinition> Rows { get; } =
    [
        new("trending", "本周热门", HomeRowStyle.Landscape,
            (t, m, ct) => t.TrendingAsync("all", "week", 1, m, ct)),
        new("popular-movie", "热门电影", HomeRowStyle.Poster,
            (t, m, ct) => t.PopularAsync(MediaKind.Movie, 1, m, ct)),
        new("popular-tv", "热门剧集", HomeRowStyle.Poster,
            (t, m, ct) => t.PopularAsync(MediaKind.Tv, 1, m, ct)),
        new("cn-tv", "华语剧集", HomeRowStyle.Poster,
            (t, m, ct) => t.DiscoverAsync(MediaKind.Tv, new Dictionary<string, string>
            {
                ["with_original_language"] = "zh",
                ["sort_by"] = "popularity.desc",
                ["vote_count.gte"] = "20",
            }, 1, m, ct)),
        new("top-movie", "影史高分", HomeRowStyle.Poster,
            (t, m, ct) => t.TopRatedAsync(MediaKind.Movie, 1, m, ct)),
        new("anime", "日本动画", HomeRowStyle.Poster,
            (t, m, ct) => t.DiscoverAsync(MediaKind.Tv, new Dictionary<string, string>
            {
                ["with_genres"] = TmdbGenres.Animation.ToString(),
                ["with_original_language"] = "ja",
                ["sort_by"] = "popularity.desc",
            }, 1, m, ct)),
        new("scifi", "科幻宇宙", HomeRowStyle.Landscape,
            (t, m, ct) => t.DiscoverAsync(MediaKind.Movie, new Dictionary<string, string>
            {
                ["with_genres"] = TmdbGenres.ScienceFiction.ToString(),
                ["sort_by"] = "popularity.desc",
                ["vote_count.gte"] = "500",
            }, 1, m, ct)),
        new("mystery", "悬疑与惊悚", HomeRowStyle.Poster,
            (t, m, ct) => t.DiscoverAsync(MediaKind.Movie, new Dictionary<string, string>
            {
                ["with_genres"] = $"{TmdbGenres.Mystery}|{TmdbGenres.Thriller}",
                ["sort_by"] = "popularity.desc",
                ["vote_count.gte"] = "300",
            }, 1, m, ct)),
        new("top-tv", "口碑剧集", HomeRowStyle.Poster,
            (t, m, ct) => t.TopRatedAsync(MediaKind.Tv, 1, m, ct)),
    ];

    /// <summary>Spotlight candidates: trending today with artwork and a synopsis.</summary>
    public async Task<IReadOnlyList<MediaItem>> SpotlightAsync(CacheMode mode, CancellationToken ct = default)
    {
        var items = await tmdb.TrendingAsync("all", "day", 1, mode, ct).ConfigureAwait(false);
        return items.Where(i => i.BackdropPath is not null && i.Overview is not null)
                    .Take(SpotlightCount)
                    .ToArray();
    }

    public async Task<IReadOnlyList<MediaItem>> LoadRowAsync(HomeRowDefinition row, CacheMode mode,
        CancellationToken ct = default)
    {
        var items = await row.Load(tmdb, mode, ct).ConfigureAwait(false);
        return items.Where(i => row.Style == HomeRowStyle.Landscape ? i.BackdropPath is not null : i.PosterPath is not null)
                    .ToArray();
    }

    /// <summary>One page of the "发现" wall: popular movies and series interleaved.</summary>
    public async Task<IReadOnlyList<MediaItem>> DiscoverPageAsync(int page, CacheMode mode,
        CancellationToken ct = default)
    {
        var filters = new Dictionary<string, string>
        {
            ["sort_by"] = "popularity.desc",
            ["vote_count.gte"] = "150",
        };

        var movies = tmdb.DiscoverAsync(MediaKind.Movie, filters, page, mode, ct);
        var shows = tmdb.DiscoverAsync(MediaKind.Tv, filters, page, mode, ct);
        await Task.WhenAll(movies, shows).ConfigureAwait(false);

        var result = new List<MediaItem>(40);
        var a = movies.Result;
        var b = shows.Result;
        for (var i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            if (i < a.Count) result.Add(a[i]);
            if (i < b.Count) result.Add(b[i]);
        }

        return result.Where(i => i.PosterPath is not null).ToArray();
    }
}

using Microsoft.Extensions.DependencyInjection;
using MoonMovie.Core.Bilibili;
using MoonMovie.Core.Models;
using MoonMovie.Core.Sources;
using MoonMovie.Core.Tmdb;
using MoonMovie.ViewModels;

namespace MoonMovie.Services;

/// <summary>
/// B站 videos play in MoonMovie's own player: the video's parts become its episode list ("bili://BV…/cid" entries,
/// resolved to DASH streams when opened), with B站 danmaku and comments instead of the LogVar track.
/// </summary>
public static class BiliPlayback
{
    public static readonly SourceSite Site = new("bilibili", "哔哩哔哩", "");

    private const string Scheme = "bili://";

    public static bool IsBili(SourceCandidate candidate) => candidate.Site.Key == Site.Key;

    public static bool IsBiliUrl(string url) => url.StartsWith(Scheme, StringComparison.Ordinal);

    public static (string Bvid, long Cid) Parse(string url)
    {
        var parts = url[Scheme.Length..].Split('/');
        return (parts[0], long.Parse(parts[1]));
    }

    public static async Task PlayAsync(BiliVideo video)
    {
        BiliVideoDetail detail;
        try
        {
            detail = await App.Services.GetRequiredService<BiliClient>().DetailAsync(video.Bvid);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or BiliException or System.Text.Json.JsonException)
        {
            // The player explains failures; a bare video still plays its first part.
            detail = new BiliVideoDetail(video, [], null, 0, 0, 0, 0);
        }

        var pages = detail.Pages;
        var episodes = pages.Count == 0
            ? []
            : pages.Select((p, i) => new PlayEpisode(i, pages.Count > 1 ? $"P{p.Number}  {p.Title}" : "正片", $"{Scheme}{video.Bvid}/{p.Cid}")).ToArray();
        if (episodes.Length == 0) return;

        var title = detail.Video.Title;
        var candidate = new SourceCandidate(Site, video.Bvid, title, detail.Video.Published.Year, null,
            pages.Count > 1 ? $"{pages.Count} P" : null, [new PlayLine("B站", episodes)], 1000);
        var panel = new SourcePanelViewModel(App.Services.GetRequiredService<SourceSearchService>());
        var source = panel.UseLocal(candidate);

        // Not a catalogue title: no TMDB id, not recorded in history; the cover stands in for artwork.
        var item = new MediaItem(0, MediaKind.Movie, title, null, detail.Video.Description, detail.Video.Published.Year, 0, 0,
            detail.Video.Cover, detail.Video.Cover, [], null) { LocalKey = "bili:" + video.Bvid };

        Navigator.OpenPlayback(new PlaybackRequest(item, panel, source, 0, null, [], Bili: detail));
    }

    /// <summary>
    /// A B站正版 season (from the 「B站」 page): the same title's detail page when TMDB knows it — 「B站正版」 then
    /// appears among its sources — or, for B站-only titles, played straight from B站's episode list.
    /// </summary>
    public static async Task OpenSeasonAsync(long seasonId)
    {
        BiliSeason season;
        try
        {
            season = await App.Services.GetRequiredService<BiliClient>().SeasonAsync(seasonId);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or BiliException or System.Text.Json.JsonException)
        {
            return;
        }

        var kind = season.SeasonType == 2 ? MediaKind.Movie : MediaKind.Tv;
        var name = BiliPgcSource.SeriesName(season.Title);
        var key = BiliPgcSource.BaseName(name);
        try
        {
            var found = await App.Services.GetRequiredService<TmdbClient>().SearchAsync(kind, name, kind == MediaKind.Movie ? season.Year : null);
            var match = found.FirstOrDefault(m =>
                (BiliPgcSource.BaseName(m.Title) == key || BiliPgcSource.BaseName(m.OriginalTitle) == key)
                && (kind == MediaKind.Tv || m.Year is null || season.Year is null || Math.Abs(m.Year.Value - season.Year.Value) <= 1));
            if (match is not null)
            {
                var number = BiliPgcSource.ParseLabel(season.Title).Number ?? BiliPgcSource.ParseLabel(season.SeasonTitle).Number;
                Navigator.OpenMedia(match, kind == MediaKind.Tv && number > 1 ? number : null);
                return;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            // TMDB unreachable: B站 can still play it.
        }

        if (BiliPgcSource.CandidateFor(season) is not { } candidate) return;
        var panel = new SourcePanelViewModel(App.Services.GetRequiredService<SourceSearchService>());
        var source = panel.UseLocal(candidate);
        var item = new MediaItem(0, kind, season.Title, null, null, season.Year, 0, 0, season.Cover, season.Cover, [], null)
        {
            LocalKey = "bilipgc:" + season.SeasonId, // not a catalogue title: kept out of 继续观看 like B站 videos
        };
        Navigator.OpenPlayback(new PlaybackRequest(item, panel, source, 0, null, []));
    }
}

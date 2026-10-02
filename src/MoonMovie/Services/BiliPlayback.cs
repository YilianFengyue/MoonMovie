using Microsoft.Extensions.DependencyInjection;
using MoonMovie.Core.Bilibili;
using MoonMovie.Core.Models;
using MoonMovie.Core.Sources;
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
}

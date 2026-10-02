using System.Globalization;
using System.Text.Json;

namespace MoonMovie.Core.Bilibili;

/// <summary>The 「B站」 page: 新番时间表, the 正版 index, 热门 / 排行榜, and the signed-in account's 追番, 稍后再看, 收藏夹.</summary>
public sealed partial class BiliClient
{
    /// <summary>A week either side of today. <paramref name="type"/>: 1 番剧, 4 国创.</summary>
    public async Task<IReadOnlyList<BiliTimelineDay>> TimelineAsync(int type = 1, CancellationToken ct = default)
    {
        var d = await GetAsync("/pgc/web/timeline", new()
        {
            ["types"] = type.ToString(CultureInfo.InvariantCulture),
            ["before"] = "6",
            ["after"] = "6",
        }, signed: false, ct).ConfigureAwait(false);
        if (d.ValueKind != JsonValueKind.Array) return [];

        var year = DateTime.Now.Year;
        return d.EnumerateArray().Select(day =>
        {
            var parts = (Str(day, "date") ?? "").Split('-');
            var date = parts.Length == 2 && int.TryParse(parts[0], out var m) && int.TryParse(parts[1], out var dd)
                ? new DateOnly(year, m, dd)
                : DateOnly.FromDateTime(DateTime.Today);
            var episodes = day.TryGetProperty("episodes", out var eps) && eps.ValueKind == JsonValueKind.Array
                ? eps.EnumerateArray().Select(e => new BiliTimelineEpisode(
                    Long(e, "season_id"),
                    Long(e, "episode_id"),
                    Str(e, "title") ?? "",
                    Absolute(Str(e, "square_cover") ?? Str(e, "cover")),
                    Str(e, "pub_index") ?? "",
                    Str(e, "pub_time") ?? "",
                    Long(e, "published") == 1)).ToArray()
                : [];
            return new BiliTimelineDay(date, (int)Long(day, "day_of_week"), Long(day, "is_today") == 1, episodes);
        }).ToArray();
    }

    /// <summary>
    /// What the 片库 index can filter and sort by for one season type (1 番剧, 4 国创, 2 电影, 5 电视剧,
    /// 3 纪录片). The first value of each filter is "all".
    /// </summary>
    public async Task<BiliIndexConditions> IndexConditionsAsync(int seasonType, CancellationToken ct = default)
    {
        var d = await GetAsync("/pgc/season/index/condition", new()
        {
            ["season_type"] = seasonType.ToString(CultureInfo.InvariantCulture),
            ["type"] = "0",
        }, signed: false, ct).ConfigureAwait(false);

        var filters = d.TryGetProperty("filter", out var f) && f.ValueKind == JsonValueKind.Array
            ? f.EnumerateArray().Select(x => new BiliIndexFilter(
                Str(x, "field") ?? "",
                Str(x, "name") ?? "",
                x.TryGetProperty("values", out var v) && v.ValueKind == JsonValueKind.Array
                    ? v.EnumerateArray().Select(o => (Str(o, "keyword") ?? "-1", Str(o, "name") ?? "")).ToArray()
                    : [])).Where(x => x.Field.Length > 0 && x.Values.Count > 1).ToArray()
            : [];
        var orders = d.TryGetProperty("order", out var o2) && o2.ValueKind == JsonValueKind.Array
            ? o2.EnumerateArray().Select(x => (Str(x, "field") ?? "", Str(x, "name") ?? "")).Where(x => x.Item1.Length > 0).ToArray()
            : [];
        return new BiliIndexConditions(filters, orders);
    }

    /// <summary>One page of the 片库 index; <paramref name="filters"/> maps a filter field to its chosen keyword.</summary>
    public async Task<BiliIndexPage> IndexAsync(int seasonType, IReadOnlyDictionary<string, string> filters, string order, int page,
        CancellationToken ct = default)
    {
        var query = new Dictionary<string, string>
        {
            ["season_type"] = seasonType.ToString(CultureInfo.InvariantCulture),
            ["type"] = "1",
            ["st"] = seasonType.ToString(CultureInfo.InvariantCulture),
            ["order"] = order,
            ["sort"] = "0",
            ["page"] = page.ToString(CultureInfo.InvariantCulture),
            ["pagesize"] = "30",
        };
        foreach (var (field, keyword) in filters) query[field] = keyword;

        var d = await GetAsync("/pgc/season/index/result", query, signed: false, ct).ConfigureAwait(false);
        var items = d.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(x => new BiliSeasonCard(
                Long(x, "season_id"),
                Str(x, "title") ?? "",
                Absolute(Str(x, "cover")),
                (int)Long(x, "season_type"),
                Str(x, "badge") is { Length: > 0 } badge ? badge : null,
                Str(x, "index_show"),
                Str(x, "order"),
                Str(x, "score") is { Length: > 0 } score ? score : null)).ToArray()
            : [];
        return new BiliIndexPage(items, Long(d, "has_next") == 1);
    }

    /// <summary>热门: what is trending across B站 now.</summary>
    public async Task<BiliVideoPage> PopularAsync(int page = 1, CancellationToken ct = default)
    {
        var d = await GetAsync("/x/web-interface/popular", new()
        {
            ["ps"] = "30",
            ["pn"] = page.ToString(CultureInfo.InvariantCulture),
        }, signed: false, ct).ConfigureAwait(false);
        var items = d.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(ArchiveVideo).ToArray()
            : [];
        var noMore = d.TryGetProperty("no_more", out var nm) && nm.ValueKind == JsonValueKind.True;
        return new BiliVideoPage(items, !noMore && items.Length > 0);
    }

    /// <summary>排行榜: the top 100 of the last three days; <paramref name="rid"/> 0 is 全站.</summary>
    public async Task<IReadOnlyList<BiliVideo>> RankingAsync(int rid = 0, CancellationToken ct = default)
    {
        var d = await GetAsync("/x/web-interface/ranking/v2", new()
        {
            ["rid"] = rid.ToString(CultureInfo.InvariantCulture),
            ["type"] = "all",
        }, signed: false, ct).ConfigureAwait(false);
        return d.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(ArchiveVideo).ToArray()
            : [];
    }

    // ----- The signed-in account ---------------------------------------------------------------------------

    /// <summary>我的追番 (<paramref name="type"/> 1) or 追剧 (2), most recently followed first.</summary>
    public async Task<(IReadOnlyList<BiliSeasonCard> Items, int Total)> FollowingAsync(int type, int page = 1, CancellationToken ct = default)
    {
        if (Credentials is not { UserId.Length: > 0 } c) return ([], 0);
        var d = await GetAsync("/x/space/bangumi/follow/list", new()
        {
            ["type"] = type.ToString(CultureInfo.InvariantCulture),
            ["follow_status"] = "0",
            ["pn"] = page.ToString(CultureInfo.InvariantCulture),
            ["ps"] = "30",
            ["vmid"] = c.UserId,
        }, signed: false, ct).ConfigureAwait(false);

        var items = d.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(x =>
            {
                var latest = x.TryGetProperty("new_ep", out var ep) ? Str(ep, "index_show") : null;
                var progress = Str(x, "progress") is { Length: > 0 } p ? p.Split(' ')[0] : null;
                return new BiliSeasonCard(
                    Long(x, "season_id"),
                    Str(x, "title") ?? "",
                    Absolute(Str(x, "cover")),
                    (int)Long(x, "season_type"),
                    Str(x, "badge") is { Length: > 0 } badge ? badge : null,
                    progress ?? latest,
                    progress is not null ? latest : null,
                    x.TryGetProperty("rating", out var r) && r.ValueKind == JsonValueKind.Object && r.TryGetProperty("score", out var s)
                        && s.ValueKind == JsonValueKind.Number ? s.GetDouble().ToString("0.0", CultureInfo.InvariantCulture) : null);
            }).ToArray()
            : [];
        return (items, (int)Long(d, "total"));
    }

    /// <summary>稍后再看.</summary>
    public async Task<IReadOnlyList<BiliVideo>> WatchLaterAsync(CancellationToken ct = default)
    {
        if (Credentials is null) return [];
        var d = await GetAsync("/x/v2/history/toview", [], signed: false, ct).ConfigureAwait(false);
        return d.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(ArchiveVideo).ToArray()
            : [];
    }

    /// <summary>The account's own 收藏夹.</summary>
    public async Task<IReadOnlyList<BiliFavFolder>> FavFoldersAsync(CancellationToken ct = default)
    {
        if (Credentials is not { UserId.Length: > 0 } c) return [];
        var d = await GetAsync("/x/v3/fav/folder/created/list-all", new() { ["up_mid"] = c.UserId }, signed: false, ct).ConfigureAwait(false);
        return d.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(x => new BiliFavFolder(Long(x, "id"), Str(x, "title") ?? "", (int)Long(x, "media_count"))).ToArray()
            : [];
    }

    /// <summary>One page of a 收藏夹's videos (removed ones and non-videos left out).</summary>
    public async Task<BiliVideoPage> FavItemsAsync(long folderId, int page = 1, CancellationToken ct = default)
    {
        var d = await GetAsync("/x/v3/fav/resource/list", new()
        {
            ["media_id"] = folderId.ToString(CultureInfo.InvariantCulture),
            ["pn"] = page.ToString(CultureInfo.InvariantCulture),
            ["ps"] = "20",
            ["platform"] = "web",
        }, signed: false, ct).ConfigureAwait(false);

        var items = d.TryGetProperty("medias", out var medias) && medias.ValueKind == JsonValueKind.Array
            ? medias.EnumerateArray()
                .Where(m => Long(m, "type") == 2 && Str(m, "bvid") is { Length: > 0 } && Long(m, "attr") == 0)
                .Select(m =>
                {
                    var upper = m.TryGetProperty("upper", out var u) ? u : default;
                    var stats = m.TryGetProperty("cnt_info", out var s) ? s : default;
                    return new BiliVideo(Str(m, "bvid")!, Long(m, "id"), Str(m, "title") ?? "", Str(upper, "name") ?? "", Long(upper, "mid"),
                        Absolute(Str(m, "cover")), (int)Long(m, "duration"), Long(stats, "play"), Long(stats, "danmaku"),
                        DateTimeOffset.FromUnixTimeSeconds(Long(m, "pubtime")), Str(m, "intro"));
                }).ToArray()
            : [];
        var hasMore = d.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True;
        return new BiliVideoPage(items, hasMore);
    }

    /// <summary>An archive (视频) object as the popular, ranking and 稍后再看 lists return it.</summary>
    private static BiliVideo ArchiveVideo(JsonElement v)
    {
        var owner = v.TryGetProperty("owner", out var o) ? o : default;
        var stat = v.TryGetProperty("stat", out var s) ? s : default;
        return new BiliVideo(Str(v, "bvid") ?? "", Long(v, "aid"), Plain(Str(v, "title")), Str(owner, "name") ?? "", Long(owner, "mid"),
            Absolute(Str(v, "pic")), (int)Long(v, "duration"), Long(stat, "view"), Long(stat, "danmaku"),
            DateTimeOffset.FromUnixTimeSeconds(Long(v, "pubdate")), Str(v, "desc"));
    }
}

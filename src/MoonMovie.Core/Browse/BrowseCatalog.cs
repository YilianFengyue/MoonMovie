using System.Globalization;
using MoonMovie.Core.Models;
using MoonMovie.Core.Tmdb;

namespace MoonMovie.Core.Browse;

public enum BrowseSection
{
    Movie,
    Tv,
    Anime,
}

/// <param name="Value">Null means "no constraint" (全部).</param>
public sealed record FilterOption(string Label, string? Value);

public sealed record FilterDimension(string Key, string Label, IReadOnlyList<FilterOption> Options, int DefaultIndex = 0)
{
    public FilterOption Default => Options[DefaultIndex];
}

/// <summary>
/// The filter vocabulary of the 电影 / 剧集 / 动漫 pages and how a selection maps onto TMDB /discover.
/// </summary>
public static class BrowseCatalog
{
    public const string Form = "form";
    public const string Genre = "genre";
    public const string Region = "region";
    public const string Year = "year";
    public const string Sort = "sort";

    // TMDB keywords: hentai, hentai adaptation, ecchi — not flagged adult, but not what a browse page should lead with.
    private const string ExplicitKeywords = "198385|384581|195669";

    public static string Title(BrowseSection section) => section switch
    {
        BrowseSection.Movie => "电影",
        BrowseSection.Tv => "剧集",
        _ => "动漫",
    };

    public static string Tagline(BrowseSection section) => section switch
    {
        BrowseSection.Movie => "院线新片、影史经典与值得一看的冷门佳作",
        BrowseSection.Tv => "追更中的热剧，和那些值得一口气看完的好剧",
        _ => "新番、经典番剧与剧场版",
    };

    public static IReadOnlyList<FilterDimension> Dimensions(BrowseSection section, int currentYear)
    {
        var list = new List<FilterDimension>(5);
        if (section == BrowseSection.Anime)
        {
            list.Add(new FilterDimension(Form, "形式", [new("番剧", "tv"), new("剧场版", "movie")]));
        }

        list.Add(new FilterDimension(Genre, "类型", section switch
        {
            BrowseSection.Movie => WithAll(
                ("动作", "28"), ("科幻", "878"), ("剧情", "18"), ("喜剧", "35"), ("爱情", "10749"),
                ("悬疑", "9648"), ("惊悚", "53"), ("犯罪", "80"), ("恐怖", "27"), ("冒险", "12"),
                ("奇幻", "14"), ("动画", "16"), ("家庭", "10751"), ("历史", "36"), ("战争", "10752"),
                ("音乐", "10402"), ("纪录", "99")),
            BrowseSection.Tv => WithAll(
                ("剧情", "18"), ("喜剧", "35"), ("犯罪", "80"), ("悬疑", "9648"), ("科幻奇幻", "10765"),
                ("动作冒险", "10759"), ("家庭", "10751"), ("动画", "16"), ("纪录", "99"), ("真人秀", "10764"),
                ("战争政治", "10768"), ("儿童", "10762"), ("脱口秀", "10767")),
            // Movie id | series id: the two genre lists number the same idea differently.
            _ => WithAll(
                ("热血", "28|10759"), ("科幻", "878|10765"), ("奇幻", "14|10765"), ("喜剧", "35|35"),
                ("剧情", "18|18"), ("悬疑", "9648|9648"), ("冒险", "12|10759"), ("家庭", "10751|10751"),
                ("儿童", "10751|10762")),
        }));

        list.Add(section == BrowseSection.Anime
            ? new FilterDimension(Region, "地区",
                WithAll(("日本", "JP"), ("中国大陆", "CN"), ("美国", "US"), ("韩国", "KR")), DefaultIndex: 1)
            : new FilterDimension(Region, "地区",
                WithAll(("中国大陆", "CN"), ("中国香港", "HK"), ("中国台湾", "TW"), ("美国", "US"), ("日本", "JP"),
                    ("韩国", "KR"), ("英国", "GB"), ("法国", "FR"), ("德国", "DE"), ("印度", "IN"), ("泰国", "TH"))));

        var years = new List<FilterOption> { new("全部", null) };
        for (var y = currentYear; y > currentYear - 3; y--)
        {
            years.Add(new FilterOption(y.ToString(CultureInfo.InvariantCulture), $"{y}-{y}"));
        }

        var decade = currentYear / 10 * 10;
        years.Add(new FilterOption($"{decade}年代", $"{decade}-{decade + 9}"));
        for (var d = decade - 10; d >= 1990; d -= 10)
        {
            years.Add(new FilterOption(d >= 2000 ? $"{d}年代" : $"{d % 100}年代", $"{d}-{d + 9}"));
        }

        years.Add(new FilterOption("更早", "-1989"));
        list.Add(new FilterDimension(Year, "年份", years));

        list.Add(new FilterDimension(Sort, "排序",
            [new("最热", "hot"), new("高分", "top"), new("最新", "new"), new("最多评价", "votes")]));

        return list;
    }

    /// <summary>Turns a selection (dimension key → option value) into the TMDB endpoint and query.</summary>
    public static (MediaKind Kind, Dictionary<string, string> Query) Build(
        BrowseSection section, IReadOnlyDictionary<string, string?> selection, DateOnly today)
    {
        string? Get(string key) => selection.TryGetValue(key, out var v) ? v : null;

        var kind = section switch
        {
            BrowseSection.Movie => MediaKind.Movie,
            BrowseSection.Tv => MediaKind.Tv,
            _ => Get(Form) == "movie" ? MediaKind.Movie : MediaKind.Tv,
        };
        var dateField = kind == MediaKind.Movie ? "primary_release_date" : "first_air_date";
        var query = new Dictionary<string, string>();

        var genres = new List<string>(2);
        if (section == BrowseSection.Anime) genres.Add(TmdbGenres.Animation.ToString(CultureInfo.InvariantCulture));
        if (Get(Genre) is { } genre)
        {
            var pair = genre.Split('|');
            genres.Add(pair.Length == 2 ? pair[kind == MediaKind.Movie ? 0 : 1] : genre);
        }

        if (genres.Count > 0) query["with_genres"] = string.Join(',', genres.Distinct());

        var region = Get(Region);
        if (region is not null) query["with_origin_country"] = region;

        string? from = null;
        string? to = null;
        if (Get(Year) is { } range)
        {
            var parts = range.Split('-');
            if (parts[0].Length > 0) from = $"{parts[0]}-01-01";
            if (parts[1].Length > 0) to = $"{parts[1]}-12-31";
        }

        // Narrow filters thin out the catalogue quickly, so vote floors relax as constraints pile up.
        var narrow = region is not null || Get(Genre) is not null || Get(Year) is not null;
        var todayText = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        switch (Get(Sort))
        {
            case "top":
                query["sort_by"] = "vote_average.desc";
                query["vote_count.gte"] = region is not null ? "40"
                    : narrow ? kind == MediaKind.Movie ? "300" : "120"
                    : kind == MediaKind.Movie ? "800" : "300";
                break;
            case "new":
                query["sort_by"] = $"{dateField}.desc";
                query["vote_count.gte"] = "2";
                to = to is null || string.CompareOrdinal(to, todayText) > 0 ? todayText : to;
                break;
            case "votes":
                query["sort_by"] = "vote_count.desc";
                break;
            default:
                // Popularity alone is easy to game (obscure titles with a handful of votes spike to the top),
                // so the hot list asks for some audience too.
                query["sort_by"] = "popularity.desc";
                query["vote_count.gte"] = section == BrowseSection.Anime ? "40" : narrow ? "10" : "50";
                break;
        }

        if (from is not null) query[$"{dateField}.gte"] = from;
        if (to is not null) query[$"{dateField}.lte"] = to;
        // TV films (series specials and the like) crowd the film charts; leave them to the series pages.
        if (kind == MediaKind.Movie && section == BrowseSection.Movie) query["without_genres"] = "10770";
        query["without_keywords"] = ExplicitKeywords;
        query["include_adult"] = "false";
        return (kind, query);
    }

    private static FilterOption[] WithAll(params (string Label, string Value)[] options) =>
        [new FilterOption("全部", null), .. options.Select(o => new FilterOption(o.Label, o.Value))];
}

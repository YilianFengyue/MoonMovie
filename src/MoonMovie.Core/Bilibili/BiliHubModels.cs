namespace MoonMovie.Core.Bilibili;

/// <summary>A 正版 season as a card: 片库, 我的追番, 新番时间表.</summary>
/// <param name="Meta">The line under the title: "全12话", "更新至第5话", "看到第3话".</param>
/// <param name="Extra">A second, quieter fact: "1770.5万追番", "9.7分".</param>
public sealed record BiliSeasonCard(
    long SeasonId,
    string Title,
    string? Cover,
    int SeasonType,
    string? Badge,
    string? Meta,
    string? Extra,
    string? Score);

/// <summary>One day of the 新番时间表.</summary>
public sealed record BiliTimelineDay(DateOnly Date, int DayOfWeek, bool IsToday, IReadOnlyList<BiliTimelineEpisode> Episodes);

/// <param name="Published">Out already; otherwise it airs at <see cref="Time"/>.</param>
public sealed record BiliTimelineEpisode(long SeasonId, long EpisodeId, string Title, string? Cover, string Index, string Time, bool Published);

/// <summary>A filter of the 片库 index ("地区": 全部地区 / 国产 / 日本…) as B站 describes it for one season type.</summary>
public sealed record BiliIndexFilter(string Field, string Name, IReadOnlyList<(string Keyword, string Name)> Values);

public sealed record BiliIndexConditions(IReadOnlyList<BiliIndexFilter> Filters, IReadOnlyList<(string Field, string Name)> Orders);

public sealed record BiliIndexPage(IReadOnlyList<BiliSeasonCard> Items, bool HasNext);

public sealed record BiliFavFolder(long Id, string Title, int Count);

/// <summary>A page of videos (稍后再看, 收藏夹, 热门) and whether more follow.</summary>
public sealed record BiliVideoPage(IReadOnlyList<BiliVideo> Items, bool HasMore);

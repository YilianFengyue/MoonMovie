using MoonMovie.Core.Models;

namespace MoonMovie.Core.Tmdb;

public static class TmdbGenres
{
    public const int Action = 28;
    public const int Animation = 16;
    public const int Comedy = 35;
    public const int Crime = 80;
    public const int Documentary = 99;
    public const int Drama = 18;
    public const int Fantasy = 14;
    public const int Horror = 27;
    public const int Mystery = 9648;
    public const int Romance = 10749;
    public const int ScienceFiction = 878;
    public const int Thriller = 53;

    private static readonly Dictionary<int, string> Movie = new()
    {
        [28] = "动作", [12] = "冒险", [16] = "动画", [35] = "喜剧", [80] = "犯罪",
        [99] = "纪录", [18] = "剧情", [10751] = "家庭", [14] = "奇幻", [36] = "历史",
        [27] = "恐怖", [10402] = "音乐", [9648] = "悬疑", [10749] = "爱情", [878] = "科幻",
        [10770] = "电视电影", [53] = "惊悚", [10752] = "战争", [37] = "西部",
    };

    private static readonly Dictionary<int, string> Tv = new()
    {
        [10759] = "动作冒险", [16] = "动画", [35] = "喜剧", [80] = "犯罪", [99] = "纪录",
        [18] = "剧情", [10751] = "家庭", [10762] = "儿童", [9648] = "悬疑", [10763] = "新闻",
        [10764] = "真人秀", [10765] = "科幻奇幻", [10766] = "肥皂剧", [10767] = "脱口秀",
        [10768] = "战争政治", [37] = "西部",
    };

    public static IReadOnlyList<string> Names(MediaKind kind, IEnumerable<int> ids)
    {
        var map = kind == MediaKind.Movie ? Movie : Tv;
        return ids.Select(id => map.TryGetValue(id, out var n) ? n : null)
                  .Where(n => n is not null)
                  .Select(n => n!)
                  .ToArray();
    }
}

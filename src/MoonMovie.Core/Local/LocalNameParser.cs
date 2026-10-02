using System.Text.RegularExpressions;

namespace MoonMovie.Core.Local;

/// <param name="Title">Best guess at the title, for TMDB search and display.</param>
/// <param name="AltTitle">A second spelling when the name carries two (e.g. Chinese and English).</param>
/// <param name="TrailingNumber">"片名 03" style number that only counts as an episode when siblings agree.</param>
/// <param name="LooksLikeAnime">Fansub naming ("[字幕组] 片名 - 03"), a strong hint for Anime4K and danmaku.</param>
public sealed record ParsedName(
    string Title,
    string? AltTitle,
    int? Year,
    int? Season,
    int? Episode,
    int? TrailingNumber,
    bool LooksLikeAnime);

/// <summary>
/// Reads title, year, season and episode out of video file names as people actually name them:
/// <c>S01E02</c>, <c>第2集</c>, <c>[字幕组] 片名 - 03 [1080p]</c>, <c>片名.2019.1080p.BluRay.x264</c>, or a bare
/// <c>01.mkv</c> inside a show folder.
/// </summary>
public static partial class LocalNameParser
{
    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".ts", ".m2ts", ".mts", ".flv", ".webm", ".rmvb", ".rm", ".mov", ".wmv",
        ".mpg", ".mpeg", ".3gp", ".ogv", ".vob",
    };

    public static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ass", ".ssa", ".srt", ".vtt", ".sup", ".sub", ".idx",
    };

    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));

    public static bool IsSubtitle(string path) => SubtitleExtensions.Contains(Path.GetExtension(path));

    /// <summary>Parses a file (or disc folder) path; parent folders fill in what the file name lacks.</summary>
    public static ParsedName Parse(string path, bool isFolder = false)
    {
        var name = isFolder ? Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) : Path.GetFileNameWithoutExtension(path);
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
        var parsed = ParseName(name);

        // Season from a "Season 2" / "S02" / "第二季" folder.
        var folder = parent is null ? null : Path.GetFileName(parent);
        var seasonFolder = folder is not null ? SeasonFolder(folder) : null;
        var showFolder = seasonFolder is not null && parent is not null ? Path.GetDirectoryName(parent) : parent;
        if (parsed.Season is null && seasonFolder is not null && parsed.Episode is not null)
        {
            parsed = parsed with { Season = seasonFolder };
        }

        // "01.mkv", "E01.mkv", "S01E01.mkv": the title lives in the folder name.
        if (parsed.Title.Length == 0 && showFolder is not null)
        {
            var show = ParseName(Path.GetFileName(showFolder));
            parsed = parsed with
            {
                Title = show.Title,
                AltTitle = show.AltTitle,
                Year = parsed.Year ?? show.Year,
                Season = parsed.Season ?? show.Season ?? seasonFolder,
                Episode = parsed.Episode ?? parsed.TrailingNumber,
                TrailingNumber = null,
                LooksLikeAnime = parsed.LooksLikeAnime || show.LooksLikeAnime,
            };
        }

        return parsed;
    }

    /// <summary>Parses one name (no folders).</summary>
    public static ParsedName ParseName(string raw)
    {
        var name = Junk().Replace(raw, " ");
        var anime = name.TrimStart().StartsWith('[') || name.TrimStart().StartsWith('【');

        int? season = null, episode = null, year = null;
        var cut = name.Length; // the title is what comes before the first technical marker

        void Cut(Match m)
        {
            if (m.Success && m.Index < cut) cut = m.Index;
        }

        var se = SeasonEpisode().Match(name);
        if (se.Success)
        {
            season = int.Parse(se.Groups["s"].Value);
            episode = int.Parse(se.Groups["e"].Value);
            Cut(se);
        }
        else if (CrossEpisode().Match(name) is { Success: true } x)
        {
            season = int.Parse(x.Groups["s"].Value);
            episode = int.Parse(x.Groups["e"].Value);
            Cut(x);
        }

        var cnSeason = ChineseSeason().Match(name);
        if (cnSeason.Success)
        {
            season ??= Number(cnSeason.Groups["n"].Value);
            Cut(cnSeason);
        }

        var cnEpisode = ChineseEpisode().Match(name);
        if (cnEpisode.Success)
        {
            episode ??= Number(cnEpisode.Groups["n"].Value);
            Cut(cnEpisode);
        }

        if (episode is null && EpisodeMarker().Match(name) is { Success: true } ep)
        {
            episode = int.Parse(ep.Groups["e"].Value);
            Cut(ep);
        }

        if (episode is null && DashEpisode().Match(name) is { Success: true } dash)
        {
            episode = int.Parse(dash.Groups["e"].Value);
            anime = true;
            Cut(dash);
        }

        // Bracketed tokens: "[Group][Title][03][1080p]".
        var brackets = Bracket().Matches(name).Select(m => (Text: m.Groups["t"].Value.Trim(), m.Index)).ToList();
        if (episode is null)
        {
            foreach (var (text, index) in brackets.Skip(1))
            {
                if (BracketEpisode().Match(text) is { Success: true } be)
                {
                    episode = int.Parse(be.Groups["e"].Value);
                    anime = true;
                    if (index < cut) cut = index;
                    break;
                }
            }
        }

        var yearMatch = Year().Matches(name).LastOrDefault(m => m.Index > 0 || brackets.Count > 0);
        if (yearMatch is not null)
        {
            year = int.Parse(yearMatch.Groups["y"].Value);
            Cut(yearMatch);
        }

        // Technical tags ("1080p", "字幕组"…) end the title, except inside the leading "[Group]" bracket.
        var groupEnd = anime && brackets.Count > 0 && name.TrimStart().Length > 0 ? brackets[0].Index + brackets[0].Text.Length : -1;
        var technical = Technical().Matches(name).FirstOrDefault(m => m.Index > groupEnd);
        if (technical is not null) Cut(technical);

        var title = TitleFrom(name[..cut]);
        if (title.Length == 0 && brackets.Count > 1)
        {
            // "[Group][Title][03]": the title is the first meaningful bracket after the group.
            title = brackets.Skip(1).Select(b => TitleFrom(b.Text)).FirstOrDefault(t => t.Length > 0 && !IsTechnical(t)) ?? "";
        }

        // 《片名》 is an explicit title.
        if (BookTitle().Match(raw) is { Success: true } book)
        {
            title = book.Groups["t"].Value.Trim();
        }

        int? trailing = null;
        if (episode is null && TrailingNumber().Match(title) is { Success: true } tn)
        {
            // "三体 12" (an episode only if siblings agree) or a bare "01" (the folder names the show).
            trailing = int.Parse(tn.Groups["n"].Value);
            title = title[..tn.Index].Trim();
        }

        var (primary, alt) = SplitScripts(title);
        return new ParsedName(primary, alt, year, season, episode, trailing, anime);
    }

    /// <summary>"Season 2", "S02", "第二季", "Specials" (0); null for anything else.</summary>
    public static int? SeasonFolder(string folder)
    {
        if (SeasonFolderPattern().Match(folder) is { Success: true } m) return int.Parse(m.Groups["n"].Value);
        if (ChineseSeason().Match(folder) is { Success: true } c && c.Length >= folder.Trim().Length - 1) return Number(c.Groups["n"].Value);
        if (folder.Equals("Specials", StringComparison.OrdinalIgnoreCase) || folder == "特典" || folder == "SPs") return 0;
        return null;
    }

    /// <summary>Key that groups files of one title: case, spacing and punctuation do not matter.</summary>
    public static string Normalize(string title) =>
        new string(title.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string TitleFrom(string text)
    {
        var t = Bracket().Replace(text, " ");
        t = t.Replace('.', ' ').Replace('_', ' ');
        t = Separators().Replace(t, " ").Trim(' ', '-', '–', '—', '·', ':', '+', '&');
        return t;
    }

    private static bool IsTechnical(string text) => Technical().IsMatch(text) || text.All(c => char.IsDigit(c) || c == ' ');

    /// <summary>"流浪地球 The Wandering Earth" → ("流浪地球", "The Wandering Earth").</summary>
    private static (string, string?) SplitScripts(string title)
    {
        if (!title.Any(IsCjk) || !title.Any(c => c is >= 'A' and <= 'z')) return (title, null);
        var cjk = CjkRun().Match(title);
        if (!cjk.Success) return (title, null);
        var rest = Separators().Replace(title.Remove(cjk.Index, cjk.Length), " ").Trim(' ', '-', '·', ':');
        return (cjk.Value.Trim(), LatinWord().IsMatch(rest) ? rest : null);
    }

    private static bool IsCjk(char c) => c is >= '一' and <= '鿿' or >= '぀' and <= 'ヿ';

    /// <summary>Arabic or Chinese numerals ("12", "十二", "二十三").</summary>
    private static int? Number(string text)
    {
        if (int.TryParse(text, out var n)) return n;
        const string digits = "零一二三四五六七八九";
        var total = 0;
        var current = 0;
        foreach (var c in text)
        {
            var d = digits.IndexOf(c);
            if (c == '两') d = 2;
            if (d >= 0)
            {
                current = d;
            }
            else if (c == '十')
            {
                total += (current == 0 ? 1 : current) * 10;
                current = 0;
            }
            else if (c == '百')
            {
                total += (current == 0 ? 1 : current) * 100;
                current = 0;
            }
        }

        total += current;
        return total > 0 ? total : null;
    }

    // Website stamps and download-site names ("[电影天堂www.dy2018.com]", "阳光电影www.ygdy8.com.").
    [GeneratedRegex(@"[\[【][^\]】]*(?:www\.|\.com|\.cc)[^\]】]*[\]】]|www\.[A-Za-z0-9-]+\.[A-Za-z]{2,4}\b\.?|[A-Za-z0-9-]+\.(?:com|cc|top|vip|xyz)\b\.?|阳光电影|电影天堂|飘花电影|迅雷下载|人人影视|BT天堂|最新电影|高清电影", RegexOptions.IgnoreCase)]
    private static partial Regex Junk();

    [GeneratedRegex(@"(?<![A-Za-z0-9])S(?<s>\d{1,2})[ ._-]?E[Pp]?(?<e>\d{1,4})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonEpisode();

    [GeneratedRegex(@"(?<![\dA-Za-z])(?<s>\d{1,2})x(?<e>\d{2,3})(?![\dA-Za-z])")]
    private static partial Regex CrossEpisode();

    [GeneratedRegex(@"第\s*(?<n>\d{1,2}|[一二三四五六七八九十两]{1,3})\s*季")]
    private static partial Regex ChineseSeason();

    [GeneratedRegex(@"第\s*(?<n>\d{1,4}|[一二三四五六七八九十百两零]{1,5})\s*[集话話期回]")]
    private static partial Regex ChineseEpisode();

    [GeneratedRegex(@"(?<![A-Za-z])(?:EP|E|Episode)[ ._]?(?<e>\d{1,4})(?![\dA-Za-z])", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeMarker();

    [GeneratedRegex(@"\s[-–]\s(?<e>\d{1,4})(?:v\d)?(?:\s|$|\[|\(|【)")]
    private static partial Regex DashEpisode();

    [GeneratedRegex(@"[\[【(（](?<t>[^\]】)）]*)[\]】)）]")]
    private static partial Regex Bracket();

    [GeneratedRegex(@"^(?:EP?|第)?(?<e>\d{1,4})(?:v\d|END|集|话|話)?$", RegexOptions.IgnoreCase)]
    private static partial Regex BracketEpisode();

    [GeneratedRegex(@"(?<![\dA-Za-z])[\[(（.\s_-]?(?<y>19[2-9]\d|20[0-4]\d)[\])）.\s_-]?(?![\dA-Za-z])")]
    private static partial Regex Year();

    [GeneratedRegex(@"(?<![A-Za-z])(?:\d{3,4}[pPiI]|4K|8K|UHD|HD|Blu-?Ray|BDRip|BDMV|BD|WEB-?DL|WEB-?Rip|WEB|HDTV|HDRip|DVDRip|DVD|REMUX|x26[45]|H\.?26[45]|HEVC|AVC|AV1|10bit|8bit|HDR10\+?|HDR|DV|DoVi|AAC|AC3|E?AC-?3|DDP?\d?|DTS(?:-HD)?|TrueHD|Atmos|FLAC|OPUS|MA|CHS|CHT|GB|BIG5|国语|粤语|国粤|中字|双语|简繁|简体|繁体|中英|内封|内嵌|外挂|完结|全集|合集|特效|字幕组|官方|高清|蓝光|超清|未删减|导演剪辑版)(?![A-Za-z])", RegexOptions.IgnoreCase)]
    private static partial Regex Technical();

    [GeneratedRegex(@"(?:^|\s)(?<n>\d{1,3})$")]
    private static partial Regex TrailingNumber();

    [GeneratedRegex(@"《(?<t>[^》]{1,40})》")]
    private static partial Regex BookTitle();

    [GeneratedRegex(@"[A-Za-z]{3,}")]
    private static partial Regex LatinWord();

    [GeneratedRegex(@"[\s._]+")]
    private static partial Regex Separators();

    [GeneratedRegex(@"[一-鿿぀-ヿ][一-鿿぀-ヿ0-9０-９：:·・！!？?\s]*")]
    private static partial Regex CjkRun();

    [GeneratedRegex(@"^(?:Season|S)\s*(?<n>\d{1,2})$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonFolderPattern();
}

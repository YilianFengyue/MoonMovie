using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MoonMovie.Core.Models;

namespace MoonMovie.Core.Sources;

/// <summary>
/// Decides whether a resource-site entry is the title we want and how confident we are.
/// Resource sites are noisy (commentary cuts, trailers, "AI 漫剧", other seasons), so this is strict:
/// the normalised title must match, the season must match, and year/category/cast adjust the score.
/// </summary>
public static partial class SourceMatcher
{
    public const int AcceptThreshold = 80;

    private static readonly string[] NoiseWords =
    [
        "解说", "预告", "花絮", "速看", "混剪", "reaction", "幕后", "特辑", "片段",
        "伦理", "福利", "写真", "短剧", "漫剧", "有声",
    ];

    /// <summary>Returns a score, or -1 when the entry is not the target.</summary>
    public static int Score(MacCmsVod vod, SourceTarget target, int playableLines)
    {
        if (playableLines == 0 || string.IsNullOrWhiteSpace(vod.VodName))
        {
            return -1;
        }

        var category = vod.TypeName ?? string.Empty;
        if (ContainsNoise(vod.VodName) || ContainsNoise(category))
        {
            return -1;
        }

        var name = Normalize(vod.VodName);
        var wanted = Normalize(target.Title);
        var original = target.OriginalTitle is null ? null : Normalize(target.OriginalTitle);
        var wantedSeason = target.Kind == MediaKind.Tv ? target.Season ?? 1 : (int?)null;

        int score;
        int? candidateSeason;
        if (name == wanted)
        {
            score = 100;
            candidateSeason = null;
        }
        else
        {
            (var baseName, candidateSeason) = SplitSeason(name);
            if (baseName == wanted)
            {
                score = 100;
            }
            else if (original is { Length: >= 2 } && (baseName == original || Normalize(vod.VodEn) == original))
            {
                score = 90;
            }
            else
            {
                return -1;
            }
        }

        // Seasons: series must match exactly; a movie must not pick up "Title 2".
        if (wantedSeason is { } ws)
        {
            if ((candidateSeason ?? 1) != ws)
            {
                return -1;
            }
        }
        else if (candidateSeason is not null)
        {
            return -1;
        }

        if (int.TryParse(vod.VodYear, out var year) && year > 1900 && target.Year is { } wantedYear)
        {
            var diff = Math.Abs(year - wantedYear);
            score += diff switch
            {
                0 => 25,
                1 => 8,
                _ => target.Kind == MediaKind.Movie ? -60 : -30,
            };
        }

        var movieLike = category.EndsWith('片') || category.Contains("电影");
        var seriesLike = category.Contains('剧') || category.Contains("综艺");
        if (target.Kind == MediaKind.Movie && seriesLike) score -= 40;
        if (target.Kind == MediaKind.Tv && movieLike) score -= 40;

        var credits = (vod.VodActor ?? string.Empty) + "/" + (vod.VodDirector ?? string.Empty);
        if (target.People.Any(p => p.Length >= 2 && credits.Contains(p, StringComparison.OrdinalIgnoreCase)))
        {
            score += 15;
        }

        return score;
    }

    public static int EpisodeAdjustment(int episodes, int? expected)
    {
        if (expected is not > 0)
        {
            return 0;
        }

        if (episodes >= expected * 0.9 && episodes <= expected * 1.5 + 2) return 10;
        if (episodes > expected * 1.5 + 2) return -15;
        return 0; // still airing / partial
    }

    /// <summary>Lower-case, half-width, no whitespace or punctuation.</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var raw in value.Normalize(NormalizationForm.FormKC))
        {
            var c = char.ToLowerInvariant(raw);
            var category = char.GetUnicodeCategory(c);
            if (char.IsWhiteSpace(c)
                || category is UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation
                    or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation
                    or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation
                    or UnicodeCategory.OtherPunctuation or UnicodeCategory.MathSymbol)
            {
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>"庆余年第二季" → ("庆余年", 2); "庆余年2" → ("庆余年", 2); "庆余年" → ("庆余年", null).</summary>
    public static (string BaseName, int? Season) SplitSeason(string normalized)
    {
        var m = SeasonSuffix().Match(normalized);
        if (m.Success)
        {
            var number = ParseNumber(m.Groups["n"].Value);
            if (number is > 0 and < 60)
            {
                return (normalized[..m.Index], number);
            }
        }

        return (normalized, null);
    }

    private static bool ContainsNoise(string text) =>
        NoiseWords.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));

    private static int? ParseNumber(string text)
    {
        if (int.TryParse(text, out var n))
        {
            return n;
        }

        const string digits = "零一二三四五六七八九";
        var total = 0;
        var current = 0;
        foreach (var c in text)
        {
            var d = digits.IndexOf(c);
            if (d >= 0)
            {
                current = d;
            }
            else if (c == '十')
            {
                total += (current == 0 ? 1 : current) * 10;
                current = 0;
            }
            else
            {
                return null;
            }
        }

        return total + current;
    }

    [GeneratedRegex(@"(?:第(?<n>[0-9一二三四五六七八九十]+)[季部]|season(?<n>\d+)|s(?<n>\d{1,2})|(?<n>\d{1,2}))$")]
    private static partial Regex SeasonSuffix();
}

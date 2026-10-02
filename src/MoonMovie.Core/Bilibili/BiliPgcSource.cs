using System.Globalization;
using System.Text.RegularExpressions;
using MoonMovie.Core.Models;
using MoonMovie.Core.Sources;

namespace MoonMovie.Core.Bilibili;

/// <summary>
/// B站正版 as a source: finds the title among B站's licensed 番剧 / 影视, picks the season TMDB means (by
/// "第N季"), and offers it once per version — the original first, then dubs ("中配", "粤配") as separate entries
/// since the player plays one line per source. Episodes are matched by order (B站 continues numbering across
/// seasons: season 2 may start at 26) and carry a "会员" badge when 大会员 is required.
/// </summary>
public sealed partial class BiliPgcSource(BiliClient client)
{
    public const string SiteKey = "bili-pgc";
    private const string Scheme = "bilipgc://";
    private static readonly TimeSpan CacheAge = TimeSpan.FromHours(6);

    private readonly Dictionary<string, (DateTimeOffset At, IReadOnlyList<SourceCandidate> Found)> _cache = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    public static bool IsOfficial(SourceCandidate candidate) => candidate.Site.Key == SiteKey;

    public static bool IsPgcUrl(string url) => url.StartsWith(Scheme, StringComparison.Ordinal);

    /// <summary>"bilipgc://{ep}/{cid}/{aid}/{season}/{type}".</summary>
    public static (long EpId, long Cid, long Aid, long SeasonId, int SeasonType) Parse(string url)
    {
        var p = url[Scheme.Length..].Split('/');
        return (long.Parse(p[0], CultureInfo.InvariantCulture), long.Parse(p[1], CultureInfo.InvariantCulture),
            long.Parse(p[2], CultureInfo.InvariantCulture), long.Parse(p[3], CultureInfo.InvariantCulture),
            int.Parse(p[4], CultureInfo.InvariantCulture));
    }

    public static string WebUrl(long epId) => $"https://www.bilibili.com/bangumi/play/ep{epId}";

    /// <summary>Every episode plays in full for this account: all free, or 大会员 covers the 会员 ones.</summary>
    public static bool FullyPlayable(SourceCandidate candidate, bool isVip) =>
        candidate.PrimaryLine.Episodes.All(e => e.Badge is null || (isVip && e.Badge == "会员"));

    /// <summary>The title's versions on B站 (empty when B站 does not have it), cached for a few hours.</summary>
    public async Task<IReadOnlyList<SourceCandidate>> FindAsync(SourceTarget target, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(target.CacheKey, out var hit) && DateTimeOffset.Now - hit.At < CacheAge) return hit.Found;
            var found = await SearchAsync(target, ct).ConfigureAwait(false);
            _cache[target.CacheKey] = (DateTimeOffset.Now, found);
            return found;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<SourceCandidate>> SearchAsync(SourceTarget target, CancellationToken ct)
    {
        var hits = await client.SearchMediaAsync(target.Title, ct).ConfigureAwait(false);
        var best = Best(hits, target);
        if (best is null && target.OriginalTitle is { Length: > 0 } original && original != target.Title)
        {
            best = Best(await client.SearchMediaAsync(original, ct).ConfigureAwait(false), target);
        }

        if (best is null) return [];

        var first = await client.SeasonAsync(best.SeasonId, ct).ConfigureAwait(false);
        if (target.Kind == MediaKind.Movie)
        {
            return Candidate(first, null, target) is { } movie ? [movie] : [];
        }

        // B站 lists a series' other seasons next to it, but also whole franchises under one roof (喜羊羊:
        // 经典版, 羊村守护者, 异国破晓…). Only generic labels count as seasons: "第二季", "TV", "第一季(中配)".
        // The matched season itself serves when it is the wanted number; others must say their number.
        var wanted = target.Season ?? 1;
        var own = ParseLabel(first.SeasonTitle);
        var refs = first.Seasons
            .Where(r => r.SeasonId != first.SeasonId)
            .Select(r => (Ref: r, Label: ParseLabel(r.Title)))
            .Where(x => !x.Label.Special && IsGenericLabel(x.Ref.Title))
            .ToList();

        var picks = new List<(BiliSeasonRef Ref, string? Dub)>();
        if ((own.Number ?? 1) == wanted && !own.Special)
        {
            picks.Add((new BiliSeasonRef(first.SeasonId, first.SeasonTitle), own.Dub));
        }
        else if (refs.FirstOrDefault(x => x.Label.Dub is null && (x.Label.Number ?? (wanted == 1 ? 1 : 0)) == wanted) is { Ref: { } other })
        {
            picks.Add((other, null));
        }

        if (picks.Count == 0) return [];
        picks.AddRange(refs.Where(x => x.Label.Dub is not null && (x.Label.Number ?? 1) == wanted).Take(3).Select(x => (x.Ref, x.Label.Dub)));

        var results = new List<SourceCandidate>(picks.Count);
        foreach (var (seasonRef, dub) in picks)
        {
            var season = seasonRef.SeasonId == first.SeasonId ? first : await client.SeasonAsync(seasonRef.SeasonId, ct).ConfigureAwait(false);
            if (season.SeasonType == 2) continue; // a film of the series (剧场总集篇…), not this season
            if (Candidate(season, dub, target) is { } candidate && results.All(c => c.Site.Name != candidate.Site.Name))
            {
                results.Add(candidate);
            }
        }

        return results;
    }

    /// <summary>The hit that is this title: same name (ignoring season and dub suffixes), right kind, close year.</summary>
    private static BiliMediaHit? Best(IReadOnlyList<BiliMediaHit> hits, SourceTarget target)
    {
        var names = new[] { target.Title, target.OriginalTitle }.Select(BaseName).Where(n => n.Length > 0).ToHashSet();
        return hits
            .Where(h => target.Kind == MediaKind.Movie ? h.SeasonType is 2 or 3 : h.SeasonType is 1 or 3 or 4 or 5 or 7)
            .Where(h => names.Contains(BaseName(h.Title)) || (h.OriginalTitle is { } org && names.Contains(BaseName(org))))
            .Where(h => target.Kind != MediaKind.Movie || target.Year is null || h.Year is null || Math.Abs(h.Year.Value - target.Year.Value) <= 1)
            .OrderBy(h => ParseLabel(h.Title).Dub is null ? 0 : 1)
            .ThenByDescending(h => h.EpisodeCount)
            .FirstOrDefault();
    }

    /// <summary>"间谍过家家 第二季 中配版" → "间谍过家家": the name to look the series up by elsewhere (TMDB).</summary>
    public static string SeriesName(string title) => Suffixes().Replace(title, " ").Trim();

    /// <summary>A season as a playable source on its own (a B站 title TMDB does not know).</summary>
    public static SourceCandidate? CandidateFor(BiliSeason season) =>
        Candidate(season, null, new SourceTarget(season.Title, null, season.Year, season.SeasonType == 2 ? MediaKind.Movie : MediaKind.Tv, null, null, []));

    private static SourceCandidate? Candidate(BiliSeason season, string? dub, SourceTarget target)
    {
        var main = season.Episodes.Where(e => e.Cid > 0 && e.Badge != "预告").ToArray();
        if (target.Kind == MediaKind.Movie)
        {
            main = main.Where(e => e.Title == "正片").Concat(main).Take(1).ToArray();
        }

        if (main.Length == 0) return null;

        var episodes = main.Select((e, i) => new PlayEpisode(
            i,
            target.Kind == MediaKind.Movie ? "正片" : e.Title.Length > 0 ? e.Title : $"第 {e.Number} 话",
            $"{Scheme}{e.EpId}/{e.Cid}/{e.Aid}/{season.SeasonId}/{season.SeasonType}",
            e.IsFree ? null : e.Badge is "会员" or null ? "会员" : e.Badge)).ToArray();

        var locked = episodes.Count(e => e.Badge is not null);
        var remarks = locked == 0 ? "免费" : locked == episodes.Length ? "大会员" : $"{locked} 集需大会员";
        var site = new SourceSite(SiteKey, dub is null ? "B站正版" : $"B站正版 · {dub}", "");
        return new SourceCandidate(site, season.SeasonId.ToString(CultureInfo.InvariantCulture), season.Title, season.Year,
            season.SeasonType switch { 1 => "番剧", 2 => "电影", 3 => "纪录片", 4 => "国创", 5 => "电视剧", 7 => "综艺", _ => null },
            remarks, [new PlayLine(dub ?? "原版", episodes)], 1000);
    }

    /// <summary>"第二季" → 2, "TV" / "正片" → none (the first), "第一季(中配)" → 1 + 中配, "剧场版…" / OVA → special.</summary>
    public static (int? Number, string? Dub, bool Special) ParseLabel(string text)
    {
        string? dub = null;
        var d = Dub().Match(text);
        if (d.Success) dub = d.Groups["dub"].Value + "配";
        if (Special().IsMatch(text)) return (null, dub, true);

        var m = SeasonNumber().Match(text);
        if (!m.Success) return (null, dub, false);
        var raw = m.Groups["n"].Value;
        var number = int.TryParse(raw, out var n) ? n : ChineseNumber(raw);
        return (number > 0 ? number : null, dub, false);
    }

    /// <summary>"第二季", "TV", "正片", "第一季(中配)", "Season 2": a season label rather than a title of its own.</summary>
    public static bool IsGenericLabel(string text)
    {
        var rest = Suffixes().Replace(text, "").Trim();
        return rest.Length == 0 || rest.Equals("TV", StringComparison.OrdinalIgnoreCase) || rest is "正片" or "TV版" or "原版";
    }

    /// <summary>
    /// The series name without B站's season and dub suffixes, normalised for comparison. A sequel number stays:
    /// "罗小黑战记 2" and "罗小黑战记2" are the same film, "罗小黑战记" is another.
    /// </summary>
    public static string BaseName(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        var t = Suffixes().Replace(title, " ");
        return SourceMatcher.Normalize(t);
    }

    private static int ChineseNumber(string s)
    {
        const string digits = "零一二三四五六七八九";
        if (s == "十") return 10;
        if (s.StartsWith('十')) return 10 + digits.IndexOf(s[1]);
        if (s.Length == 2 && s[1] == '十') return digits.IndexOf(s[0]) * 10;
        if (s.Length == 3 && s[1] == '十') return digits.IndexOf(s[0]) * 10 + digits.IndexOf(s[2]);
        return s.Length == 1 ? digits.IndexOf(s[0]) : 0;
    }

    [GeneratedRegex(@"第\s*(?<n>\d+|[一二三四五六七八九十]+)\s*季|Season\s*(?<n>\d+)|S(?<n>\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonNumber();

    [GeneratedRegex(@"[（(]?(?<dub>[中粤日英国])配版?[）)]?")]
    private static partial Regex Dub();

    [GeneratedRegex(@"剧场版|电影版|OVA|OAD|特别篇|总集篇|SP\b|番外", RegexOptions.IgnoreCase)]
    private static partial Regex Special();

    [GeneratedRegex(@"第\s*(\d+|[一二三四五六七八九十]+)\s*[季部期]|Season\s*\d+|[（(]?[中粤日英国]配版?[）)]?|[（(][^）)]*(?:地区|版)[）)]", RegexOptions.IgnoreCase)]
    private static partial Regex Suffixes();
}

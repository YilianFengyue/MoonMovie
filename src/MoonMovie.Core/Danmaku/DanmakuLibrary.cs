using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MoonMovie.Core.Configuration;
using MoonMovie.Core.Models;
using MoonMovie.Core.Settings;

namespace MoonMovie.Core.Danmaku;

/// <summary>What the player asks for: a title and an episode (zero-based, as in the source's episode list).</summary>
public sealed record DanmakuRequest(string MediaKey, string Title, int? Year, MediaKind Kind, int? Season, int EpisodeIndex);

/// <param name="Raw">Unfiltered comments, kept so block words and merging can be re-applied live.</param>
public sealed record DanmakuTrack(DanmakuMatch Match, IReadOnlyList<DanmakuComment> Comments, IReadOnlyList<DanmakuComment> Raw);

public sealed record DanmakuChoice(long AnimeId, string Title);

/// <summary>
/// Finds and filters the comment track for an episode. Automatic matching goes through the server; when the user
/// picks a different show it is remembered per title + season, so the following episodes follow that choice.
/// </summary>
public sealed class DanmakuLibrary(DanmakuClient client, SettingsStore settings)
{
    private readonly string _choicesPath = Path.Combine(AppPaths.Data, "danmaku-choices.json");
    private Dictionary<string, DanmakuChoice>? _choices;

    public DanmakuClient Client => client;

    public async Task<DanmakuTrack?> LoadAsync(DanmakuRequest request, CancellationToken ct = default)
    {
        DanmakuMatch? match = null;
        if (Choice(request) is { } choice)
        {
            match = await EpisodeOfAsync(choice, request.EpisodeIndex, ct).ConfigureAwait(false);
        }

        match ??= await client.MatchAsync(request.Title, request.Year, request.Kind, request.Season,
            request.EpisodeIndex + 1, ct).ConfigureAwait(false);
        if (match is null) return null;

        return await LoadTrackAsync(match, ct).ConfigureAwait(false);
    }

    /// <summary>The user picked another show: remember it and load the same episode from it.</summary>
    public async Task<DanmakuTrack?> ChooseAsync(DanmakuRequest request, DanmakuAnime anime, CancellationToken ct = default)
    {
        var choice = new DanmakuChoice(anime.AnimeId, anime.Title);
        Remember(request, choice);
        var match = await EpisodeOfAsync(choice, request.EpisodeIndex, ct).ConfigureAwait(false);
        return match is null ? null : await LoadTrackAsync(match, ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<DanmakuAnime>> CandidatesAsync(string keyword, CancellationToken ct = default) =>
        client.SearchAsync(keyword, ct);

    /// <summary>Re-applies block words and duplicate merging (after a settings change) to raw comments.</summary>
    public IReadOnlyList<DanmakuComment> Filter(IReadOnlyList<DanmakuComment> raw)
    {
        var s = settings.Current.Danmaku;
        var blockers = ParseBlockWords(s.BlockWords);
        var lastSeen = s.MergeDuplicates ? new Dictionary<string, double>(StringComparer.Ordinal) : null;
        var result = new List<DanmakuComment>(raw.Count);

        foreach (var original in raw)
        {
            // Platform emoticon codes ("[喜欢]", "[6周年]") have no meaning outside their own player.
            var text = Emoticon.Replace(Invisible.Replace(System.Net.WebUtility.HtmlDecode(original.Text), string.Empty), string.Empty).Trim();

            // The server appends likes ("♡14") and merged repeats ("x3"): keep them as weight, not as text.
            var weight = original.Weight;
            for (var m = Annotation.Match(text); m.Success; m = Annotation.Match(text))
            {
                weight += int.TryParse(m.Groups[1].Value, out var n) ? n : 0;
                text = text[..m.Index].TrimEnd();
            }

            if (text.Length == 0 || Spam.IsMatch(text)) continue;
            var c = text == original.Text && weight == original.Weight ? original : original with { Text = text, Weight = weight };

            if (blockers.Any(b => b(c.Text))) continue;
            if (lastSeen is not null)
            {
                var key = Normalize(c.Text);
                if (lastSeen.TryGetValue(key, out var t) && c.Time - t < 8) continue;
                lastSeen[key] = c.Time;
            }

            if (s.Density == DanmakuDensity.Smart && IsLowValue(c.Text)) continue;
            result.Add(c);
        }

        return Thin(result, s);
    }

    /// <summary>
    /// Caps comments per second of video. When a second is over the cap, the ones kept are the most "worth
    /// reading": readable length, and phrases many viewers typed (the crowd reacting) over one-off noise.
    /// </summary>
    private static IReadOnlyList<DanmakuComment> Thin(List<DanmakuComment> comments, DanmakuSettings s)
    {
        var cap = s.Density switch
        {
            DanmakuDensity.Low => 3,
            DanmakuDensity.Medium => 6,
            DanmakuDensity.High => 12,
            DanmakuDensity.All => int.MaxValue,
            // Smart: roughly what the chosen display area can show without lanes overflowing.
            _ => (int)Math.Round(4 + 8 * Math.Clamp(s.Area, 0.25, 1)),
        };
        if (cap == int.MaxValue) return comments;

        var popularity = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var c in comments)
        {
            var key = Normalize(c.Text);
            popularity[key] = popularity.GetValueOrDefault(key) + 1;
        }

        double Score(DanmakuComment c)
        {
            var length = c.Text.Length;
            var readable = length is >= 4 and <= 24 ? 2.0 : length is >= 2 and <= 36 ? 1.0 : 0.0;
            var crowd = Math.Log2(popularity.GetValueOrDefault(Normalize(c.Text), 1));
            var liked = Math.Log2(1 + c.Weight) * 0.8;
            var pinned = c.Mode == DanmakuMode.Scroll ? 0 : 0.5; // pinned comments are rarer and usually deliberate
            return readable + crowd + liked + pinned;
        }

        var result = new List<DanmakuComment>(comments.Count);
        foreach (var second in comments.GroupBy(c => (long)Math.Floor(c.Time)))
        {
            if (second.Count() <= cap)
            {
                result.AddRange(second);
                continue;
            }

            result.AddRange(second.OrderByDescending(Score).Take(cap).OrderBy(c => c.Time));
        }

        return result;
    }

    private static readonly Regex LowValue = new(
        @"^(\d+|([A-Za-z0-9])\2{2,}|[\p{P}\p{S}\s]+|\d{4}[.\-/年]\d{1,2}([.\-/月]\d{1,2}日?)?.{0,6}|\d{1,2}:\d{2}(:\d{2})?|.{0,4}(打卡|签到|前排|报道|报到|留念|到此一游|[一二三四五六七八九十\d]+刷).{0,4}|(第一|沙发|来了+|来啦|我来了|空降))$",
        RegexOptions.Compiled);

    /// <summary>Check-ins, bare dates and timestamps, lone digits or symbols: noise in a dense second.</summary>
    private static bool IsLowValue(string text) => text.Length <= 1 || LowValue.IsMatch(text);

    private async Task<DanmakuTrack> LoadTrackAsync(DanmakuMatch match, CancellationToken ct)
    {
        var raw = await client.CommentsAsync(match.EpisodeId, ct).ConfigureAwait(false);
        return new DanmakuTrack(match, Filter(raw), raw);
    }

    private async Task<DanmakuMatch?> EpisodeOfAsync(DanmakuChoice choice, int episodeIndex, CancellationToken ct)
    {
        var episodes = await client.EpisodesAsync(choice.AnimeId, ct).ConfigureAwait(false);
        if (episodes.Count == 0) return null;

        var episode = episodes.FirstOrDefault(e => e.Number == episodeIndex + 1)
                      ?? episodes[Math.Clamp(episodeIndex, 0, episodes.Count - 1)];
        return new DanmakuMatch(episode.EpisodeId, choice.Title, episode.Title, choice.AnimeId);
    }

    // ----- Remembered choices ------------------------------------------------------------------------

    private static string ChoiceKey(DanmakuRequest r) => $"{r.MediaKey}|{r.Season ?? 0}";

    private DanmakuChoice? Choice(DanmakuRequest request)
    {
        _choices ??= LoadChoices();
        return _choices.TryGetValue(ChoiceKey(request), out var choice) ? choice : null;
    }

    private void Remember(DanmakuRequest request, DanmakuChoice choice)
    {
        _choices ??= LoadChoices();
        _choices[ChoiceKey(request)] = choice;
        try
        {
            File.WriteAllText(_choicesPath, JsonSerializer.Serialize(_choices, DanmakuChoicesJsonContext.Default.DictionaryStringDanmakuChoice));
        }
        catch (IOException)
        {
        }
    }

    private Dictionary<string, DanmakuChoice> LoadChoices()
    {
        try
        {
            return File.Exists(_choicesPath)
                ? JsonSerializer.Deserialize(File.ReadAllText(_choicesPath), DanmakuChoicesJsonContext.Default.DictionaryStringDanmakuChoice) ?? []
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return [];
        }
    }

    // ----- Filtering -----------------------------------------------------------------------------------

    private static List<Func<string, bool>> ParseBlockWords(string? text)
    {
        var list = new List<Func<string, bool>>();
        if (string.IsNullOrWhiteSpace(text)) return list;

        foreach (var raw in text.Split([' ', ',', '，', '\n', '\r', '\t', ';', '；'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.Length > 2 && raw[0] == '/' && raw[^1] == '/')
            {
                try
                {
                    var regex = new Regex(raw[1..^1], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(20));
                    list.Add(t =>
                    {
                        try { return regex.IsMatch(t); }
                        catch (RegexMatchTimeoutException) { return false; }
                    });
                }
                catch (ArgumentException)
                {
                    // Not a valid pattern: treat it as plain text.
                    var word = raw;
                    list.Add(t => t.Contains(word, StringComparison.OrdinalIgnoreCase));
                }
            }
            else
            {
                var word = raw;
                list.Add(t => t.Contains(word, StringComparison.OrdinalIgnoreCase));
            }
        }

        return list;
    }

    // Zero-width spaces/joiners and emoji variation selectors: invisible, but they defeat every text rule.
    private static readonly Regex Invisible = new(@"[​-‏⁠︎️]", RegexOptions.Compiled);

    // Always dropped: gambling / betting ads, links and contact bait, and script-injection junk.
    private static readonly Regex Spam = new(
        @"(提款|到账|官方直营|以小博大|博彩|彩票|娱乐城|百家乐|棋牌|下注|充值返|加微|加v|vx[:：]|qq群|https?://|www\.|\.com\b|<\s*(img|script|iframe)|onerror|javascript:|document\.cookie)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Annotation = new(@"\s*(?:[♡❤♥]\s*|[x×X]\s?)(\d{1,6})$", RegexOptions.Compiled);

    private static readonly Regex Emoticon = new(@"\[[^\[\]\s]{1,8}\]", RegexOptions.Compiled);

    /// <summary>"哈哈哈哈哈" and "哈哈哈" count as the same comment.</summary>
    private static string Normalize(string text)
    {
        var trimmed = text.Trim().ToLowerInvariant();
        return Regex.Replace(trimmed, @"(.)\1{2,}", "$1$1");
    }
}

[JsonSerializable(typeof(Dictionary<string, DanmakuChoice>))]
internal sealed partial class DanmakuChoicesJsonContext : JsonSerializerContext;

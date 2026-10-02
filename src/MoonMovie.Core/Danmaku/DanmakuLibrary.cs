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
            var text = Emoticon.Replace(original.Text, string.Empty).Trim();
            if (text.Length == 0) continue;
            var c = text.Length == original.Text.Length ? original : original with { Text = text };

            if (blockers.Any(b => b(c.Text))) continue;
            if (lastSeen is not null)
            {
                var key = Normalize(c.Text);
                if (lastSeen.TryGetValue(key, out var t) && c.Time - t < 8) continue;
                lastSeen[key] = c.Time;
            }

            result.Add(c);
        }

        return result;
    }

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

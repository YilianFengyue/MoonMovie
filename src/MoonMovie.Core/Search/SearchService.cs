using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MoonMovie.Core.Configuration;
using MoonMovie.Core.Models;
using MoonMovie.Core.Sources;
using MoonMovie.Core.Tmdb;

namespace MoonMovie.Core.Search;

/// <summary>What the user typed, split into a keyword and the hints we can act on.</summary>
public sealed record SearchQuery(string Raw, string Keyword, int? Year, int? Season)
{
    public bool IsEmpty => Keyword.Length == 0;
}

public sealed record PersonResult(int Id, string Name, string? ProfilePath, string? Department, double Popularity, IReadOnlyList<MediaItem> KnownFor);

/// <param name="Person">Set when the query is best read as a person ("诺兰"): their work leads the results.</param>
public sealed record SearchResults(SearchQuery Query, IReadOnlyList<MediaItem> Media, IReadOnlyList<PersonResult> People, PersonResult? Person = null);

public sealed partial class SearchService(TmdbClient tmdb)
{
    private const int HistoryLimit = 20;
    private readonly string _historyPath = Path.Combine(AppPaths.Data, "search-history.json");
    private List<string>? _history;

    /// <summary>"庆余年 第二季" → keyword 庆余年, season 2; "Interstellar (2014)" → keyword Interstellar, year 2014.</summary>
    public static SearchQuery Parse(string input)
    {
        var text = input.Normalize(System.Text.NormalizationForm.FormKC).Trim();
        int? year = null;
        int? season = null;

        var y = YearPattern().Match(text);
        if (y.Success)
        {
            year = int.Parse(y.Groups[1].Value);
            text = text.Remove(y.Index, y.Length);
        }

        var s = SeasonPattern().Match(text);
        if (s.Success)
        {
            var (_, parsed) = SourceMatcher.SplitSeason(SourceMatcher.Normalize(s.Value));
            season = parsed ?? (int.TryParse(s.Groups["n"].Value, out var n) ? n : null);
            text = text.Remove(s.Index, s.Length);
        }

        var keyword = WhitespaceRun().Replace(text, " ").Trim(' ', '-', '·', ':', '：', '(', ')', '（', '）');

        // "2012" is a title, not a year filter.
        return keyword.Length == 0
            ? new SearchQuery(input.Trim(), input.Trim(), null, null)
            : new SearchQuery(input.Trim(), keyword, year, season);
    }

    public async Task<IReadOnlyList<MediaItem>> SuggestAsync(string input, CancellationToken ct)
    {
        var query = Parse(input);
        if (query.IsEmpty) return [];
        var (media, _) = await tmdb.SearchMultiAsync(query.Keyword, 1, ct).ConfigureAwait(false);
        return Rank(media, query).Take(8).ToArray();
    }

    public async Task<SearchResults> SearchAsync(string input, CancellationToken ct)
    {
        var query = Parse(input);
        if (query.IsEmpty) return new SearchResults(query, [], []);

        var first = tmdb.SearchMultiAsync(query.Keyword, 1, ct);
        var second = tmdb.SearchMultiAsync(query.Keyword, 2, ct);
        await Task.WhenAll(first, second).ConfigureAwait(false);

        var media = first.Result.Media.Concat(second.Result.Media)
            .DistinctBy(m => m.MediaKey)
            .ToArray();
        var people = first.Result.People.Concat(second.Result.People)
            .DistinctBy(p => p.Id)
            .Where(p => p.ProfilePath is not null)
            .OrderByDescending(p => p.Popularity)
            .Take(16)
            .ToArray();

        var ranked = Rank(media, query).ToArray();

        // Person intent: the most popular matching person, and no title that matches exactly.
        var wanted = SourceMatcher.Normalize(query.Keyword);
        var person = people.FirstOrDefault(p => SourceMatcher.Normalize(p.Name).Contains(wanted, StringComparison.Ordinal));
        var exactTitle = ranked.Any(m => SourceMatcher.Normalize(m.Title) == wanted || SourceMatcher.Normalize(m.OriginalTitle) == wanted);
        if (person is not null && !exactTitle && person.KnownFor.Count > 0)
        {
            var work = person.KnownFor.OrderByDescending(m => m.VoteCount).ToArray();
            ranked = work.Concat(ranked).DistinctBy(m => m.MediaKey).ToArray();
            people = people.OrderByDescending(p => p == person).ToArray();
            return new SearchResults(query, ranked, people, person);
        }

        // Otherwise a person's work still belongs in the grid, after the titles.
        var knownFor = people.Take(2).SelectMany(p => p.KnownFor);
        return new SearchResults(query, ranked.Concat(knownFor).DistinctBy(m => m.MediaKey).ToArray(), people);
    }

    /// <summary>Exact title matches first (either language), then year agreement, then popularity signal.</summary>
    private static IEnumerable<MediaItem> Rank(IEnumerable<MediaItem> items, SearchQuery query)
    {
        var wanted = SourceMatcher.Normalize(query.Keyword);
        return items
            .Select((item, order) => (item, score: Score(item, wanted, query.Year) - order * 0.5))
            .OrderByDescending(x => x.score)
            .Select(x => x.item);
    }

    private static double Score(MediaItem item, string wanted, int? year)
    {
        var title = SourceMatcher.Normalize(item.Title);
        var original = SourceMatcher.Normalize(item.OriginalTitle);
        double score = 0;

        if (title == wanted || original == wanted) score += 100;
        else if (title.StartsWith(wanted, StringComparison.Ordinal) || original.StartsWith(wanted, StringComparison.Ordinal)) score += 45;
        else if (title.Contains(wanted, StringComparison.Ordinal) || original.Contains(wanted, StringComparison.Ordinal)) score += 25;

        if (year is { } y && item.Year is { } iy) score += iy == y ? 60 : Math.Abs(iy - y) == 1 ? 15 : -30;
        score += Math.Log10(1 + item.VoteCount) * 8;
        if (item.PosterPath is null) score -= 40;
        return score;
    }

    // ----- History ------------------------------------------------------------------------------------

    public IReadOnlyList<string> History => _history ??= LoadHistory();

    public void Remember(string query)
    {
        query = query.Trim();
        if (query.Length == 0) return;
        var list = _history ??= LoadHistory();
        list.RemoveAll(q => string.Equals(q, query, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, query);
        if (list.Count > HistoryLimit) list.RemoveRange(HistoryLimit, list.Count - HistoryLimit);
        File.WriteAllText(_historyPath, JsonSerializer.Serialize(list, SearchJsonContext.Default.ListString));
    }

    public void Forget(string query)
    {
        var list = _history ??= LoadHistory();
        if (list.RemoveAll(q => string.Equals(q, query, StringComparison.OrdinalIgnoreCase)) == 0) return;
        File.WriteAllText(_historyPath, JsonSerializer.Serialize(list, SearchJsonContext.Default.ListString));
    }

    public void ClearHistory()
    {
        _history = [];
        File.WriteAllText(_historyPath, "[]");
    }

    private List<string> LoadHistory()
    {
        try
        {
            return File.Exists(_historyPath)
                ? JsonSerializer.Deserialize(File.ReadAllText(_historyPath), SearchJsonContext.Default.ListString) ?? []
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    [GeneratedRegex(@"[\(（]?(?<!\d)((?:19|20)\d{2})(?!\d)[\)）]?")]
    private static partial Regex YearPattern();

    [GeneratedRegex(@"第\s*(?<n>[0-9一二三四五六七八九十]+)\s*[季部]|\bs(?:eason)?\s*(?<n>\d{1,2})\b", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();
}

[JsonSerializable(typeof(List<string>))]
internal sealed partial class SearchJsonContext : JsonSerializerContext;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MoonMovie.Core.Configuration;
using MoonMovie.Core.Local;
using MoonMovie.Core.Models;

namespace MoonMovie.Core.Douban;

public sealed record DoubanRating(string Id, double Value, long Count)
{
    public string Url => $"https://movie.douban.com/subject/{Id}/";
}

/// <summary>
/// 豆瓣评分 next to TMDB's. Douban has no open API, so this sticks to the two lookups that work without an account
/// and asks once per title (results cached for a week, misses for two days): the IMDb id from TMDB first (exact),
/// then a title search (Chinese series often have no IMDb id; Douban also rates every season separately).
/// Any failure simply means no Douban rating is shown.
/// </summary>
public sealed partial class DoubanClient
{
    // The long-standing public key of Douban's legacy v2 API, still answering IMDb lookups.
    private const string LegacyApiKey = "0ab215a8b1977939201640fa14c66bab";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Safari/537.36";

    private static readonly TimeSpan HitTtl = TimeSpan.FromDays(7);
    private static readonly TimeSpan MissTtl = TimeSpan.FromDays(2);

    private readonly HttpClient _http;
    private readonly string _path = Path.Combine(AppPaths.Cache, "douban.json");
    private readonly object _gate = new();
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private Dictionary<string, DoubanCacheEntry>? _cache;
    private DateTimeOffset _searchBlockedUntil;

    public DoubanClient(HttpClient http)
    {
        _http = http;
    }

    /// <param name="season">For series: the season on screen (Douban has one entry per season).</param>
    /// <param name="seasonYear">That season's first air year, to tell same-named entries apart.</param>
    public async Task<DoubanRating?> RatingAsync(MediaItem item, int? season, int? seasonYear, string? imdbId, CancellationToken ct = default)
    {
        var key = $"{item.MediaKey}|{season ?? 0}";
        lock (_gate)
        {
            if (Cache.TryGetValue(key, out var hit) && DateTimeOffset.Now - hit.At < (hit.Id is null ? MissTtl : HitTtl))
            {
                return hit.Id is null ? null : new DoubanRating(hit.Id, hit.Value, hit.Count);
            }
        }

        DoubanRating? rating = null;
        var failed = false;
        await _requestGate.WaitAsync(ct).ConfigureAwait(false); // one at a time: Douban blocks bursts quickly
        try
        {
            var firstSeason = item.Kind == MediaKind.Movie || season is null or 1;
            if (firstSeason && !string.IsNullOrEmpty(imdbId)) rating = await ByImdbAsync(imdbId, ct).ConfigureAwait(false);
            rating ??= await ByTitleAsync(item, season, seasonYear, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException
                                       or KeyNotFoundException)
        {
            failed = !ct.IsCancellationRequested; // blocked or offline: do not remember a miss
            if (ct.IsCancellationRequested) throw;
        }
        finally
        {
            _requestGate.Release();
        }

        if (!failed)
        {
            lock (_gate)
            {
                Cache[key] = new DoubanCacheEntry(rating?.Id, rating?.Value ?? 0, rating?.Count ?? 0, DateTimeOffset.Now);
                Save();
            }
        }

        return rating;
    }

    private async Task<DoubanRating?> ByImdbAsync(string imdbId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.douban.com/v2/movie/imdb/{imdbId}")
        {
            Content = new FormUrlEncodedContent([new("apikey", LegacyApiKey)]),
        };
        request.Headers.UserAgent.ParseAdd(UserAgent);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = doc.RootElement;
        if (!root.TryGetProperty("rating", out var rating) || Str(root, "alt") is not { } alt) return null;
        var id = TrailingId().Match(alt).Value;
        var value = double.TryParse(Str(rating, "average"), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        var count = rating.TryGetProperty("numRaters", out var n) && n.TryGetInt64(out var c) ? c : 0;
        return id.Length > 0 && value > 0 ? new DoubanRating(id, value, count) : null;
    }

    private async Task<DoubanRating?> ByTitleAsync(MediaItem item, int? season, int? seasonYear, CancellationToken ct)
    {
        if (DateTimeOffset.Now < _searchBlockedUntil) throw new HttpRequestException("豆瓣搜索暂时不可用");
        var tv = item.Kind == MediaKind.Tv;
        var query = tv && season is > 1 ? $"{item.Title} 第{Chinese(season.Value)}季" : item.Title;
        var url = $"https://m.douban.com/rexxar/api/v2/search/subjects?q={Uri.EscapeDataString(query)}&type={(tv ? "tv" : "movie")}";
        using var doc = await RexxarAsync(url, ct).ConfigureAwait(false);
        if (doc is null || !doc.RootElement.TryGetProperty("subjects", out var subjects) || !subjects.TryGetProperty("items", out var items)) return null;

        var wantYear = season is > 1 ? seasonYear : item.Year;
        var wanted = new[] { query, item.Title, item.OriginalTitle }.Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => LocalNameParser.Normalize(t!)).ToHashSet();

        JsonElement? best = null;
        var bestScore = 0;
        foreach (var entry in items.EnumerateArray())
        {
            if (!entry.TryGetProperty("target", out var target) || Str(target, "id") is null) continue;
            var title = LocalNameParser.Normalize(Str(target, "title") ?? "");
            var score = wanted.Contains(title) ? 2 : wanted.Any(w => title.StartsWith(w, StringComparison.Ordinal)) ? 1 : 0;
            if (score == 0) continue;
            if (wantYear is { } y && int.TryParse(Str(target, "year"), out var ty)) score += ty == y ? 2 : Math.Abs(ty - y) <= 1 ? 1 : -3;
            if (score > bestScore)
            {
                bestScore = score;
                best = target.Clone();
            }
        }

        if (best is not { } match || bestScore < 2) return null;
        var id = Str(match, "id")!;
        if (match.TryGetProperty("rating", out var r) && r.TryGetProperty("value", out var value) && value.TryGetDouble(out var rv) && rv > 0
            && r.TryGetProperty("count", out var count) && count.TryGetInt64(out var rc))
        {
            return new DoubanRating(id, rv, rc);
        }

        // Search results sometimes omit the count: the subject itself has it.
        using var detail = await RexxarAsync($"https://m.douban.com/rexxar/api/v2/{(tv ? "tv" : "movie")}/{id}", ct).ConfigureAwait(false);
        if (detail is null || !detail.RootElement.TryGetProperty("rating", out var dr) || dr.ValueKind != JsonValueKind.Object) return null;
        var dv = dr.TryGetProperty("value", out var x) && x.TryGetDouble(out var xv) ? xv : 0;
        var dc = dr.TryGetProperty("count", out var y2) && y2.TryGetInt64(out var yc) ? yc : 0;
        return dv > 0 ? new DoubanRating(id, dv, dc) : null;
    }

    /// <summary>Douban's mobile JSON API (wants a mobile Referer; answers HTML when it suspects a bot).</summary>
    private async Task<JsonDocument?> RexxarAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Referrer = new Uri("https://m.douban.com/movie/");
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (body.Length == 0 || body[0] != '{')
        {
            // Bot check: back off for a while instead of asking again on every detail page.
            _searchBlockedUntil = DateTimeOffset.Now.AddMinutes(30);
            throw new HttpRequestException("豆瓣拒绝了请求");
        }
        return JsonDocument.Parse(body);
    }

    private static string Chinese(int n) => n switch
    {
        1 => "一", 2 => "二", 3 => "三", 4 => "四", 5 => "五", 6 => "六", 7 => "七", 8 => "八", 9 => "九", 10 => "十",
        _ when n < 20 => "十" + Chinese(n - 10),
        _ => n.ToString(CultureInfo.InvariantCulture),
    };

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private Dictionary<string, DoubanCacheEntry> Cache => _cache ??= Load();

    private Dictionary<string, DoubanCacheEntry> Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize(File.ReadAllText(_path), DoubanJsonContext.Default.DictionaryStringDoubanCacheEntry) ?? []
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return [];
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(Cache, DoubanJsonContext.Default.DictionaryStringDoubanCacheEntry));
        }
        catch (IOException)
        {
        }
    }

    [GeneratedRegex(@"\d+(?=/?$)")]
    private static partial Regex TrailingId();
}

/// <summary>A cached lookup; a null id records "Douban has nothing" for a while.</summary>
public sealed record DoubanCacheEntry(string? Id, double Value, long Count, DateTimeOffset At);

[JsonSerializable(typeof(Dictionary<string, DoubanCacheEntry>))]
internal sealed partial class DoubanJsonContext : JsonSerializerContext;

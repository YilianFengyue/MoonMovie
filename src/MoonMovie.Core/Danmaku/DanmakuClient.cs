using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using MoonMovie.Core.Caching;
using MoonMovie.Core.Configuration;
using MoonMovie.Core.Models;
using MoonMovie.Core.Settings;

namespace MoonMovie.Core.Danmaku;

/// <summary>1 = scrolling, 4 = pinned bottom, 5 = pinned top (dandanplay numbering).</summary>
public enum DanmakuMode
{
    Scroll = 1,
    Bottom = 4,
    Top = 5,
}

public readonly record struct DanmakuComment(double Time, DanmakuMode Mode, uint Color, string Text);

public sealed record DanmakuAnime(long AnimeId, string Title, string? TypeDescription, int EpisodeCount, string? StartDate, string? Source);

public sealed record DanmakuEpisode(long EpisodeId, string Title, int? Number);

/// <summary>Which comment track is attached to what is playing.</summary>
public sealed record DanmakuMatch(long EpisodeId, string AnimeTitle, string EpisodeTitle, long? AnimeId = null);

/// <summary>
/// Client for a LogVar danmu_api server (dandanplay-compatible API). The server aggregates comments from the
/// big Chinese platforms; it can be slow (it fans out to them), so matches and comments are cached on disk.
/// </summary>
public sealed class DanmakuClient
{
    private static readonly TimeSpan MatchTtl = TimeSpan.FromDays(14);
    private static readonly TimeSpan CommentTtl = TimeSpan.FromHours(12);
    private static readonly TimeSpan SearchTtl = TimeSpan.FromHours(12);

    private readonly HttpClient _http;
    private readonly EnvFile _env;
    private readonly SettingsStore _settings;
    private readonly JsonDiskCache _cache = new(Directory.CreateDirectory(Path.Combine(AppPaths.Root, "cache", "danmaku")).FullName);

    public DanmakuClient(HttpClient http, EnvFile env, SettingsStore settings)
    {
        _http = http;
        _env = env;
        _settings = settings;
    }

    public bool IsConfigured => BaseUrl is not null;

    /// <summary>Server root including the token segment, without a trailing slash.</summary>
    private string? BaseUrl
    {
        get
        {
            var s = _settings.Current.Danmaku;
            var url = (string.IsNullOrWhiteSpace(s.ServerUrl) ? _env.Get("LOGVAR_BASE_URL") : s.ServerUrl)?.Trim().TrimEnd('/');
            if (string.IsNullOrEmpty(url)) return null;
            var token = (string.IsNullOrWhiteSpace(s.ServerUrl) && string.IsNullOrWhiteSpace(s.Token) ? _env.Get("LOGVAR_TOKEN") : s.Token)?.Trim();
            return string.IsNullOrEmpty(token) ? url : $"{url}/{token}";
        }
    }

    /// <summary>Checks the server answers at the configured address (and token).</summary>
    public async Task<(bool Ok, string Message)> PingAsync(CancellationToken ct = default)
    {
        if (BaseUrl is null) return (false, "未配置服务器地址");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            using var response = await _http.GetAsync(Url("/api/config"), timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (false, response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? "令牌不正确（401）"
                    : $"服务器返回 {(int)response.StatusCode}");
            }

            using var doc = JsonDocument.Parse(body);
            var version = Str(doc.RootElement, "version");
            return (true, version is null ? $"已连接 · {watch.ElapsedMilliseconds} ms" : $"已连接 · v{version} · {watch.ElapsedMilliseconds} ms");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (false, ex is TaskCanceledException ? "连接超时" : "无法连接服务器");
        }
    }

    /// <summary>
    /// Asks the server to pick the right show and episode. Series go as "片名.S02E03" and films as
    /// "片名.2014" — the file-name shape its matcher parses (title, year, season, episode).
    /// </summary>
    public async Task<DanmakuMatch?> MatchAsync(string title, int? year, MediaKind kind, int? season, int episode,
        CancellationToken ct = default)
    {
        var clean = title.Replace('.', ' ').Trim();
        // Each season is its own show on the Chinese platforms, so a series' first-air year would only mislead.
        var yearPart = kind == MediaKind.Movie && year is { } y ? $".{y}" : string.Empty;
        var fileName = kind == MediaKind.Tv
            ? $"{clean}{yearPart}.S{season ?? 1:00}E{episode:00}"
            : $"{clean}{yearPart}";

        var key = "match|" + fileName;
        var body = await CachedAsync(key, MatchTtl, async () =>
        {
            using var response = await _http.PostAsJsonAsync(Url("/api/v2/match"), new MatchRequest(fileName),
                DanmakuJsonContext.Default.MatchRequest, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }, cacheIf: b => b.Contains("\"isMatched\":true", StringComparison.Ordinal), ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (!root.TryGetProperty("isMatched", out var matched) || !matched.GetBoolean()) return null;
        if (!root.TryGetProperty("matches", out var matches) || matches.GetArrayLength() == 0) return null;

        var m = matches[0];
        return new DanmakuMatch(
            m.GetProperty("episodeId").GetInt64(),
            Str(m, "animeTitle") ?? title,
            Str(m, "episodeTitle") ?? string.Empty,
            m.TryGetProperty("animeId", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt64() : null);
    }

    public async Task<IReadOnlyList<DanmakuAnime>> SearchAsync(string keyword, CancellationToken ct = default)
    {
        var body = await CachedAsync("search|" + keyword, SearchTtl,
            () => _http.GetStringAsync(Url($"/api/v2/search/anime?keyword={Uri.EscapeDataString(keyword)}"), ct),
            cacheIf: b => b.Contains("\"animeId\"", StringComparison.Ordinal), ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("animes", out var animes)) return [];

        return animes.EnumerateArray()
            .Where(a => a.TryGetProperty("animeId", out var id) && id.ValueKind == JsonValueKind.Number)
            .Select(a => new DanmakuAnime(
                a.GetProperty("animeId").GetInt64(),
                Str(a, "animeTitle") ?? "",
                Str(a, "typeDescription"),
                a.TryGetProperty("episodeCount", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0,
                Str(a, "startDate"),
                Str(a, "source")))
            .ToArray();
    }

    public async Task<IReadOnlyList<DanmakuEpisode>> EpisodesAsync(long animeId, CancellationToken ct = default)
    {
        var body = await CachedAsync($"bangumi|{animeId}", SearchTtl,
            () => _http.GetStringAsync(Url($"/api/v2/bangumi/{animeId}"), ct),
            cacheIf: b => b.Contains("\"episodeId\"", StringComparison.Ordinal), ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("bangumi", out var bangumi)
            || !bangumi.TryGetProperty("episodes", out var episodes))
        {
            return [];
        }

        return episodes.EnumerateArray()
            .Select(e => new DanmakuEpisode(
                e.GetProperty("episodeId").GetInt64(),
                Str(e, "episodeTitle") ?? "",
                int.TryParse(Str(e, "episodeNumber"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null))
            .ToArray();
    }

    /// <summary>All comments of an episode, sorted by time.</summary>
    public async Task<IReadOnlyList<DanmakuComment>> CommentsAsync(long episodeId, CancellationToken ct = default)
    {
        var body = await CachedAsync($"comment|{episodeId}", CommentTtl,
            () => _http.GetStringAsync(Url($"/api/v2/comment/{episodeId}?format=json"), ct),
            cacheIf: b => b.Contains("\"comments\"", StringComparison.Ordinal), ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("comments", out var comments)) return [];

        var list = new List<DanmakuComment>(comments.GetArrayLength());
        foreach (var c in comments.EnumerateArray())
        {
            if (Str(c, "p") is not { } p || Str(c, "m") is not { Length: > 0 } text) continue;
            if (Parse(p, text) is { } comment) list.Add(comment);
        }

        list.Sort((a, b) => a.Time.CompareTo(b.Time));
        return list;
    }

    /// <summary>
    /// "time,mode,color,[source]" (this server), or the 8/9-field Bilibili layout "time,mode,size,color,…".
    /// </summary>
    internal static DanmakuComment? Parse(string p, string text)
    {
        var parts = p.Split(',');
        if (parts.Length < 3 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var time))
        {
            return null;
        }

        var mode = int.TryParse(parts[1], out var m) ? m : 1;
        var colorField = parts.Length >= 8 ? parts[3] : parts[2];
        var color = uint.TryParse(colorField, out var c) ? c & 0xFFFFFF : 0xFFFFFF;
        var kind = mode switch
        {
            4 => DanmakuMode.Bottom,
            5 => DanmakuMode.Top,
            _ => DanmakuMode.Scroll,
        };

        return new DanmakuComment(time, kind, color, text.Trim());
    }

    private string Url(string path) =>
        (BaseUrl ?? throw new InvalidOperationException("弹幕服务器未配置")) + path;

    private async Task<string> CachedAsync(string key, TimeSpan ttl, Func<Task<string>> fetch, Func<string, bool> cacheIf,
        CancellationToken ct)
    {
        var scopedKey = BaseUrl + "|" + key;
        if (await _cache.TryGetAsync(scopedKey, ct).ConfigureAwait(false) is { } hit && hit.IsFresh(ttl))
        {
            return hit.Body;
        }

        var body = await fetch().ConfigureAwait(false);
        if (cacheIf(body))
        {
            await _cache.SetAsync(scopedKey, body, CancellationToken.None).ConfigureAwait(false);
        }

        return body;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;
}

internal sealed record MatchRequest(string fileName);

[System.Text.Json.Serialization.JsonSerializable(typeof(MatchRequest))]
internal sealed partial class DanmakuJsonContext : System.Text.Json.Serialization.JsonSerializerContext;

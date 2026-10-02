using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MoonMovie.Core.Caching;
using MoonMovie.Core.Models;

namespace MoonMovie.Core.Tmdb;

public enum CacheMode
{
    /// <summary>Use cache inside TTL, otherwise network; falls back to stale cache on failure.</summary>
    Default,

    /// <summary>Return any cached copy immediately and refresh it in the background when stale.</summary>
    StaleWhileRevalidate,
}

public sealed class TmdbClient
{
    private static readonly TimeSpan ListTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ImagesTtl = TimeSpan.FromDays(7);
    private static readonly TimeSpan DetailTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan SearchTtl = TimeSpan.FromHours(6);

    private readonly HttpClient _http;
    private readonly TmdbOptions _options;
    private readonly JsonDiskCache _cache;
    private int _apiBaseIndex;

    public TmdbClient(HttpClient http, TmdbOptions options, JsonDiskCache cache)
    {
        _http = http;
        _options = options;
        _cache = cache;
        ImageRoot = options.ImageRoots[0];
    }

    public bool IsConfigured => _options.IsConfigured;

    /// <summary>Image root currently in use, without size segment.</summary>
    public string ImageRoot { get; private set; }

    public string? ImageUrl(string? path, string size) =>
        string.IsNullOrEmpty(path) ? null : $"{ImageRoot}/{size}{path}";

    /// <summary>Switch to the next configured image root (called by the image loader after repeated failures).</summary>
    public void FailoverImageRoot()
    {
        var roots = _options.ImageRoots;
        var i = roots.ToList().IndexOf(ImageRoot);
        ImageRoot = roots[(i + 1) % roots.Count];
    }

    public Task<IReadOnlyList<MediaItem>> TrendingAsync(string mediaType, string window, int page = 1,
        CacheMode mode = CacheMode.Default, CancellationToken ct = default) =>
        ListAsync($"trending/{mediaType}/{window}", null, page, KindFromSegment(mediaType), mode, ct);

    public Task<IReadOnlyList<MediaItem>> PopularAsync(MediaKind kind, int page = 1,
        CacheMode mode = CacheMode.Default, CancellationToken ct = default) =>
        ListAsync($"{Segment(kind)}/popular", null, page, kind, mode, ct);

    public Task<IReadOnlyList<MediaItem>> TopRatedAsync(MediaKind kind, int page = 1,
        CacheMode mode = CacheMode.Default, CancellationToken ct = default) =>
        ListAsync($"{Segment(kind)}/top_rated", null, page, kind, mode, ct);

    public Task<IReadOnlyList<MediaItem>> DiscoverAsync(MediaKind kind, IReadOnlyDictionary<string, string> filters,
        int page = 1, CacheMode mode = CacheMode.Default, CancellationToken ct = default) =>
        ListAsync($"discover/{Segment(kind)}", filters, page, kind, mode, ct);

    /// <summary>/search/multi split into titles and people.</summary>
    public async Task<(IReadOnlyList<MediaItem> Media, IReadOnlyList<Search.PersonResult> People)> SearchMultiAsync(
        string query, int page, CancellationToken ct = default)
    {
        var parameters = new Dictionary<string, string>
        {
            ["query"] = query,
            ["page"] = page.ToString(),
            ["include_adult"] = "false",
        };

        var result = await GetAsync("search/multi", parameters, TmdbJsonContext.Default.TmdbPageTmdbMediaDto,
            SearchTtl, CacheMode.Default, includeLanguage: true, ct).ConfigureAwait(false);
        if (result is null)
        {
            return ([], []);
        }

        var media = result.Results
            .Select(dto => MediaItem.FromDto(dto, null))
            .Where(m => m is not null)
            .Select(m => m!)
            .ToArray();

        var people = result.Results
            .Where(dto => dto.MediaType == "person" && !string.IsNullOrWhiteSpace(dto.Name))
            .Select(dto => new Search.PersonResult(
                dto.Id,
                dto.Name!,
                dto.ProfilePath,
                dto.KnownForDepartment,
                dto.Popularity,
                (dto.KnownFor ?? [])
                    .Select(k => MediaItem.FromDto(k, null))
                    .Where(m => m is not null)
                    .Select(m => m!)
                    .ToArray()))
            .ToArray();

        return (media, people);
    }

    /// <summary>/search/movie or /search/tv, optionally narrowed to a year (local file matching).</summary>
    public Task<IReadOnlyList<MediaItem>> SearchAsync(MediaKind kind, string query, int? year = null,
        CancellationToken ct = default)
    {
        var filters = new Dictionary<string, string> { ["query"] = query, ["include_adult"] = "false" };
        if (year is { } y) filters[kind == MediaKind.Movie ? "primary_release_year" : "first_air_date_year"] = y.ToString();
        return ListAsync($"search/{Segment(kind)}", filters, 1, kind, CacheMode.Default, ct);
    }

    /// <summary>Full detail with credits, recommendations and logos in a single request.</summary>
    public async Task<MediaDetail?> DetailAsync(MediaKind kind, int id, CancellationToken ct = default)
    {
        var query = new Dictionary<string, string>
        {
            ["append_to_response"] = kind == MediaKind.Movie
                ? "credits,recommendations,images"
                : "aggregate_credits,recommendations,images",
            ["include_image_language"] = "zh,en,null",
        };

        var dto = await GetAsync($"{Segment(kind)}/{id}", query, TmdbDetailJsonContext.Default.TmdbDetailsDto,
            DetailTtl, CacheMode.StaleWhileRevalidate, includeLanguage: true, ct).ConfigureAwait(false);
        return dto is null ? null : MediaDetail.FromDto(dto, kind);
    }

    public async Task<IReadOnlyList<EpisodeInfo>> SeasonAsync(int tvId, int season, CancellationToken ct = default)
    {
        var dto = await GetAsync($"tv/{tvId}/season/{season}", [], TmdbDetailJsonContext.Default.TmdbSeasonDto,
            DetailTtl, CacheMode.StaleWhileRevalidate, includeLanguage: true, ct).ConfigureAwait(false);

        return (dto?.Episodes ?? [])
            .Select(e => new EpisodeInfo(
                e.EpisodeNumber,
                string.IsNullOrWhiteSpace(e.Name) ? $"第 {e.EpisodeNumber} 集" : e.Name!.Trim(),
                string.IsNullOrWhiteSpace(e.Overview) ? null : e.Overview!.Trim(),
                e.StillPath,
                e.Runtime,
                e.AirDate))
            .ToArray();
    }

    /// <summary>Best transparent title logo (zh, then en), or null.</summary>
    public async Task<string?> LogoPathAsync(MediaKind kind, int id, CancellationToken ct = default)
    {
        var query = new Dictionary<string, string> { ["include_image_language"] = "zh,en,null" };
        var images = await GetAsync($"{Segment(kind)}/{id}/images", query, TmdbJsonContext.Default.TmdbImagesDto,
            ImagesTtl, CacheMode.StaleWhileRevalidate, includeLanguage: false, ct).ConfigureAwait(false);

        return images?.Logos?
            .Where(l => l.FilePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            .OrderBy(l => l.Language switch { "zh" => 0, "en" => 1, null => 2, _ => 3 })
            .ThenByDescending(l => l.VoteAverage)
            .Select(l => l.FilePath)
            .FirstOrDefault();
    }

    private async Task<IReadOnlyList<MediaItem>> ListAsync(string path, IReadOnlyDictionary<string, string>? filters,
        int page, MediaKind? fallbackKind, CacheMode mode, CancellationToken ct)
    {
        var query = new Dictionary<string, string> { ["page"] = page.ToString() };
        if (filters is not null)
        {
            foreach (var (k, v) in filters)
            {
                query[k] = v;
            }
        }

        var result = await GetAsync(path, query, TmdbJsonContext.Default.TmdbPageTmdbMediaDto, ListTtl, mode,
            includeLanguage: true, ct).ConfigureAwait(false);

        if (result is null)
        {
            return [];
        }

        return result.Results
            .Select(dto => MediaItem.FromDto(dto, fallbackKind))
            .Where(m => m is not null)
            .Select(m => m!)
            .ToArray();
    }

    private async Task<T?> GetAsync<T>(string path, Dictionary<string, string> query, JsonTypeInfo<T> typeInfo,
        TimeSpan ttl, CacheMode mode, bool includeLanguage, CancellationToken ct) where T : class
    {
        if (includeLanguage)
        {
            query["language"] = _options.Language;
        }

        var relative = path + "?" + string.Join("&", query.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));

        var cached = await _cache.TryGetAsync(relative, ct).ConfigureAwait(false);
        if (cached is { } hit && (hit.IsFresh(ttl) || mode == CacheMode.StaleWhileRevalidate))
        {
            var value = TryDeserialize(hit.Body, typeInfo);
            if (value is not null)
            {
                if (!hit.IsFresh(ttl))
                {
                    _ = RefreshAsync(relative);
                }

                return value;
            }
        }

        try
        {
            var body = await FetchAsync(relative, ct).ConfigureAwait(false);
            var value = TryDeserialize(body, typeInfo);
            if (value is not null)
            {
                await _cache.SetAsync(relative, body, CancellationToken.None).ConfigureAwait(false);
            }

            return value;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return cached is { } stale ? TryDeserialize(stale.Body, typeInfo) : null;
        }
    }

    private async Task RefreshAsync(string relative)
    {
        try
        {
            var body = await FetchAsync(relative, CancellationToken.None).ConfigureAwait(false);
            await _cache.SetAsync(relative, body).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Keep the stale copy; next call retries.
        }
    }

    /// <summary>Tries the active API base first, then the others; remembers the one that worked.</summary>
    private async Task<string> FetchAsync(string relative, CancellationToken ct)
    {
        if (!_options.IsConfigured)
        {
            throw new HttpRequestException("TMDB API key is not configured.");
        }

        var bases = _options.ApiBases;
        Exception? last = null;
        for (var attempt = 0; attempt < bases.Count; attempt++)
        {
            var index = (Volatile.Read(ref _apiBaseIndex) + attempt) % bases.Count;
            try
            {
                using var request = BuildRequest(bases[index], relative);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                Volatile.Write(ref _apiBaseIndex, index);
                return body;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                last = ex;
            }
        }

        throw last as HttpRequestException ?? new HttpRequestException("TMDB request failed.", last);
    }

    private HttpRequestMessage BuildRequest(string apiBase, string relative)
    {
        var url = new StringBuilder(apiBase).Append('/').Append(relative);
        var token = _options.ReadAccessToken;
        var key = _options.ApiKey;

        // A key that looks like a JWT is a v4 read token and must go in the Authorization header.
        if (string.IsNullOrEmpty(token) && key is not null && key.StartsWith("eyJ", StringComparison.Ordinal))
        {
            token = key;
            key = null;
        }

        if (!string.IsNullOrEmpty(key))
        {
            url.Append("&api_key=").Append(Uri.EscapeDataString(key));
        }

        var request = new HttpRequestMessage(HttpMethod.Get, url.ToString());
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    private static T? TryDeserialize<T>(string body, JsonTypeInfo<T> typeInfo) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(body, typeInfo);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Segment(MediaKind kind) => kind == MediaKind.Movie ? "movie" : "tv";

    private static MediaKind? KindFromSegment(string mediaType) => mediaType switch
    {
        "movie" => MediaKind.Movie,
        "tv" => MediaKind.Tv,
        _ => null,
    };
}

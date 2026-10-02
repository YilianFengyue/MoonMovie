using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MoonMovie.Core.Caching;
using MoonMovie.Core.Playback;

namespace MoonMovie.Playback;

/// <summary>
/// Loopback HTTP proxy between Media Foundation and resource-site CDNs.
/// <list type="bullet">
/// <item>Fetches directly (no system proxy) — mainland CDNs reject proxy exits, and MF would use the system proxy.</item>
/// <item>Rewrites HLS playlists so every variant/segment/key goes through here, stripping spliced ads.</item>
/// <item>Adds the Referer/User-Agent a CDN expects and passes Range through for seekable MP4.</item>
/// <item>Keeps HLS segments in a disk cache: re-watching and seeking back are instant, and the next episode's
/// opening segments can be fetched before it starts.</item>
/// </list>
/// </summary>
public sealed class MediaProxy(HttpClient direct, SegmentCache cache) : IAsyncDisposable
{
    private const string BrowserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Safari/537.36";

    private readonly SemaphoreSlim _startLock = new(1, 1);
    private WebApplication? _app;
    private string _origin = "";

    /// <summary>Raised when a playlist had ads removed: (segments, seconds).</summary>
    public event Action<int, double>? AdsRemoved;

    public async Task<Uri> PlaylistUriAsync(string url, string? referer = null)
    {
        await EnsureStartedAsync();
        return new Uri(Map("m3u8", url, referer));
    }

    public async Task<Uri> FileUriAsync(string url, string? referer = null)
    {
        await EnsureStartedAsync();
        return new Uri(Map("seg", url, referer));
    }

    private string Map(string route, string url, string? referer) =>
        $"{_origin}/{route}?u={Uri.EscapeDataString(url)}" + (referer is null ? "" : $"&r={Uri.EscapeDataString(referer)}");

    private async Task EnsureStartedAsync()
    {
        if (_app is not null) return;

        await _startLock.WaitAsync();
        try
        {
            if (_app is not null) return;

            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();

            app.MapGet("/m3u8", HandlePlaylistAsync);
            app.MapGet("/seg", HandleSegmentAsync);
            app.MapGet("/hseg", HandleCachedSegmentAsync);

            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            _origin = address.TrimEnd('/');
            _app = app;
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task HandlePlaylistAsync(HttpContext context)
    {
        var url = context.Request.Query["u"].ToString();
        var referer = context.Request.Query["r"].ToString();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var source))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var request = CreateUpstreamRequest(source, referer);
        using var response = await direct.SendAsync(request, context.RequestAborted);
        if (!response.IsSuccessStatusCode)
        {
            context.Response.StatusCode = (int)response.StatusCode;
            return;
        }

        var text = await response.Content.ReadAsStringAsync(context.RequestAborted);
        var baseUri = response.RequestMessage?.RequestUri ?? source; // after redirects
        var refererOrNull = string.IsNullOrEmpty(referer) ? null : referer;
        var result = HlsRewriter.Rewrite(
            text,
            baseUri,
            u => Map("m3u8", u.AbsoluteUri, refererOrNull),
            u => Map("hseg", u.AbsoluteUri, refererOrNull));

        if (result.RemovedSegments > 0)
        {
            AdsRemoved?.Invoke(result.RemovedSegments, result.RemovedSeconds);
        }

        context.Response.ContentType = "application/vnd.apple.mpegurl";
        context.Response.Headers.CacheControl = "no-cache";
        await context.Response.WriteAsync(result.Playlist, context.RequestAborted);
    }

    private async Task HandleSegmentAsync(HttpContext context)
    {
        var url = context.Request.Query["u"].ToString();
        var referer = context.Request.Query["r"].ToString();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var source))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var request = CreateUpstreamRequest(source, referer);
        if (context.Request.Headers.Range is { Count: > 0 } range
            && System.Net.Http.Headers.RangeHeaderValue.TryParse(range.ToString(), out var parsed))
        {
            request.Headers.Range = parsed;
        }

        using var response = await direct.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        context.Response.StatusCode = (int)response.StatusCode;

        var contentType = response.Content.Headers.ContentType?.MediaType;
        context.Response.ContentType = SegmentContentType(source, contentType);
        if (response.Content.Headers.ContentLength is { } length) context.Response.ContentLength = length;
        if (response.Content.Headers.ContentRange is { } contentRange) context.Response.Headers.ContentRange = contentRange.ToString();
        context.Response.Headers.AcceptRanges = "bytes";

        await using var body = await response.Content.ReadAsStreamAsync(context.RequestAborted);
        await body.CopyToAsync(context.Response.Body, 81920, context.RequestAborted);
    }

    /// <summary>
    /// HLS segments and keys: from the disk cache when present; otherwise streamed through while being written to
    /// it. Byte-range requests (EXT-X-BYTERANGE playlists) pass straight through.
    /// </summary>
    private async Task HandleCachedSegmentAsync(HttpContext context)
    {
        var url = context.Request.Query["u"].ToString();
        var referer = context.Request.Query["r"].ToString();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var source))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (context.Request.Headers.Range.Count > 0)
        {
            await HandleSegmentAsync(context);
            return;
        }

        if (cache.TryGet(source.AbsoluteUri) is { } hit)
        {
            context.Response.ContentType = SegmentContentType(source, null);
            await context.Response.SendFileAsync(hit, context.RequestAborted);
            return;
        }

        using var request = CreateUpstreamRequest(source, referer);
        using var response = await direct.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        context.Response.StatusCode = (int)response.StatusCode;
        context.Response.ContentType = SegmentContentType(source, response.Content.Headers.ContentType?.MediaType);
        if (response.Content.Headers.ContentLength is { } length) context.Response.ContentLength = length;
        if (!response.IsSuccessStatusCode) return;

        var temp = cache.NewTempFile();
        var complete = false;
        try
        {
            await using (var file = File.Create(temp))
            await using (var body = await response.Content.ReadAsStreamAsync(context.RequestAborted))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await body.ReadAsync(buffer, context.RequestAborted)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
                    await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
                }

                complete = response.Content.Headers.ContentLength is not { } expected || file.Length == expected;
            }
        }
        finally
        {
            if (complete) cache.Commit(source.AbsoluteUri, temp);
            else TryDelete(temp);
        }
    }

    /// <summary>
    /// Fetches the first segments of an HLS stream into the cache (the next episode, near the end of this one), so it
    /// starts as fast as a re-watch.
    /// </summary>
    public async Task PrefetchAsync(string url, int segments, string? referer = null, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var source)) return;
        try
        {
            var media = await HlsPlaylist.LoadAsync(direct, source, r => Decorate(r, referer), ct);
            foreach (var segment in media.Segments.Take(segments))
            {
                foreach (var uri in new[] { segment.Key?.Uri, segment.Map, segment.Uri })
                {
                    if (uri is null || cache.TryGet(uri.AbsoluteUri) is not null) continue;
                    using var request = CreateUpstreamRequest(uri, referer);
                    using var response = await direct.SendAsync(request, ct);
                    if (!response.IsSuccessStatusCode) return;
                    cache.Store(uri.AbsoluteUri, await response.Content.ReadAsByteArrayAsync(ct));
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or IOException)
        {
            // Best effort: the episode just starts the normal way.
        }
    }

    /// <summary>Some CDNs label TS segments as images or octet-stream; players sniff better with the right type.</summary>
    private static string SegmentContentType(Uri source, string? contentType)
    {
        var path = source.AbsolutePath;
        if (path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
            || contentType is null or "application/octet-stream" || contentType.StartsWith("image/", StringComparison.Ordinal))
        {
            return path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase)
                ? "video/mp4"
                : path.EndsWith(".key", StringComparison.OrdinalIgnoreCase) ? "application/octet-stream"
                : "video/mp2t";
        }

        return contentType;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>The headers a resource-site CDN expects (also used by offline downloads).</summary>
    public static void Decorate(HttpRequestMessage request, string? referer)
    {
        request.Headers.UserAgent.ParseAdd(BrowserAgent);
        if (!string.IsNullOrEmpty(referer)) request.Headers.Referrer = new Uri(referer);
    }

    private static HttpRequestMessage CreateUpstreamRequest(Uri source, string? referer)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, source);
        request.Headers.UserAgent.ParseAdd(BrowserAgent);
        if (!string.IsNullOrEmpty(referer))
        {
            request.Headers.Referrer = new Uri(referer);
        }

        return request;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }
}

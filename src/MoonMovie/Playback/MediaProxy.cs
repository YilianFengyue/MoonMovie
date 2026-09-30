using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MoonMovie.Core.Playback;

namespace MoonMovie.Playback;

/// <summary>
/// Loopback HTTP proxy between Media Foundation and resource-site CDNs.
/// <list type="bullet">
/// <item>Fetches directly (no system proxy) — mainland CDNs reject proxy exits, and MF would use the system proxy.</item>
/// <item>Rewrites HLS playlists so every variant/segment/key goes through here, stripping spliced ads.</item>
/// <item>Adds the Referer/User-Agent a CDN expects and passes Range through for seekable MP4.</item>
/// </list>
/// </summary>
public sealed class MediaProxy(HttpClient direct) : IAsyncDisposable
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
            u => Map("seg", u.AbsoluteUri, refererOrNull));

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
        var path = source.AbsolutePath;
        // Some CDNs label TS segments as images or octet-stream; Media Foundation sniffs better with the right type.
        if (path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
            || contentType is null or "application/octet-stream" || contentType.StartsWith("image/", StringComparison.Ordinal))
        {
            contentType = path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? "video/mp4"
                : path.EndsWith(".key", StringComparison.OrdinalIgnoreCase) ? "application/octet-stream"
                : "video/mp2t";
        }

        context.Response.ContentType = contentType;
        if (response.Content.Headers.ContentLength is { } length) context.Response.ContentLength = length;
        if (response.Content.Headers.ContentRange is { } contentRange) context.Response.Headers.ContentRange = contentRange.ToString();
        context.Response.Headers.AcceptRanges = "bytes";

        await using var body = await response.Content.ReadAsStreamAsync(context.RequestAborted);
        await body.CopyToAsync(context.Response.Body, 81920, context.RequestAborted);
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

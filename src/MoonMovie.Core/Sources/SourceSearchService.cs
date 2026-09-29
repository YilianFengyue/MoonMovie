using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MoonMovie.Core.Sources;

/// <summary>Queries Apple CMS resource sites and measures how quickly a stream actually starts.</summary>
public sealed class SourceSearchService(HttpClient http, IReadOnlyList<SourceSite> sites)
{
    private const string BrowserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Safari/537.36";

    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(7);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    private const int SlowThresholdMs = 1500;

    public IReadOnlyList<SourceSite> Sites { get; } = sites;

    public static IReadOnlyList<SourceSite> LoadBundledSites()
    {
        using var stream = typeof(SourceSearchService).Assembly
            .GetManifestResourceStream("MoonMovie.Core.Sources.sites.json")
            ?? throw new InvalidOperationException("Bundled source list missing.");
        using var doc = JsonDocument.Parse(stream);

        return doc.RootElement.GetProperty("api_site").EnumerateObject()
            .Select(p => new SourceSite(
                p.Name,
                p.Value.TryGetProperty("name", out var n) ? n.GetString() ?? p.Name : p.Name,
                p.Value.GetProperty("api").GetString()!.TrimEnd('/')))
            .ToArray();
    }

    /// <summary>Candidates on one site that match <paramref name="target"/>, best first.</summary>
    public async Task<IReadOnlyList<SourceCandidate>> SearchSiteAsync(SourceSite site, SourceTarget target,
        CancellationToken ct)
    {
        var results = await QueryAsync(site, target.Title, target, ct).ConfigureAwait(false);

        // "生化危机：爆发夜" is often listed without its subtitle punctuation or under the main title only.
        if (results.Count == 0)
        {
            var cut = target.Title.IndexOfAny(['：', ':', '·']);
            if (cut >= 2)
            {
                results = await QueryAsync(site, target.Title[..cut], target, ct).ConfigureAwait(false);
            }
        }

        return results;
    }

    /// <summary>Time to first playlist bytes; also checks the response really is a playlist or media.</summary>
    public async Task<ProbeResult> ProbeAsync(string url, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProbeTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(BrowserAgent);
            request.Headers.Range = new RangeHeaderValue(0, 4095);

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new ProbeResult(ProbeOutcome.Failed, (int)stopwatch.ElapsedMilliseconds, $"HTTP {(int)response.StatusCode}");
            }

            var buffer = new byte[512];
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var read = await body.ReadAtLeastAsync(buffer, 16, throwOnEndOfStream: false, timeout.Token).ConfigureAwait(false);
            var latency = (int)stopwatch.ElapsedMilliseconds;

            var isPlaylist = url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);
            if (isPlaylist && !Encoding.UTF8.GetString(buffer, 0, read).TrimStart('﻿', ' ', '\r', '\n').StartsWith("#EXTM3U", StringComparison.Ordinal))
            {
                return new ProbeResult(ProbeOutcome.Failed, latency, "不是有效的播放列表");
            }

            return new ProbeResult(latency > SlowThresholdMs ? ProbeOutcome.Slow : ProbeOutcome.Ok, latency);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
        {
            return new ProbeResult(ProbeOutcome.Failed, (int)stopwatch.ElapsedMilliseconds,
                ex is TaskCanceledException ? "超时" : "无法连接");
        }
    }

    private async Task<IReadOnlyList<SourceCandidate>> QueryAsync(SourceSite site, string keyword, SourceTarget target,
        CancellationToken ct)
    {
        var url = $"{site.Api}/?ac=videolist&wd={Uri.EscapeDataString(keyword)}";
        MacCmsResponse? payload;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(SearchTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(BrowserAgent);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
            }

            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (body.Length == 0 || body.TrimStart()[0] != '{')
            {
                throw new HttpRequestException("非 JSON 响应");
            }

            payload = JsonSerializer.Deserialize(body, MacCmsJsonContext.Default.MacCmsResponse);
        }
        catch (JsonException)
        {
            throw new HttpRequestException("响应格式错误");
        }

        var candidates = new List<SourceCandidate>();
        foreach (var vod in payload?.List ?? [])
        {
            var lines = MacCms.ParseLines(vod.VodPlayFrom, vod.VodPlayUrl);
            var score = SourceMatcher.Score(vod, target, lines.Count);
            if (score < 0)
            {
                continue;
            }

            var episodes = lines.Max(l => l.Episodes.Count);
            score += SourceMatcher.EpisodeAdjustment(episodes, target.ExpectedEpisodes);
            if (score < SourceMatcher.AcceptThreshold)
            {
                continue;
            }

            candidates.Add(new SourceCandidate(
                site,
                vod.VodId ?? vod.VodName!,
                vod.VodName!.Trim(),
                int.TryParse(vod.VodYear, out var y) ? y : null,
                vod.TypeName,
                string.IsNullOrWhiteSpace(vod.VodRemarks) ? null : vod.VodRemarks.Trim(),
                lines,
                score));
        }

        return candidates.OrderByDescending(c => c.Score).ToArray();
    }
}

using System.Globalization;
using System.Text.RegularExpressions;

namespace MoonMovie.Core.Playback;

/// <summary>AES-128 (or none) encryption in force for a segment.</summary>
public sealed record HlsKey(string Method, Uri? Uri, byte[]? Iv);

public sealed record HlsSegment(Uri Uri, double Duration, long Sequence, HlsKey? Key, Uri? Map);

public sealed record HlsMedia(IReadOnlyList<HlsSegment> Segments, int RemovedAds, double RemovedSeconds)
{
    public double Duration => Segments.Sum(s => s.Duration);
}

/// <summary>
/// Resolves an HLS URL to the programme's segments: follows a master playlist to its best variant and drops spliced
/// ads exactly like playback does (<see cref="HlsRewriter"/>), keeping each segment's original media sequence for
/// AES-128 IVs. Used for offline downloads and next-episode prefetch.
/// </summary>
public static partial class HlsPlaylist
{
    public static async Task<HlsMedia> LoadAsync(HttpClient http, Uri url, Action<HttpRequestMessage> decorate,
        CancellationToken ct, int depth = 0)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        decorate(request);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var baseUri = response.RequestMessage?.RequestUri ?? url;
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();

        if (lines.Any(l => l.StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal)))
        {
            if (depth > 3) throw new InvalidDataException("播放列表嵌套过深");
            return await LoadAsync(http, BestVariant(lines, baseUri), decorate, ct, depth + 1).ConfigureAwait(false);
        }

        // Original sequence numbers, before ad groups disappear.
        var sequenceOf = new Dictionary<string, long>();
        var sequence = lines.FirstOrDefault(l => l.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal)) is { } ms
                       && long.TryParse(ms["#EXT-X-MEDIA-SEQUENCE:".Length..], out var first)
            ? first
            : 0;
        foreach (var line in lines.Where(l => l[0] != '#'))
        {
            sequenceOf.TryAdd(new Uri(baseUri, line).AbsoluteUri, sequence++);
        }

        var cleaned = HlsRewriter.Rewrite(text, baseUri, u => u.AbsoluteUri, u => u.AbsoluteUri);
        var segments = new List<HlsSegment>();
        HlsKey? key = null;
        Uri? map = null;
        double duration = 0;
        var fallback = 0L;
        foreach (var line in cleaned.Playlist.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
        {
            if (line.StartsWith("#EXT-X-KEY", StringComparison.Ordinal))
            {
                var method = Attribute(line, "METHOD") ?? "NONE";
                key = method == "NONE" ? null : new HlsKey(method,
                    Attribute(line, "URI") is { } k ? new Uri(k) : null,
                    Attribute(line, "IV") is { } iv ? Convert.FromHexString(iv.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? iv[2..] : iv) : null);
            }
            else if (line.StartsWith("#EXT-X-MAP", StringComparison.Ordinal))
            {
                map = Attribute(line, "URI") is { } m ? new Uri(m) : null;
            }
            else if (line.StartsWith("#EXTINF", StringComparison.Ordinal))
            {
                var m = ExtInf().Match(line);
                duration = m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
            }
            else if (line[0] != '#')
            {
                var uri = new Uri(line);
                segments.Add(new HlsSegment(uri, duration, sequenceOf.GetValueOrDefault(uri.AbsoluteUri, fallback), key, map));
                fallback++;
                duration = 0;
            }
        }

        return new HlsMedia(segments, cleaned.RemovedSegments, cleaned.RemovedSeconds);
    }

    /// <summary>Highest bandwidth wins: offline copies and prefetch want what playback will pick.</summary>
    private static Uri BestVariant(string[] lines, Uri baseUri)
    {
        Uri? best = null;
        long bestBandwidth = -1;
        for (var i = 0; i < lines.Length - 1; i++)
        {
            if (!lines[i].StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal)) continue;
            var bandwidth = long.TryParse(Attribute(lines[i], "BANDWIDTH"), out var b) ? b : 0;
            var next = lines.Skip(i + 1).FirstOrDefault(l => l[0] != '#');
            if (next is null || bandwidth <= bestBandwidth) continue;
            bestBandwidth = bandwidth;
            best = new Uri(baseUri, next);
        }

        return best ?? throw new InvalidDataException("播放列表里没有可用的清晰度");
    }

    /// <summary>The 16-byte IV: explicit, or the segment's media sequence number big-endian.</summary>
    public static byte[] IvFor(HlsSegment segment)
    {
        if (segment.Key?.Iv is { Length: 16 } iv) return iv;
        var bytes = new byte[16];
        var n = segment.Sequence;
        for (var i = 15; i >= 8; i--)
        {
            bytes[i] = (byte)(n & 0xFF);
            n >>= 8;
        }

        return bytes;
    }

    private static string? Attribute(string line, string name)
    {
        var m = Regex.Match(line, $@"(?:^|[:,]){name}=(""(?<q>[^""]*)""|(?<v>[^,]*))");
        if (!m.Success) return null;
        return m.Groups["q"].Success ? m.Groups["q"].Value : m.Groups["v"].Value;
    }

    [GeneratedRegex(@"#EXTINF:\s*([0-9.]+)")]
    private static partial Regex ExtInf();
}

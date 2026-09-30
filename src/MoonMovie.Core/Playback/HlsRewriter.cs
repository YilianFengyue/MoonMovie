using System.Text;
using System.Text.RegularExpressions;

namespace MoonMovie.Core.Playback;

/// <summary>
/// Rewrites HLS playlists so every URI goes through the local media proxy, and strips inserted ads.
/// </summary>
/// <remarks>
/// Resource sites splice ads in as short runs of segments fenced by <c>#EXT-X-DISCONTINUITY</c>, served from a
/// different host or directory than the programme. We keep the dominant origin (weighted by duration) and drop
/// short discontinuity groups that come from anywhere else.
/// </remarks>
public static partial class HlsRewriter
{
    private const double MaxAdGroupSeconds = 120;

    public sealed record Result(string Playlist, bool IsMaster, int RemovedSegments, double RemovedSeconds);

    public static Result Rewrite(string playlist, Uri baseUri, Func<Uri, string> mapPlaylist, Func<Uri, string> mapSegment)
    {
        var lines = playlist.Replace("\r\n", "\n").Split('\n');
        var isMaster = lines.Any(l => l.StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal));
        return isMaster
            ? new Result(RewriteMaster(lines, baseUri, mapPlaylist), true, 0, 0)
            : RewriteMedia(lines, baseUri, mapSegment);
    }

    private static string RewriteMaster(string[] lines, Uri baseUri, Func<Uri, string> mapPlaylist)
    {
        var output = new StringBuilder();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] != '#')
            {
                output.AppendLine(mapPlaylist(new Uri(baseUri, line)));
            }
            else if (line.StartsWith("#EXT-X-MEDIA", StringComparison.Ordinal) || line.StartsWith("#EXT-X-I-FRAME-STREAM-INF", StringComparison.Ordinal))
            {
                output.AppendLine(ReplaceUriAttribute(line, baseUri, mapPlaylist));
            }
            else
            {
                output.AppendLine(line);
            }
        }

        return output.ToString();
    }

    private static Result RewriteMedia(string[] lines, Uri baseUri, Func<Uri, string> mapSegment)
    {
        // Pass 1: split into header lines and discontinuity groups of segments.
        var header = new List<string>();
        var groups = new List<Group> { new() };
        var pending = new List<string>();
        double pendingDuration = 0;
        var inBody = false;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("#EXT-X-DISCONTINUITY", StringComparison.Ordinal) && !line.StartsWith("#EXT-X-DISCONTINUITY-SEQUENCE", StringComparison.Ordinal))
            {
                if (groups[^1].Segments.Count > 0) groups.Add(new Group());
                inBody = true;
                continue;
            }

            if (line.StartsWith("#EXTINF", StringComparison.Ordinal))
            {
                inBody = true;
                var m = ExtInfDuration().Match(line);
                pendingDuration = m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
                pending.Add(line);
                continue;
            }

            if (line.StartsWith("#EXT-X-KEY", StringComparison.Ordinal) || line.StartsWith("#EXT-X-MAP", StringComparison.Ordinal))
            {
                var rewritten = ReplaceUriAttribute(line, baseUri, mapSegment);
                if (inBody) pending.Add(rewritten); else header.Add(rewritten);
                continue;
            }

            if (line.StartsWith("#EXT-X-ENDLIST", StringComparison.Ordinal))
            {
                continue; // re-appended at the end
            }

            if (line[0] == '#')
            {
                if (inBody) pending.Add(line); else header.Add(line);
                continue;
            }

            var uri = new Uri(baseUri, line);
            groups[^1].Segments.Add(new Segment(pending.ToArray(), uri, pendingDuration));
            pending.Clear();
            pendingDuration = 0;
        }

        var ended = lines.Any(l => l.StartsWith("#EXT-X-ENDLIST", StringComparison.Ordinal));
        groups.RemoveAll(g => g.Segments.Count == 0);

        // Pass 2: find the programme's origin and drop short foreign groups.
        var dominant = groups
            .SelectMany(g => g.Segments)
            .GroupBy(s => Origin(s.Uri))
            .OrderByDescending(g => g.Sum(s => s.Duration))
            .Select(g => g.Key)
            .FirstOrDefault();

        var medianDuration = groups.Count == 0 ? 0 : groups.Select(g => g.Duration).OrderBy(d => d).ElementAt(groups.Count / 2);
        var removedSegments = 0;
        double removedSeconds = 0;
        var kept = new List<Group>();
        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            if (groups.Count > 1 && group.Duration <= MaxAdGroupSeconds && IsAd(i))
            {
                removedSegments += group.Segments.Count;
                removedSeconds += group.Duration;
                continue;
            }

            kept.Add(group);
        }

        bool IsAd(int i)
        {
            var group = groups[i];

            // A. Served from a different host/directory than the programme ("/adjump/...").
            if (dominant is not null && group.Segments.All(s => Origin(s.Uri) != dominant))
            {
                return true;
            }

            var middle = i > 0 && i < groups.Count - 1;
            if (!middle)
            {
                return false;
            }

            // B. Numbered segments: the group breaks the sequence and the next group resumes it.
            var before = SegmentNumber(groups[i - 1].Segments[^1].Uri);
            var first = SegmentNumber(group.Segments[0].Uri);
            var after = SegmentNumber(groups[i + 1].Segments[0].Uri);
            if (before is { } b && after is { } a && first is { } f && a == b + 1 && f != b + 1)
            {
                return true;
            }

            // C. Programme split into a few long parts; a short splice between two of them is an ad.
            return groups.Count <= 16 && medianDuration >= MaxAdGroupSeconds && group.Duration < 35;
        }

        var output = new StringBuilder();
        foreach (var line in header) output.AppendLine(line);
        for (var i = 0; i < kept.Count; i++)
        {
            if (i > 0) output.AppendLine("#EXT-X-DISCONTINUITY");
            foreach (var segment in kept[i].Segments)
            {
                foreach (var tag in segment.Tags) output.AppendLine(tag);
                output.AppendLine(mapSegment(segment.Uri));
            }
        }

        if (ended) output.AppendLine("#EXT-X-ENDLIST");
        return new Result(output.ToString(), false, removedSegments, removedSeconds);
    }

    /// <summary>Host plus directory: ads usually live on another host or under another path.</summary>
    private static string Origin(Uri uri)
    {
        var path = uri.AbsolutePath;
        var slash = path.LastIndexOf('/');
        return uri.Host + (slash > 0 ? path[..slash] : string.Empty);
    }

    /// <summary>Trailing number of the file name ("0000075.ts" → 75), when it looks like a sequence.</summary>
    private static long? SegmentNumber(Uri uri)
    {
        var m = TrailingNumber().Match(uri.AbsolutePath);
        return m.Success && long.TryParse(m.Groups[1].Value, out var n) ? n : null;
    }

    private static string ReplaceUriAttribute(string line, Uri baseUri, Func<Uri, string> map) =>
        UriAttribute().Replace(line, m => $"URI=\"{map(new Uri(baseUri, m.Groups[1].Value))}\"");

    private sealed class Group
    {
        public List<Segment> Segments { get; } = [];

        public double Duration => Segments.Sum(s => s.Duration);
    }

    private sealed record Segment(string[] Tags, Uri Uri, double Duration);

    [GeneratedRegex(@"#EXTINF:\s*([0-9.]+)")]
    private static partial Regex ExtInfDuration();

    // Last (up to 9) digits before the extension; random hashes rarely form a +1 sequence, so they never trip rule B.
    [GeneratedRegex(@"([0-9]{1,9})\.(?:ts|m4s|jpg|jpeg|png)$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingNumber();

    [GeneratedRegex("URI=\"([^\"]+)\"")]
    private static partial Regex UriAttribute();
}

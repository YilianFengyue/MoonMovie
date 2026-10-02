using System.Text.Json.Serialization;
using MoonMovie.Core.Models;

namespace MoonMovie.Core.Sources;

public sealed record SourceSite(string Key, string Name, string Api);

/// <summary>An episode on a source; <see cref="Badge"/> marks one the source restricts ("会员").</summary>
public sealed record PlayEpisode(int Index, string Name, string Url, string? Badge = null);

public sealed record PlayLine(string Name, IReadOnlyList<PlayEpisode> Episodes);

/// <summary>What we are looking for, described with everything TMDB told us.</summary>
public sealed record SourceTarget(
    string Title,
    string? OriginalTitle,
    int? Year,
    MediaKind Kind,
    int? Season,
    int? ExpectedEpisodes,
    IReadOnlyList<string> People)
{
    public string CacheKey => $"{Kind}:{Title}:{Year}:{Season}";
}

/// <summary>One title on one resource site that plausibly is the thing we want.</summary>
public sealed record SourceCandidate(
    SourceSite Site,
    string RemoteId,
    string Title,
    int? Year,
    string? Category,
    string? Remarks,
    IReadOnlyList<PlayLine> Lines,
    int Score)
{
    [JsonIgnore]
    public string Identity => $"{Site.Key}:{RemoteId}";

    /// <summary>The line with the most episodes; resource sites often keep a partial mirror line.</summary>
    [JsonIgnore]
    public PlayLine PrimaryLine => Lines.OrderByDescending(l => l.Episodes.Count).First();

    [JsonIgnore]
    public int EpisodeCount => PrimaryLine.Episodes.Count;
}

public enum ProbeOutcome
{
    Ok,
    Slow,
    Failed,
}

public sealed record ProbeResult(ProbeOutcome Outcome, int LatencyMs, string? Error = null);

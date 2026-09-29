using MoonMovie.Core.Models;

namespace MoonMovie.Core.Sources;

public sealed record SourceSite(string Key, string Name, string Api);

public sealed record PlayEpisode(int Index, string Name, string Url);

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
    public string Identity => $"{Site.Key}:{RemoteId}";

    /// <summary>The line with the most episodes; resource sites often keep a partial mirror line.</summary>
    public PlayLine PrimaryLine => Lines.OrderByDescending(l => l.Episodes.Count).First();

    public int EpisodeCount => PrimaryLine.Episodes.Count;
}

public enum ProbeOutcome
{
    Ok,
    Slow,
    Failed,
}

public sealed record ProbeResult(ProbeOutcome Outcome, int LatencyMs, string? Error = null);

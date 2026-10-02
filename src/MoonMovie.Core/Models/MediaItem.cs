using MoonMovie.Core.Tmdb;

namespace MoonMovie.Core.Models;

public enum MediaKind
{
    Movie,
    Tv,
}

public sealed record MediaItem(
    int TmdbId,
    MediaKind Kind,
    string Title,
    string? OriginalTitle,
    string? Overview,
    int? Year,
    double Rating,
    int VoteCount,
    string? PosterPath,
    string? BackdropPath,
    IReadOnlyList<int> GenreIds,
    string? OriginalLanguage)
{
    /// <summary>Set for local files TMDB could not identify ("local:…"); such items have no detail page.</summary>
    public string? LocalKey { get; init; }

    public bool IsLocalOnly => LocalKey is not null;

    public string MediaKey => LocalKey ?? $"tmdb:{(Kind == MediaKind.Movie ? "movie" : "tv")}:{TmdbId}";

    public string KindLabel => Kind == MediaKind.Movie ? "电影" : "剧集";

    public string? RatingText => VoteCount >= 20 && Rating > 0 ? Rating.ToString("0.0") : null;

    public IReadOnlyList<string> GenreNames => TmdbGenres.Names(Kind, GenreIds);

    /// <summary>"电影 · 2014 · 科幻 / 冒险" style line; empty parts are skipped.</summary>
    public string MetaLine
    {
        get
        {
            var parts = new List<string>(4) { KindLabel };
            if (Year is { } y)
            {
                parts.Add(y.ToString());
            }

            var genres = GenreNames.Take(2).ToArray();
            if (genres.Length > 0)
            {
                parts.Add(string.Join(" / ", genres));
            }

            return string.Join("  ·  ", parts);
        }
    }

    public static MediaItem? FromDto(TmdbMediaDto dto, MediaKind? fallbackKind)
    {
        MediaKind kind;
        switch (dto.MediaType)
        {
            case "movie": kind = MediaKind.Movie; break;
            case "tv": kind = MediaKind.Tv; break;
            case null when fallbackKind is { } fk: kind = fk; break;
            default: return null; // person or unknown
        }

        var title = kind == MediaKind.Movie ? dto.Title : dto.Name;
        if (string.IsNullOrWhiteSpace(title) || dto.Adult)
        {
            return null;
        }

        var original = kind == MediaKind.Movie ? dto.OriginalTitle : dto.OriginalName;
        var date = kind == MediaKind.Movie ? dto.ReleaseDate : dto.FirstAirDate;
        int? year = date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), out var y) ? y : null;

        return new MediaItem(
            dto.Id,
            kind,
            title.Trim(),
            string.Equals(original, title, StringComparison.Ordinal) ? null : original,
            string.IsNullOrWhiteSpace(dto.Overview) ? null : dto.Overview.Trim(),
            year,
            dto.VoteAverage,
            dto.VoteCount,
            dto.PosterPath,
            dto.BackdropPath,
            dto.GenreIds ?? [],
            dto.OriginalLanguage);
    }
}

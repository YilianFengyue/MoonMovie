using MoonMovie.Core.Tmdb;

namespace MoonMovie.Core.Models;

public sealed record Person(int Id, string Name, string? Role, string? ProfilePath);

public sealed record SeasonSummary(int Number, string Name, int EpisodeCount, int? Year);

public sealed record EpisodeInfo(int Number, string Name, string? Overview, string? StillPath, int? RuntimeMinutes, string? AirDate);

public sealed record MediaDetail(
    MediaItem Item,
    string? Tagline,
    int? RuntimeMinutes,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Countries,
    IReadOnlyList<SeasonSummary> Seasons,
    int? EpisodeCount,
    IReadOnlyList<Person> Cast,
    IReadOnlyList<string> Directors,
    IReadOnlyList<MediaItem> Recommendations,
    string? LogoPath,
    string? Status)
{
    public static MediaDetail FromDto(TmdbDetailsDto dto, MediaKind kind)
    {
        var genreIds = dto.Genres?.Select(g => g.Id).ToList() ?? [];
        var item = MediaItem.FromDto(new TmdbMediaDto
        {
            Id = dto.Id,
            Title = dto.Title,
            Name = dto.Name,
            OriginalTitle = dto.OriginalTitle,
            OriginalName = dto.OriginalName,
            Overview = dto.Overview,
            PosterPath = dto.PosterPath,
            BackdropPath = dto.BackdropPath,
            ReleaseDate = dto.ReleaseDate,
            FirstAirDate = dto.FirstAirDate,
            VoteAverage = dto.VoteAverage,
            VoteCount = dto.VoteCount,
            GenreIds = genreIds,
            OriginalLanguage = dto.OriginalLanguage,
        }, kind) ?? throw new InvalidOperationException("TMDB detail without a title.");

        var cast = kind == MediaKind.Movie
            ? (dto.Credits?.Cast ?? [])
                .OrderBy(c => c.Order)
                .Select(c => new Person(c.Id, c.Name, c.Character, c.ProfilePath))
            : (dto.AggregateCredits?.Cast ?? [])
                .OrderBy(c => c.Order)
                .Select(c => new Person(c.Id, c.Name, c.Roles?.FirstOrDefault()?.Character, c.ProfilePath));

        var directors = kind == MediaKind.Movie
            ? (dto.Credits?.Crew ?? []).Where(c => c.Job == "Director").Select(c => c.Name)
            : (dto.CreatedBy ?? []).Select(c => c.Name);

        var countries = (dto.OriginCountry is { Count: > 0 } origin
                ? origin
                : dto.ProductionCountries?.Select(c => c.Code ?? string.Empty) ?? [])
            .Select(Regions.Name)
            .Where(n => n.Length > 0);

        var seasons = (dto.Seasons ?? [])
            .Where(s => s.SeasonNumber > 0 && s.EpisodeCount > 0)
            .Select(s => new SeasonSummary(
                s.SeasonNumber,
                string.IsNullOrWhiteSpace(s.Name) ? $"第 {s.SeasonNumber} 季" : s.Name!,
                s.EpisodeCount,
                s.AirDate is { Length: >= 4 } && int.TryParse(s.AirDate.AsSpan(0, 4), out var y) ? y : null))
            .ToArray();

        var logo = dto.Images?.Logos?
            .Where(l => l.FilePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            .OrderBy(l => l.Language switch { "zh" => 0, "en" => 1, null => 2, _ => 3 })
            .ThenByDescending(l => l.VoteAverage)
            .Select(l => l.FilePath)
            .FirstOrDefault();

        int? runtime = dto.Runtime is > 0 ? dto.Runtime : null;
        if (runtime is null && dto.EpisodeRunTime?.FirstOrDefault(x => x > 0) is int episodeRuntime && episodeRuntime > 0)
        {
            runtime = episodeRuntime;
        }

        return new MediaDetail(
            item,
            string.IsNullOrWhiteSpace(dto.Tagline) ? null : dto.Tagline.Trim(),
            runtime,
            dto.Genres?.Select(g => g.Name).Where(n => n.Length > 0).ToArray() ?? [],
            countries.Distinct().Take(2).ToArray(),
            seasons,
            dto.NumberOfEpisodes,
            cast.Where(p => !string.IsNullOrWhiteSpace(p.Name)).Take(24).ToArray(),
            directors.Distinct().Take(3).ToArray(),
            (dto.Recommendations?.Results ?? [])
                .Select(r => MediaItem.FromDto(r, kind))
                .Where(m => m?.PosterPath is not null)
                .Select(m => m!)
                .Take(20)
                .ToArray(),
            logo,
            dto.Status);
    }
}

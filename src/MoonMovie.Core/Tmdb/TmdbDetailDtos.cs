using System.Text.Json.Serialization;

namespace MoonMovie.Core.Tmdb;

public sealed class TmdbDetailsDto
{
    public int Id { get; set; }

    public string? Title { get; set; }

    public string? Name { get; set; }

    public string? OriginalTitle { get; set; }

    public string? OriginalName { get; set; }

    public string? Overview { get; set; }

    public string? Tagline { get; set; }

    public List<TmdbNamedDto>? Genres { get; set; }

    public int? Runtime { get; set; }

    public List<int>? EpisodeRunTime { get; set; }

    public int? NumberOfSeasons { get; set; }

    public int? NumberOfEpisodes { get; set; }

    public List<TmdbSeasonSummaryDto>? Seasons { get; set; }

    public string? ReleaseDate { get; set; }

    public string? FirstAirDate { get; set; }

    public double VoteAverage { get; set; }

    public int VoteCount { get; set; }

    public string? BackdropPath { get; set; }

    public string? PosterPath { get; set; }

    public string? OriginalLanguage { get; set; }

    /// <summary>Films carry it directly; series in external_ids.</summary>
    public string? ImdbId { get; set; }

    public TmdbExternalIdsDto? ExternalIds { get; set; }

    public List<TmdbCountryDto>? ProductionCountries { get; set; }

    public List<string>? OriginCountry { get; set; }

    public List<TmdbNamedDto>? CreatedBy { get; set; }

    public string? Status { get; set; }

    public TmdbCreditsDto? Credits { get; set; }

    public TmdbAggregateCreditsDto? AggregateCredits { get; set; }

    public TmdbPage<TmdbMediaDto>? Recommendations { get; set; }

    public TmdbImagesDto? Images { get; set; }
}

public sealed class TmdbNamedDto
{
    public int Id { get; set; }

    public string Name { get; set; } = "";
}

public sealed class TmdbCountryDto
{
    [JsonPropertyName("iso_3166_1")]
    public string? Code { get; set; }

    public string? Name { get; set; }
}

public sealed class TmdbSeasonSummaryDto
{
    public int SeasonNumber { get; set; }

    public string? Name { get; set; }

    public int EpisodeCount { get; set; }

    public string? PosterPath { get; set; }

    public string? AirDate { get; set; }
}

public sealed class TmdbCreditsDto
{
    public List<TmdbCastDto>? Cast { get; set; }

    public List<TmdbCrewDto>? Crew { get; set; }
}

public sealed class TmdbCastDto
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public string? Character { get; set; }

    public string? ProfilePath { get; set; }

    public int Order { get; set; }
}

public sealed class TmdbCrewDto
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public string? Job { get; set; }
}

public sealed class TmdbAggregateCreditsDto
{
    public List<TmdbAggregateCastDto>? Cast { get; set; }
}

public sealed class TmdbAggregateCastDto
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public string? ProfilePath { get; set; }

    public List<TmdbRoleDto>? Roles { get; set; }

    public int TotalEpisodeCount { get; set; }

    public int Order { get; set; }
}

public sealed class TmdbRoleDto
{
    public string? Character { get; set; }

    public int EpisodeCount { get; set; }
}

public sealed class TmdbSeasonDto
{
    public int SeasonNumber { get; set; }

    public string? Name { get; set; }

    public string? AirDate { get; set; }

    public List<TmdbEpisodeDto>? Episodes { get; set; }
}

public sealed class TmdbEpisodeDto
{
    public int EpisodeNumber { get; set; }

    public string? Name { get; set; }

    public string? Overview { get; set; }

    public string? StillPath { get; set; }

    public int? Runtime { get; set; }

    public string? AirDate { get; set; }

    public double VoteAverage { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(TmdbDetailsDto))]
[JsonSerializable(typeof(TmdbSeasonDto))]
internal sealed partial class TmdbDetailJsonContext : JsonSerializerContext;

public sealed class TmdbExternalIdsDto
{
    public string? ImdbId { get; set; }
}

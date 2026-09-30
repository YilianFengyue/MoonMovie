using System.Text.Json.Serialization;

namespace MoonMovie.Core.Tmdb;

public sealed class TmdbPage<T>
{
    public int Page { get; set; }

    public List<T> Results { get; set; } = [];

    public int TotalPages { get; set; }

    public int TotalResults { get; set; }
}

public sealed class TmdbMediaDto
{
    public int Id { get; set; }

    public string? Title { get; set; }

    public string? Name { get; set; }

    public string? OriginalTitle { get; set; }

    public string? OriginalName { get; set; }

    public string? Overview { get; set; }

    public string? PosterPath { get; set; }

    public string? BackdropPath { get; set; }

    public string? ReleaseDate { get; set; }

    public string? FirstAirDate { get; set; }

    public double VoteAverage { get; set; }

    public int VoteCount { get; set; }

    public string? MediaType { get; set; }

    public List<int>? GenreIds { get; set; }

    public string? OriginalLanguage { get; set; }

    public double Popularity { get; set; }

    public bool Adult { get; set; }

    // Person results of /search/multi.
    public string? ProfilePath { get; set; }

    public string? KnownForDepartment { get; set; }

    public List<TmdbMediaDto>? KnownFor { get; set; }
}

public sealed class TmdbImagesDto
{
    public List<TmdbImageDto>? Logos { get; set; }

    public List<TmdbImageDto>? Backdrops { get; set; }
}

public sealed class TmdbImageDto
{
    public string FilePath { get; set; } = "";

    [JsonPropertyName("iso_639_1")]
    public string? Language { get; set; }

    public double AspectRatio { get; set; }

    public double VoteAverage { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(TmdbPage<TmdbMediaDto>))]
[JsonSerializable(typeof(TmdbImagesDto))]
internal sealed partial class TmdbJsonContext : JsonSerializerContext;

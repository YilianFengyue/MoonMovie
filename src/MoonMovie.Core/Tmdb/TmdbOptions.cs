using System.Text.RegularExpressions;
using MoonMovie.Core.Configuration;

namespace MoonMovie.Core.Tmdb;

public sealed partial class TmdbOptions
{
    public const string DirectApiBase = "https://api.themoviedb.org/3";
    public const string DirectImageRoot = "https://image.tmdb.org/t/p";
    public const string ProxyApiBase = "https://tmdb-proxy.lapu2023.workers.dev/3";
    public const string ProxyImageRoot = "https://tmdb-proxy.lapu2023.workers.dev/image";

    public string? ApiKey { get; init; }

    public string? ReadAccessToken { get; init; }

    public string Language { get; init; } = "zh-CN";

    /// <summary>API bases tried in order; the first one that answers wins for the session.</summary>
    public IReadOnlyList<string> ApiBases { get; init; } = [DirectApiBase, ProxyApiBase];

    /// <summary>Image roots without a size segment, e.g. https://image.tmdb.org/t/p.</summary>
    public IReadOnlyList<string> ImageRoots { get; init; } = [DirectImageRoot, ProxyImageRoot];

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey) || !string.IsNullOrWhiteSpace(ReadAccessToken);

    /// <param name="apiKey">A key entered in Settings; wins over .env.</param>
    public static TmdbOptions FromEnv(EnvFile env, string? apiKey = null)
    {
        var apiBase = env.Get("TMDB_BASE_URL")?.TrimEnd('/');
        var imageBase = env.Get("TMDB_IMAGE_BASE");

        return new TmdbOptions
        {
            ApiKey = string.IsNullOrWhiteSpace(apiKey) ? env.Get("TMDB_API_KEY") : apiKey.Trim(),
            ReadAccessToken = env.Get("TMDB_READ_ACCESS_TOKEN"),
            ApiBases = Distinct(apiBase, DirectApiBase, ProxyApiBase),
            ImageRoots = Distinct(imageBase is null ? null : NormalizeImageRoot(imageBase), DirectImageRoot, ProxyImageRoot),
        };
    }

    /// <summary>Strips a trailing size segment such as /w500 or /original.</summary>
    public static string NormalizeImageRoot(string imageBase) =>
        SizeSuffix().Replace(imageBase.TrimEnd('/'), string.Empty);

    private static string[] Distinct(params string?[] values) =>
        values.Where(v => !string.IsNullOrWhiteSpace(v))
              .Select(v => v!)
              .Distinct(StringComparer.OrdinalIgnoreCase)
              .ToArray();

    [GeneratedRegex(@"/(w\d+|h\d+|original)$", RegexOptions.IgnoreCase)]
    private static partial Regex SizeSuffix();
}

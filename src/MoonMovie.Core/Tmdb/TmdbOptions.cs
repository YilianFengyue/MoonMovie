using System.Text.RegularExpressions;
using MoonMovie.Core.Configuration;

namespace MoonMovie.Core.Tmdb;

public sealed partial class TmdbOptions
{
    public const string DirectApiBase = "https://api.themoviedb.org/3";
    public const string DirectImageRoot = "https://image.tmdb.org/t/p";
    public const string ProxyApiBase = "https://tmdb-proxy.lapu2023.workers.dev/3";
    public const string ProxyImageRoot = "https://tmdb-proxy.lapu2023.workers.dev/image";

    // MoonMovie's own reverse proxy (Vercel, China-friendly domain): reachable with or without a system proxy.
    public const string OwnApiBase = "https://tmdb.ylfmoonn.top/3";
    public const string OwnImageRoot = "https://tmdb.ylfmoonn.top/image";

    public string? ApiKey { get; init; }

    public string? ReadAccessToken { get; init; }

    public string Language { get; init; } = "zh-CN";

    /// <summary>API bases, asked all at once; the first one that answers wins for the session.</summary>
    public IReadOnlyList<string> ApiBases { get; init; } = [OwnApiBase, DirectApiBase, ProxyApiBase];

    /// <summary>
    /// Image roots without a size segment, e.g. https://image.tmdb.org/t/p. The own proxy comes first: image.tmdb.org
    /// is reset without a system proxy, and every image would pay for that before falling back.
    /// </summary>
    public IReadOnlyList<string> ImageRoots { get; init; } = [OwnImageRoot, DirectImageRoot, ProxyImageRoot];

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey) || !string.IsNullOrWhiteSpace(ReadAccessToken);

    /// <param name="apiKey">A key entered in Settings; wins over .env.</param>
    public static TmdbOptions FromEnv(EnvFile env, string? apiKey = null)
    {
        var apiBase = env.Get("TMDB_BASE_URL")?.TrimEnd('/');
        var imageBase = env.Get("TMDB_IMAGE_BASE") is { } configured ? NormalizeImageRoot(configured) : null;
        // A configured mirror leads; the official host does not (it is the one that needs a system proxy).
        var imageFirst = imageBase is not null && !imageBase.Equals(DirectImageRoot, StringComparison.OrdinalIgnoreCase) ? imageBase : null;

        return new TmdbOptions
        {
            ApiKey = string.IsNullOrWhiteSpace(apiKey) ? env.Get("TMDB_API_KEY") : apiKey.Trim(),
            ReadAccessToken = env.Get("TMDB_READ_ACCESS_TOKEN"),
            ApiBases = Distinct(apiBase, OwnApiBase, DirectApiBase, ProxyApiBase),
            ImageRoots = Distinct(imageFirst, OwnImageRoot, DirectImageRoot, ProxyImageRoot),
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

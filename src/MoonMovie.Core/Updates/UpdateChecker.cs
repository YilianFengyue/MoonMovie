using System.Net.Http.Headers;
using System.Text.Json;

namespace MoonMovie.Core.Updates;

/// <summary>A newer release on GitHub: its version, notes and the files to update with.</summary>
/// <param name="MsixUrl">The signed package (installed builds update with it).</param>
/// <param name="PageUrl">The release page (the portable build downloads from there).</param>
public sealed record UpdateInfo(Version Version, string Tag, string? Notes, DateTimeOffset Published, string? MsixUrl, long MsixBytes, string PageUrl);

/// <summary>
/// Asks GitHub for the latest MoonMovie release (tags like v1.2.0) and whether it is newer than this build.
/// Unauthenticated: GitHub allows 60 requests an hour per address, plenty for a check every few hours.
/// </summary>
public sealed class UpdateChecker(HttpClient http)
{
    public const string Repository = "YilianFengyue/MoonMovie";

    public static string ReleasesPage => $"https://github.com/{Repository}/releases";

    /// <summary>The newer release, or null when this build is current (or nothing is published yet).</summary>
    public async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("MoonMovie", current.ToString(3)));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no release yet
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct)
            .ConfigureAwait(false);
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (!TryParse(tag, out var version) || version <= Normalize(current)) return null;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;

        string? msix = null;
        long bytes = 0;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (!name.EndsWith("-x64.msix", StringComparison.OrdinalIgnoreCase)) continue;
                msix = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                bytes = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
            }
        }

        var published = root.TryGetProperty("published_at", out var p) && p.TryGetDateTimeOffset(out var at) ? at : DateTimeOffset.Now;
        var page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesPage : ReleasesPage;
        var notes = root.TryGetProperty("body", out var b) ? b.GetString() : null;
        return new UpdateInfo(version, tag, notes, published, msix, bytes, page);
    }

    /// <summary>Downloads the package to a temporary file, reporting 0–1; returns its path.</summary>
    public async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken ct = default)
    {
        if (update.MsixUrl is not { } url) throw new InvalidOperationException("This release has no package.");
        var folder = Path.Combine(Path.GetTempPath(), "MoonMovie-update");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"MoonMovie-{update.Version.ToString(3)}-x64.msix");
        if (File.Exists(path) && update.MsixBytes > 0 && new FileInfo(path).Length == update.MsixBytes) return path;

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? update.MsixBytes;
        var part = path + ".part";
        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var target = File.Create(part))
        {
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        File.Move(part, path, overwrite: true);
        return path;
    }

    /// <summary>"v1.2.0" / "1.2" → 1.2.0.</summary>
    public static bool TryParse(string tag, out Version version)
    {
        var ok = Version.TryParse(tag.Trim().TrimStart('v', 'V'), out var parsed);
        version = ok ? Normalize(parsed!) : new Version(0, 0, 0);
        return ok;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));
}

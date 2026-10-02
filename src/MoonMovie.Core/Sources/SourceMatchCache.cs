using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoonMovie.Core.Configuration;

namespace MoonMovie.Core.Sources;

public sealed record CachedSource(SourceCandidate Candidate, ProbeResult? Probe);

public sealed record CachedSources(DateTimeOffset SavedAt, IReadOnlyList<CachedSource> Items);

/// <summary>
/// Which resource sites had a title last time, with their episode lists and latency: the detail page shows them
/// at once and refreshes in the background instead of waiting for every site again.
/// </summary>
public sealed class SourceMatchCache
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    private readonly string _folder = Path.Combine(AppPaths.Root, "cache", "sources");

    public SourceMatchCache()
    {
        Directory.CreateDirectory(_folder);
    }

    public CachedSources? Load(string key)
    {
        var path = PathFor(key);
        try
        {
            if (!File.Exists(path)) return null;
            var cached = JsonSerializer.Deserialize(File.ReadAllText(path), SourceCacheJsonContext.Default.CachedSources);
            return cached is not null && DateTimeOffset.Now - cached.SavedAt < MaxAge && cached.Items.Count > 0 ? cached : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    public void Save(string key, IReadOnlyList<CachedSource> items)
    {
        if (items.Count == 0) return;
        try
        {
            var tmp = PathFor(key) + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new CachedSources(DateTimeOffset.Now, items), SourceCacheJsonContext.Default.CachedSources));
            File.Move(tmp, PathFor(key), overwrite: true);
        }
        catch (IOException)
        {
        }
    }

    public void Clear()
    {
        foreach (var file in Directory.EnumerateFiles(_folder))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }

    private string PathFor(string key) =>
        Path.Combine(_folder, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..20] + ".json");
}

[JsonSerializable(typeof(CachedSources))]
internal sealed partial class SourceCacheJsonContext : JsonSerializerContext;

using System.Security.Cryptography;
using System.Text;

namespace MoonMovie.Core.Caching;

/// <summary>
/// Stores raw response bodies on disk; freshness comes from the file timestamp.
/// </summary>
public sealed class JsonDiskCache(string directory)
{
    public readonly record struct Entry(string Body, DateTimeOffset StoredAt)
    {
        public bool IsFresh(TimeSpan ttl) => DateTimeOffset.UtcNow - StoredAt < ttl;
    }

    public async Task<Entry?> TryGetAsync(string key, CancellationToken ct = default)
    {
        var path = PathFor(key);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var body = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return new Entry(body, File.GetLastWriteTimeUtc(path));
        }
        catch (IOException)
        {
            return null;
        }
    }

    public async Task SetAsync(string key, string body, CancellationToken ct = default)
    {
        var path = PathFor(key);
        var tmp = path + "." + Environment.CurrentManagedThreadId + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tmp, body, ct).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
        }
        catch (IOException)
        {
            TryDelete(tmp);
        }
    }

    private string PathFor(string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(directory, hash[..32] + ".json");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }
}

using System.Security.Cryptography;
using System.Text;
using MoonMovie.Core.Configuration;

namespace MoonMovie.Core.Caching;

/// <summary>
/// Least-recently-used disk cache of streamed HLS segments (and their keys), keyed by upstream URL. Re-watching,
/// seeking back past the player's memory buffer and offline downloads of something already watched all read from
/// here instead of the network. The size limit comes from settings and is enforced after every write.
/// </summary>
public sealed class SegmentCache
{
    private readonly object _gate = new();
    private readonly Func<long> _limitBytes;
    private long _size = -1;

    public SegmentCache(Func<long> limitBytes, string? folder = null)
    {
        _limitBytes = limitBytes;
        Folder = folder ?? Path.Combine(AppPaths.Root, "cache", "segments");
        Directory.CreateDirectory(Folder);
    }

    public string Folder { get; }

    public long SizeBytes
    {
        get
        {
            lock (_gate)
            {
                if (_size < 0) _size = Measure();
                return _size;
            }
        }
    }

    /// <summary>The cached file for a URL (its access time refreshed), or null.</summary>
    public string? TryGet(string url)
    {
        var path = PathFor(url);
        if (!File.Exists(path)) return null;
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow); // LRU marker
        }
        catch (IOException)
        {
        }

        return path;
    }

    /// <summary>A temp file to write a download into; <see cref="Commit"/> moves it into the cache.</summary>
    public string NewTempFile() => Path.Combine(Folder, Guid.NewGuid().ToString("N") + ".tmp");

    public void Commit(string url, string tempFile)
    {
        if (_limitBytes() <= 0)
        {
            TryDelete(tempFile);
            return;
        }

        var target = PathFor(url);
        try
        {
            var length = new FileInfo(tempFile).Length;
            File.Move(tempFile, target, overwrite: true);
            lock (_gate)
            {
                if (_size >= 0) _size += length;
            }
        }
        catch (IOException)
        {
            TryDelete(tempFile);
            return;
        }

        Trim();
    }

    public void Store(string url, ReadOnlySpan<byte> data)
    {
        var temp = NewTempFile();
        File.WriteAllBytes(temp, data.ToArray());
        Commit(url, temp);
    }

    public void Clear()
    {
        lock (_gate)
        {
            foreach (var file in Directory.EnumerateFiles(Folder)) TryDelete(file);
            _size = 0;
        }
    }

    /// <summary>Deletes the least recently used files until the cache fits its limit (with 10 % headroom).</summary>
    public void Trim()
    {
        var limit = _limitBytes();
        lock (_gate)
        {
            if (_size < 0) _size = Measure();
            if (_size <= limit) return;

            var files = new DirectoryInfo(Folder).EnumerateFiles("*.seg")
                .OrderBy(f => f.LastWriteTimeUtc)
                .ToList();
            var target = (long)(limit * 0.9);
            foreach (var file in files)
            {
                if (_size <= target) break;
                var length = file.Length;
                if (TryDelete(file.FullName)) _size -= length;
            }
        }
    }

    private string PathFor(string url) =>
        Path.Combine(Folder, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))) + ".seg");

    private long Measure() => new DirectoryInfo(Folder).EnumerateFiles("*.seg").Sum(f => f.Length);

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

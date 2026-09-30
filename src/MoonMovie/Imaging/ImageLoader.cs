using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml.Media.Imaging;
using MoonMovie.Core.Configuration;
using Windows.Storage.Streams;

namespace MoonMovie.Imaging;

/// <summary>
/// Downloads images to a disk cache (shared across TMDB mirrors), decodes them at display size and keeps a
/// small in-memory LRU of decoded bitmaps. Decoding members must be called on the UI thread.
/// </summary>
public sealed class ImageLoader(HttpClient http, IReadOnlyList<string> mirrorRoots)
{
    private const int MemoryCapacity = 360;

    private readonly SemaphoreSlim _downloadGate = new(8);
    private readonly SemaphoreSlim _priorityGate = new(4);
    private readonly ConcurrentDictionary<string, Task<string?>> _inflight = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, BitmapImage Image)>> _memory = new();
    private readonly LinkedList<(string Key, BitmapImage Image)> _lru = new();

    public BitmapImage? TryGetDecoded(string url, int decodeWidth)
    {
        var key = MemoryKey(url, decodeWidth);
        if (!_memory.TryGetValue(key, out var node))
        {
            return null;
        }

        _lru.Remove(node);
        _lru.AddFirst(node);
        return node.Value.Image;
    }

    public async Task<BitmapImage?> LoadAsync(string url, int decodeWidth, CancellationToken ct = default,
        bool highPriority = false)
    {
        if (TryGetDecoded(url, decodeWidth) is { } hit)
        {
            return hit;
        }

        var file = await GetFileAsync(url, ct, highPriority);
        if (file is null || ct.IsCancellationRequested)
        {
            return null;
        }

        var bitmap = new BitmapImage { DecodePixelType = DecodePixelType.Logical };
        if (decodeWidth > 0)
        {
            bitmap.DecodePixelWidth = decodeWidth;
        }

        try
        {
            // XAML may decode the source again later (recycling, resize, memory trim), so the stream must stay
            // alive for the bitmap's lifetime. Disposing it after SetSourceAsync crashes inside Microsoft.UI.Xaml.
            var bytes = await File.ReadAllBytesAsync(file, ct);
            var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            await bitmap.SetSourceAsync(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            TryDelete(file);
            return null;
        }

        Remember(MemoryKey(url, decodeWidth), bitmap);
        return bitmap;
    }

    public bool IsCached(string url) => File.Exists(CachePath(url));

    /// <summary>Warms the disk cache without decoding.</summary>
    public void Prefetch(string? url, bool highPriority = false)
    {
        if (!string.IsNullOrEmpty(url) && !IsCached(url))
        {
            _ = GetFileAsync(url, default, highPriority);
        }
    }

    /// <summary>Returns the local cache path, downloading once even under concurrent requests.</summary>
    public Task<string?> GetFileAsync(string url, CancellationToken ct = default, bool highPriority = false)
    {
        var (_, suffix) = SplitMirror(url);
        var path = CachePath(url);
        if (File.Exists(path))
        {
            return Task.FromResult<string?>(path);
        }

        var task = _inflight.GetOrAdd(path, _ => DownloadAsync(url, suffix, path, highPriority ? _priorityGate : _downloadGate));
        _ = task.ContinueWith(_ => _inflight.TryRemove(path, out Task<string?>? _), TaskScheduler.Default);
        return ct.CanBeCanceled ? task.WaitAsync(ct).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null,
            TaskScheduler.Default) : task;
    }

    private async Task<string?> DownloadAsync(string url, string? mirrorSuffix, string path, SemaphoreSlim gate)
    {
        var candidates = mirrorSuffix is null
            ? [url]
            : mirrorRoots.Select(r => r + mirrorSuffix).Prepend(url).Distinct().ToArray();

        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var candidate in candidates)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                    using var response = await http.GetAsync(candidate, HttpCompletionOption.ResponseHeadersRead,
                        timeout.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        continue;
                    }

                    var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
                    await using (var file = File.Create(tmp))
                    {
                        await response.Content.CopyToAsync(file, timeout.Token).ConfigureAwait(false);
                    }

                    File.Move(tmp, path, overwrite: true);
                    return path;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
                {
                    // Try the next mirror.
                }
            }

            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    private string CachePath(string url) =>
        Path.Combine(AppPaths.ImageCache, Hash(SplitMirror(url).CacheKey) + Extension(url));

    private (string CacheKey, string? Suffix) SplitMirror(string url)
    {
        foreach (var root in mirrorRoots)
        {
            if (url.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                var suffix = url[root.Length..];
                return ("tmdb:" + suffix, suffix);
            }
        }

        return (url, null);
    }

    private void Remember(string key, BitmapImage image)
    {
        if (_memory.TryGetValue(key, out var existing))
        {
            _lru.Remove(existing);
        }

        var node = _lru.AddFirst((key, image));
        _memory[key] = node;

        while (_lru.Count > MemoryCapacity)
        {
            var last = _lru.Last!;
            _lru.RemoveLast();
            _memory.Remove(last.Value.Key);
        }
    }

    private static string MemoryKey(string url, int width) => width + "|" + url;

    private static string Hash(string value) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Extension(string url)
    {
        var path = url.Split('?', 2)[0];
        var ext = Path.GetExtension(path);
        return ext.Length is > 1 and <= 5 ? ext.ToLowerInvariant() : ".img";
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }
}

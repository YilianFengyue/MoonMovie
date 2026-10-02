using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoonMovie.Core.Caching;
using MoonMovie.Core.Configuration;
using MoonMovie.Core.Local;
using MoonMovie.Core.Models;
using MoonMovie.Core.Playback;
using MoonMovie.Core.Settings;

namespace MoonMovie.Core.Downloads;

public enum DownloadState
{
    Queued,
    Running,
    Paused,
    Completed,
    Failed,
}

/// <summary>One episode (or film) being saved for offline viewing. Persisted; the manager mutates it.</summary>
public sealed class DownloadItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public int TmdbId { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter<MediaKind>))]
    public MediaKind Kind { get; set; }

    public string Title { get; set; } = "";

    public string? OriginalTitle { get; set; }

    public int? Year { get; set; }

    public string? PosterPath { get; set; }

    public string? BackdropPath { get; set; }

    public List<int> GenreIds { get; set; } = [];

    public int? Season { get; set; }

    public int EpisodeIndex { get; set; }

    /// <summary>"第 3 集", or a version name for films with several.</summary>
    public string? EpisodeLabel { get; set; }

    public string SourceUrl { get; set; } = "";

    public string SiteName { get; set; } = "";

    public string? OutputPath { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter<DownloadState>))]
    public DownloadState State { get; set; }

    public string? Error { get; set; }

    public double Progress { get; set; }

    public long BytesDone { get; set; }

    /// <summary>Estimated final size (bytes so far scaled by progress) while running; exact once done.</summary>
    public long BytesTotal { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset? CompletedAt { get; set; }

    [JsonIgnore]
    public double BytesPerSecond { get; set; }

    [JsonIgnore]
    public string MediaKey => $"tmdb:{(Kind == MediaKind.Movie ? "movie" : "tv")}:{TmdbId}";

    public MediaItem ToMediaItem() =>
        new(TmdbId, Kind, Title, OriginalTitle, null, Year, 0, 0, PosterPath, BackdropPath, GenreIds, null);
}

/// <param name="Url">Episode URL on the resource site (HLS playlist or a direct file).</param>
public sealed record DownloadRequest(MediaItem Item, int? Season, int EpisodeIndex, string? EpisodeLabel, string Url, string SiteName);

/// <summary>
/// Offline downloads: HLS episodes are saved without the spliced ads (the same cleaning playback does), segment by
/// segment so a pause or a crash resumes where it stopped, decrypted (AES-128) and joined into one file under the
/// download folder. That folder is part of the local library, so finished episodes show up in 「本地」, play from
/// the detail page without a network, and keep their TMDB identity.
/// </summary>
public sealed class DownloadManager
{
    private const int SegmentConcurrency = 4;
    private const string BrowserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Safari/537.36";

    private readonly HttpClient _http;
    private readonly SegmentCache _cache;
    private readonly SettingsStore _settings;
    private readonly LocalLibrary _library;
    private readonly RateLimiter _limiter;
    private readonly string _path = Path.Combine(AppPaths.Data, "downloads.json");
    private readonly object _gate = new();
    private readonly Dictionary<string, CancellationTokenSource> _running = [];
    private readonly List<DownloadItem> _items;

    public DownloadManager(HttpClient http, SegmentCache cache, SettingsStore settings, LocalLibrary library)
    {
        _http = http;
        _cache = cache;
        _settings = settings;
        _library = library;
        _limiter = new RateLimiter(() => (long)_settings.Current.Downloads.SpeedLimitMb << 20);
        _items = Load();

        // Interrupted by quitting: carry on next time.
        foreach (var item in _items.Where(i => i.State == DownloadState.Running)) item.State = DownloadState.Queued;
    }

    /// <summary>Raised on a background thread: an item's progress or state (null: the list itself) changed.</summary>
    public event Action<DownloadItem?>? Changed;

    /// <summary>Raised on a background thread when an item completes or fails (not when paused or removed).</summary>
    public event Action<DownloadItem>? Finished;

    public IReadOnlyList<DownloadItem> Items
    {
        get
        {
            lock (_gate) return _items.ToArray();
        }
    }

    public int ActiveCount
    {
        get
        {
            lock (_gate) return _items.Count(i => i.State is DownloadState.Queued or DownloadState.Running);
        }
    }

    public string Folder => _settings.Current.Downloads.Folder is { Length: > 0 } f ? f : DownloadSettings.DefaultFolder;

    public void Start() => Pump();

    /// <summary>Queues episodes; ones already queued or saved are skipped. Returns how many were added.</summary>
    public int Enqueue(IEnumerable<DownloadRequest> requests)
    {
        var added = 0;
        lock (_gate)
        {
            foreach (var r in requests)
            {
                var key = r.Item.MediaKey;
                if (_items.Any(i => i.MediaKey == key && i.Season == r.Season && i.EpisodeIndex == r.EpisodeIndex
                                    && i.State != DownloadState.Failed))
                {
                    continue;
                }

                _items.RemoveAll(i => i.MediaKey == key && i.Season == r.Season && i.EpisodeIndex == r.EpisodeIndex);
                _items.Add(new DownloadItem
                {
                    TmdbId = r.Item.TmdbId,
                    Kind = r.Item.Kind,
                    Title = r.Item.Title,
                    OriginalTitle = r.Item.OriginalTitle,
                    Year = r.Item.Year,
                    PosterPath = r.Item.PosterPath,
                    BackdropPath = r.Item.BackdropPath,
                    GenreIds = r.Item.GenreIds.ToList(),
                    Season = r.Item.Kind == MediaKind.Tv ? r.Season ?? 1 : null,
                    EpisodeIndex = r.EpisodeIndex,
                    EpisodeLabel = r.EpisodeLabel,
                    SourceUrl = r.Url,
                    SiteName = r.SiteName,
                    State = DownloadState.Queued,
                });
                added++;
            }

            Save();
        }

        Changed?.Invoke(null);
        Pump();
        return added;
    }

    public void Pause(string id)
    {
        lock (_gate)
        {
            if (Find(id) is not { State: DownloadState.Queued or DownloadState.Running } item) return;
            item.State = DownloadState.Paused;
            item.BytesPerSecond = 0;
            if (_running.Remove(id, out var cts)) cts.Cancel();
            Save();
        }

        Changed?.Invoke(null);
        Pump();
    }

    public void Resume(string id)
    {
        lock (_gate)
        {
            if (Find(id) is not { State: DownloadState.Paused or DownloadState.Failed } item) return;
            item.State = DownloadState.Queued;
            item.Error = null;
            Save();
        }

        Changed?.Invoke(null);
        Pump();
    }

    /// <summary>Drops the task and its partial data; <paramref name="deleteFile"/> also deletes a finished file.</summary>
    public void Remove(string id, bool deleteFile)
    {
        DownloadItem? item;
        lock (_gate)
        {
            item = Find(id);
            if (item is null) return;
            if (_running.Remove(id, out var cts)) cts.Cancel();
            _items.Remove(item);
            Save();
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(500).ConfigureAwait(false); // let a cancelled worker let go of its files
            TryDeleteDirectory(PartsFolder(item));
            if (deleteFile && item.OutputPath is { } output) TryDeleteFile(output);

            // Unfinished: the title folder made for it, and the parts root, if nothing else is in them.
            DeleteIfEmpty(Path.GetDirectoryName(OutputPathFor(item)));
            DeleteIfEmpty(Path.Combine(Folder, ".moonmovie-parts"));
        });
        Changed?.Invoke(null);
        Pump();
    }

    public DownloadItem? Find(string id) => _items.FirstOrDefault(i => i.Id == id);

    /// <summary>Starts queued items while there are free slots.</summary>
    private void Pump()
    {
        lock (_gate)
        {
            var slots = Math.Clamp(_settings.Current.Downloads.Concurrent, 1, 4) - _running.Count;
            foreach (var item in _items.Where(i => i.State == DownloadState.Queued).Take(Math.Max(0, slots)).ToArray())
            {
                var cts = new CancellationTokenSource();
                _running[item.Id] = cts;
                item.State = DownloadState.Running;
                _ = Task.Run(() => RunAsync(item, cts.Token));
            }
        }
    }

    private async Task RunAsync(DownloadItem item, CancellationToken ct)
    {
        Changed?.Invoke(null);
        try
        {
            var output = OutputPathFor(item);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            if (item.SourceUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                output = await DownloadHlsAsync(item, output, ct).ConfigureAwait(false);
            }
            else
            {
                output = Path.ChangeExtension(output, Path.GetExtension(new Uri(item.SourceUrl).AbsolutePath) is { Length: > 1 and <= 5 } ext ? ext : ".mp4");
                await DownloadFileAsync(item, output, ct).ConfigureAwait(false);
            }

            lock (_gate)
            {
                item.OutputPath = output;
                item.State = DownloadState.Completed;
                item.Progress = 1;
                item.BytesPerSecond = 0;
                item.BytesTotal = item.BytesDone = new FileInfo(output).Length;
                item.CompletedAt = DateTimeOffset.Now;
                Save();
            }

            // Into the local library, already identified.
            _library.Remember(output, item.ToMediaItem());
            await _library.AddFolderAsync(Folder).ConfigureAwait(false);
            _ = _library.RescanAsync();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Paused or removed.
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or CryptographicException
                                       or TaskCanceledException or UnauthorizedAccessException)
        {
            lock (_gate)
            {
                item.State = DownloadState.Failed;
                item.BytesPerSecond = 0;
                item.Error = ex switch
                {
                    HttpRequestException { StatusCode: { } code } => $"片源返回 {(int)code}",
                    HttpRequestException or TaskCanceledException => "网络连接失败",
                    IOException when ex.HResult == unchecked((int)0x80070070) => "磁盘空间不足",
                    _ => ex.Message,
                };
                Save();
            }
        }
        finally
        {
            lock (_gate) _running.Remove(item.Id);
            Changed?.Invoke(item);
            if (item.State is DownloadState.Completed or DownloadState.Failed) Finished?.Invoke(item);
            Pump();
        }
    }

    // ----- HLS --------------------------------------------------------------------------------------------

    private async Task<string> DownloadHlsAsync(DownloadItem item, string output, CancellationToken ct)
    {
        var media = await HlsPlaylist.LoadAsync(_http, new Uri(item.SourceUrl), Decorate, ct).ConfigureAwait(false);
        if (media.Segments.Count == 0) throw new InvalidDataException("播放列表是空的");
#if DEBUG
        // Test hook: MOONMOVIE_DEBUG_DL_SEGMENTS=N keeps only the first N segments (a whole flow in seconds).
        if (int.TryParse(Environment.GetEnvironmentVariable("MOONMOVIE_DEBUG_DL_SEGMENTS"), out var keep) && keep > 0)
        {
            media = media with { Segments = media.Segments.Take(keep).ToArray() };
        }
#endif

        var parts = PartsFolder(item);
        Directory.CreateDirectory(parts);
        var keys = new Dictionary<string, byte[]>();
        foreach (var key in media.Segments.Select(s => s.Key).Where(k => k?.Uri is not null).DistinctBy(k => k!.Uri!.AbsoluteUri))
        {
            if (key!.Method != "AES-128") throw new InvalidDataException($"不支持的加密方式 {key.Method}");
            keys[key.Uri!.AbsoluteUri] = await FetchAsync(key.Uri, ct).ConfigureAwait(false);
        }

        var done = media.Segments.Select((_, i) => File.Exists(PartFile(parts, i))).Count(x => x);
        var bytes = Directory.EnumerateFiles(parts, "*.part").Sum(f => new FileInfo(f).Length);
        var speed = new SpeedMeter();
        Report(item, done, media.Segments.Count, bytes, speed);

        await Parallel.ForEachAsync(Enumerable.Range(0, media.Segments.Count),
            new ParallelOptions { MaxDegreeOfParallelism = SegmentConcurrency, CancellationToken = ct },
            async (index, token) =>
            {
                var file = PartFile(parts, index);
                if (File.Exists(file)) return;

                var segment = media.Segments[index];
                var data = await FetchWithRetryAsync(segment.Uri, token).ConfigureAwait(false);
                if (segment.Key is { Method: "AES-128", Uri: { } keyUri })
                {
                    using var aes = Aes.Create();
                    aes.Key = keys[keyUri.AbsoluteUri];
                    data = aes.DecryptCbc(data, HlsPlaylist.IvFor(segment), PaddingMode.PKCS7);
                }

                var temp = file + ".tmp";
                await File.WriteAllBytesAsync(temp, data, token).ConfigureAwait(false);
                File.Move(temp, file, overwrite: true);

                speed.Add(data.Length);
                Report(item, Interlocked.Increment(ref done), media.Segments.Count, Interlocked.Add(ref bytes, data.Length), speed);
            }).ConfigureAwait(false);

        // fMP4 streams start with their init segment and become an .mp4; MPEG-TS stays .ts.
        var map = media.Segments.Select(s => s.Map).FirstOrDefault(m => m is not null);
        if (map is not null) output = Path.ChangeExtension(output, ".mp4");
        var temp = output + ".joining";
        await using (var target = File.Create(temp))
        {
            if (map is not null) await target.WriteAsync(await FetchAsync(map, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            for (var i = 0; i < media.Segments.Count; i++)
            {
                await using var part = File.OpenRead(PartFile(parts, i));
                await part.CopyToAsync(target, ct).ConfigureAwait(false);
            }
        }

        File.Move(temp, output, overwrite: true);
        TryDeleteDirectory(parts);
        DeleteIfEmpty(Path.GetDirectoryName(parts));
        return output;
    }

    private static string PartFile(string parts, int index) => Path.Combine(parts, $"{index:D5}.part");

    private string PartsFolder(DownloadItem item) => Path.Combine(Folder, ".moonmovie-parts", item.Id);

    private async Task<byte[]> FetchWithRetryAsync(Uri uri, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await FetchAsync(uri, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < 4 && ex is HttpRequestException or IOException
                                           || (ex is TaskCanceledException && !ct.IsCancellationRequested && attempt < 4))
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>From the playback cache when the segment was watched already, else the network (rate limited).</summary>
    private async Task<byte[]> FetchAsync(Uri uri, CancellationToken ct)
    {
        if (_cache.TryGet(uri.AbsoluteUri) is { } cached) return await File.ReadAllBytesAsync(cached, ct).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        Decorate(request);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var memory = new MemoryStream(response.Content.Headers.ContentLength is { } n and < 64 << 20 ? (int)n : 1 << 20);
        var buffer = new byte[65536];
        int read;
        while ((read = await body.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
        {
            await _limiter.WaitAsync(read, timeout.Token).ConfigureAwait(false);
            memory.Write(buffer, 0, read);
        }

        return memory.ToArray();
    }

    // ----- Direct files -----------------------------------------------------------------------------------

    private async Task DownloadFileAsync(DownloadItem item, string output, CancellationToken ct)
    {
        var partial = output + ".part";
        var have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, item.SourceUrl);
        Decorate(request);
        if (have > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != System.Net.HttpStatusCode.PartialContent) have = 0; // server ignored the range

        var total = (response.Content.Headers.ContentLength ?? 0) + have;
        var speed = new SpeedMeter();
        await using (var file = new FileStream(partial, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write))
        await using (var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        {
            var buffer = new byte[131072];
            int read;
            var done = have;
            while ((read = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await _limiter.WaitAsync(read, ct).ConfigureAwait(false);
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                speed.Add(read);
                lock (_gate)
                {
                    item.BytesDone = done;
                    item.BytesTotal = total;
                    item.Progress = total > 0 ? (double)done / total : 0;
                    item.BytesPerSecond = speed.BytesPerSecond;
                }

                Changed?.Invoke(item);
            }
        }

        File.Move(partial, output, overwrite: true);
    }

    // ----- Helpers ----------------------------------------------------------------------------------------

    private void Report(DownloadItem item, int done, int total, long bytes, SpeedMeter speed)
    {
        lock (_gate)
        {
            item.Progress = total > 0 ? (double)done / total : 0;
            item.BytesDone = bytes;
            item.BytesTotal = done > 0 ? (long)(bytes * (double)total / done) : 0;
            item.BytesPerSecond = speed.BytesPerSecond;
            if (done % 20 == 0) Save(); // resume info is on disk anyway (the part files); keep the list fresh too
        }

        Changed?.Invoke(item);
    }

    /// <summary>"绝命毒师\绝命毒师 S01E03.ts" / "星际穿越 (2014)\星际穿越 (2014).ts": names the library parses back.</summary>
    private string OutputPathFor(DownloadItem item)
    {
        var title = Safe(item.Title);
        if (item.Kind == MediaKind.Tv)
        {
            var name = $"{title} S{item.Season ?? 1:00}E{item.EpisodeIndex + 1:00}";
            return Path.Combine(Folder, title, name + ".ts");
        }

        var film = item.Year is { } y ? $"{title} ({y})" : title;
        var version = item.EpisodeIndex > 0 && item.EpisodeLabel is { Length: > 0 } label ? $" - {Safe(label)}" : string.Empty;
        return Path.Combine(Folder, film, film + version + ".ts");
    }

    private static string Safe(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? ' ' : c).ToArray()).Trim().TrimEnd('.');
        return cleaned.Length > 0 ? cleaned : "未命名";
    }

    private static void Decorate(HttpRequestMessage request) => request.Headers.UserAgent.ParseAdd(BrowserAgent);

    private List<DownloadItem> Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize(File.ReadAllText(_path), DownloadJsonContext.Default.ListDownloadItem) ?? []
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return [];
        }
    }

    private void Save()
    {
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_items, DownloadJsonContext.Default.ListDownloadItem));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (IOException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteIfEmpty(string? folder)
    {
        try
        {
            if (folder is not null && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
            var folder = Path.GetDirectoryName(path);
            if (folder is not null && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Bytes per second over the last few seconds.</summary>
    private sealed class SpeedMeter
    {
        private readonly Queue<(DateTimeOffset At, long Bytes)> _samples = new();
        private readonly object _gate = new();

        public void Add(long bytes)
        {
            lock (_gate)
            {
                _samples.Enqueue((DateTimeOffset.Now, bytes));
                while (_samples.Count > 0 && DateTimeOffset.Now - _samples.Peek().At > TimeSpan.FromSeconds(4)) _samples.Dequeue();
            }
        }

        public double BytesPerSecond
        {
            get
            {
                lock (_gate)
                {
                    if (_samples.Count < 2) return 0;
                    var span = (DateTimeOffset.Now - _samples.Peek().At).TotalSeconds;
                    return span > 0.2 ? _samples.Sum(s => s.Bytes) / span : 0;
                }
            }
        }
    }

    /// <summary>Token bucket shared by every download; a zero limit means unlimited.</summary>
    private sealed class RateLimiter(Func<long> bytesPerSecond)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private double _tokens;
        private DateTimeOffset _last = DateTimeOffset.Now;

        public async Task WaitAsync(int bytes, CancellationToken ct)
        {
            var rate = bytesPerSecond();
            if (rate <= 0) return;

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var now = DateTimeOffset.Now;
                _tokens = Math.Min(rate, _tokens + (now - _last).TotalSeconds * rate);
                _last = now;
                _tokens -= bytes;
                if (_tokens < 0) await Task.Delay(TimeSpan.FromSeconds(-_tokens / rate), ct).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}

[JsonSerializable(typeof(List<DownloadItem>))]
internal sealed partial class DownloadJsonContext : JsonSerializerContext;

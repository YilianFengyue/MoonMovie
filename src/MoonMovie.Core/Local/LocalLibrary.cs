using System.Text.Json;
using System.Text.Json.Serialization;
using MoonMovie.Core.Configuration;
using MoonMovie.Core.Models;
using MoonMovie.Core.Tmdb;

namespace MoonMovie.Core.Local;

/// <summary>A video file (or a Blu-ray / DVD folder) found in a watched folder, with what its name says.</summary>
public sealed class LocalFile
{
    public string Path { get; set; } = "";

    public long Size { get; set; }

    public DateTime ModifiedUtc { get; set; }

    /// <summary>A BDMV / VIDEO_TS folder rather than a file.</summary>
    public bool IsDisc { get; set; }

    public string Title { get; set; } = "";

    public string? AltTitle { get; set; }

    public int? Year { get; set; }

    public int? Season { get; set; }

    public int? Episode { get; set; }

    public int? TrailingNumber { get; set; }

    public bool Anime { get; set; }

    [JsonIgnore]
    public string FileName => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Path));

    internal static LocalFile Create(string path, long size, DateTime modified, bool isDisc)
    {
        var parsed = LocalNameParser.Parse(path, isDisc);
        return new LocalFile
        {
            Path = path,
            Size = size,
            ModifiedUtc = modified,
            IsDisc = isDisc,
            Title = parsed.Title,
            AltTitle = parsed.AltTitle,
            Year = parsed.Year,
            Season = parsed.Season,
            Episode = parsed.Episode,
            TrailingNumber = parsed.TrailingNumber,
            Anime = parsed.LooksLikeAnime,
        };
    }
}

/// <summary>What TMDB says a local title is (or that it found nothing).</summary>
public sealed class LocalMatch
{
    public int TmdbId { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter<MediaKind>))]
    public MediaKind Kind { get; set; }

    public string Title { get; set; } = "";

    public string? OriginalTitle { get; set; }

    public string? Overview { get; set; }

    public int? Year { get; set; }

    public double Rating { get; set; }

    public int VoteCount { get; set; }

    public string? PosterPath { get; set; }

    public string? BackdropPath { get; set; }

    public List<int> GenreIds { get; set; } = [];

    /// <summary>Chosen (or cleared) by the user: never replaced automatically.</summary>
    public bool Manual { get; set; }

    /// <summary>Searched and nothing fitted; retried after a while.</summary>
    public bool NotFound { get; set; }

    public DateTimeOffset CheckedAt { get; set; }

    public MediaItem ToMediaItem() =>
        new(TmdbId, Kind, Title, OriginalTitle, Overview, Year, Rating, VoteCount, PosterPath, BackdropPath, GenreIds, null);

    public static LocalMatch From(MediaItem item, bool manual) => new()
    {
        TmdbId = item.TmdbId,
        Kind = item.Kind,
        Title = item.Title,
        OriginalTitle = item.OriginalTitle,
        Overview = item.Overview,
        Year = item.Year,
        Rating = item.Rating,
        VoteCount = item.VoteCount,
        PosterPath = item.PosterPath,
        BackdropPath = item.BackdropPath,
        GenreIds = item.GenreIds.ToList(),
        Manual = manual,
        CheckedAt = DateTimeOffset.Now,
    };
}

/// <summary>One movie or series made of local files, matched to TMDB when possible.</summary>
public sealed class LocalTitle
{
    internal LocalTitle(string key, IReadOnlyList<LocalFile> files, LocalMatch? match)
    {
        Key = key;
        Files = files;
        Match = match is { NotFound: false } ? match : null;
        ParsedTitle = files.GroupBy(f => f.Title).OrderByDescending(g => g.Count()).First().Key;
        AltTitle = files.Select(f => f.AltTitle).FirstOrDefault(a => a is not null);
        IsSeries = files.Any(f => f.Episode is not null);
        Year = files.Select(f => f.Year).FirstOrDefault(y => y is not null);
        Anime = files.Any(f => f.Anime);
        LastAdded = files.Max(f => f.ModifiedUtc);
    }

    /// <summary>Stable grouping key: normalised title plus kind (and year for films).</summary>
    public string Key { get; }

    public IReadOnlyList<LocalFile> Files { get; }

    public LocalMatch? Match { get; }

    public bool IsMatched => Match is not null;

    public string ParsedTitle { get; }

    public string? AltTitle { get; }

    public string Title => Match?.Title ?? ParsedTitle;

    public bool IsSeries { get; }

    public MediaKind Kind => Match?.Kind ?? (IsSeries ? MediaKind.Tv : MediaKind.Movie);

    public int? Year { get; }

    /// <summary>Fansub naming or TMDB's animation genre.</summary>
    public bool Anime { get; }

    public bool IsAnimation => Anime || Match?.GenreIds.Contains(TmdbGenres.Animation) == true;

    public DateTime LastAdded { get; }

    public IReadOnlyList<int> Seasons => Files.Select(f => f.Season ?? 1).Distinct().Order().ToArray();

    /// <summary>Files of a season in episode order; files without a number follow, by name.</summary>
    public IReadOnlyList<LocalFile> Episodes(int? season)
    {
        var s = season ?? Seasons.FirstOrDefault(1);
        return Files.Where(f => !IsSeries || (f.Season ?? 1) == s)
            .OrderBy(f => f.Episode ?? int.MaxValue)
            .ThenBy(f => f.FileName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public MediaItem ToMediaItem() => Match?.ToMediaItem()
        ?? new MediaItem(0, Kind, ParsedTitle, AltTitle, null, Year, 0, 0, null, null, [], null) { LocalKey = "local:" + Key };

    public string MediaKey => ToMediaItem().MediaKey;
}

/// <summary>
/// The 「本地」 library: watched folders, scanned into titles grouped from file names and matched against TMDB.
/// A matched title shares its media key with the online catalogue, so progress, favourites and the detail page
/// are the same whether it plays from disk or a resource site.
/// </summary>
public sealed class LocalLibrary : IDisposable
{
    private const long MinFileBytes = 2L << 20;
    private const long SampleBytes = 200L << 20;
    private static readonly TimeSpan RetryNotFound = TimeSpan.FromDays(3);

    private readonly TmdbClient _tmdb;
    private readonly string _path = Path.Combine(AppPaths.Data, "local-library.json");
    private readonly object _gate = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private LocalLibraryData _data;
    private IReadOnlyList<LocalTitle> _titles = [];
    private Timer? _debounce;
    private bool _initialized;

    public LocalLibrary(TmdbClient tmdb)
    {
        _tmdb = tmdb;
        _data = Load();
        _titles = BuildTitles(_data);
    }

    /// <summary>Raised (on a background thread) whenever folders, files or matches change.</summary>
    public event Action? Changed;

    public IReadOnlyList<string> Folders
    {
        get
        {
            lock (_gate) return _data.Folders.ToArray();
        }
    }

    public IReadOnlyList<LocalTitle> Titles => Volatile.Read(ref _titles);

    public bool IsScanning { get; private set; }

    public int FileCount => Titles.Sum(t => t.Files.Count);

    /// <summary>Starts watching and refreshes in the background; cheap to call more than once.</summary>
    public void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        RestartWatchers();
        if (Folders.Count > 0) _ = RescanAsync();
    }

    public async Task AddFolderAsync(string folder)
    {
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        lock (_gate)
        {
            if (_data.Folders.Any(f => f.Equals(folder, StringComparison.OrdinalIgnoreCase))) return;
            // A parent replaces its children; a child of a watched folder adds nothing.
            if (_data.Folders.Any(f => IsUnder(folder, f))) return;
            _data.Folders.RemoveAll(f => IsUnder(f, folder));
            _data.Folders.Add(folder);
            Save();
        }

        RestartWatchers();
        Changed?.Invoke();
        await RescanAsync().ConfigureAwait(false);
    }

    public void RemoveFolder(string folder)
    {
        lock (_gate)
        {
            _data.Folders.RemoveAll(f => f.Equals(folder, StringComparison.OrdinalIgnoreCase));
            _data.Files.RemoveAll(f => IsUnder(f.Path, folder));
            Save();
            Volatile.Write(ref _titles, BuildTitles(_data));
        }

        RestartWatchers();
        Changed?.Invoke();
    }

    public LocalTitle? Find(string mediaKey) => Titles.FirstOrDefault(t => t.MediaKey == mediaKey);

    public LocalTitle? FindByKey(string key) => Titles.FirstOrDefault(t => t.Key == key);

    public LocalTitle? FindByPath(string path) =>
        Titles.FirstOrDefault(t => t.Files.Any(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The user's own match (or "none" with a null item); remembered over automatic matching.</summary>
    public void SetMatch(string key, MediaItem? item)
    {
        lock (_gate)
        {
            _data.Matches[key] = item is null
                ? new LocalMatch { Manual = true, NotFound = true, CheckedAt = DateTimeOffset.Now }
                : LocalMatch.From(item, manual: true);
            Save();
            Volatile.Write(ref _titles, BuildTitles(_data));
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// A title for files opened directly (not in the library): the file plus its siblings of the same title, so the
    /// next episode is there. Not stored.
    /// </summary>
    public static LocalTitle AdHoc(string path)
    {
        var isDisc = Directory.Exists(path);
        var opened = isDisc ? LocalFile.Create(path, 0, DateTime.UtcNow, true) : FromInfo(new FileInfo(path));
        var files = new List<LocalFile> { opened };
        var folder = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
        if (!isDisc && folder is not null)
        {
            try
            {
                foreach (var sibling in Directory.EnumerateFiles(folder))
                {
                    if (sibling.Equals(path, StringComparison.OrdinalIgnoreCase) || !LocalNameParser.IsVideo(sibling)) continue;
                    files.Add(FromInfo(new FileInfo(sibling)));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        InferTrailingEpisodes(files);
        var key = KeyOf(opened);
        return new LocalTitle(key, files.Where(f => KeyOf(f) == key).ToArray(), null);

        static LocalFile FromInfo(FileInfo info) => LocalFile.Create(info.FullName, info.Length, info.LastWriteTimeUtc, false);
    }

    // ----- Scanning -------------------------------------------------------------------------------------

    public async Task RescanAsync()
    {
        if (!await _scanGate.WaitAsync(0).ConfigureAwait(false)) return; // one scan at a time; the next event catches up
        try
        {
            IsScanning = true;
            Changed?.Invoke();

            string[] folders;
            Dictionary<string, LocalFile> known;
            lock (_gate)
            {
                folders = _data.Folders.ToArray();
                known = _data.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
            }

            var found = await Task.Run(() => Walk(folders, known)).ConfigureAwait(false);
            lock (_gate)
            {
                _data.Files = found;
                Save();
                Volatile.Write(ref _titles, BuildTitles(_data));
            }

            Changed?.Invoke();
            await MatchPendingAsync().ConfigureAwait(false);
        }
        finally
        {
            IsScanning = false;
            _scanGate.Release();
            Changed?.Invoke();
        }
    }

    private static List<LocalFile> Walk(string[] folders, Dictionary<string, LocalFile> known)
    {
        var result = new List<LocalFile>();
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder))
            {
                // Unplugged drive: keep what we knew so the titles come back with it.
                result.AddRange(known.Values.Where(f => IsUnder(f.Path, folder)));
                continue;
            }

            WalkDirectory(folder, 0, result, known);
        }

        return result;
    }

    private static void WalkDirectory(string dir, int depth, List<LocalFile> result, Dictionary<string, LocalFile> known)
    {
        if (depth > 10) return;
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };

        try
        {
            // Blu-ray / DVD folder structures are one title each.
            if (File.Exists(Path.Combine(dir, "BDMV", "index.bdmv")) || Directory.Exists(Path.Combine(dir, "VIDEO_TS")))
            {
                result.Add(known.TryGetValue(dir, out var disc) ? disc : LocalFile.Create(dir, 0, Directory.GetLastWriteTimeUtc(dir), true));
                return;
            }

            foreach (var info in new DirectoryInfo(dir).EnumerateFiles("*", options))
            {
                if (!LocalNameParser.IsVideo(info.Name) || info.Length < MinFileBytes) continue;
                if (info.Length < SampleBytes && info.Name.Contains("sample", StringComparison.OrdinalIgnoreCase)) continue;

                result.Add(known.TryGetValue(info.FullName, out var old) && old.Size == info.Length && old.ModifiedUtc == info.LastWriteTimeUtc
                    ? old
                    : LocalFile.Create(info.FullName, info.Length, info.LastWriteTimeUtc, false));
            }

            foreach (var sub in Directory.EnumerateDirectories(dir, "*", options))
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.') || name.StartsWith('$') || name.StartsWith('@')) continue;
                WalkDirectory(sub, depth + 1, result, known);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // ----- Grouping ---------------------------------------------------------------------------------------

    private static IReadOnlyList<LocalTitle> BuildTitles(LocalLibraryData data)
    {
        InferTrailingEpisodes(data.Files);
        return data.Files
            .Where(f => f.Title.Length > 0)
            .GroupBy(KeyOf)
            .Select(g => new LocalTitle(g.Key, g.ToArray(), data.Matches.GetValueOrDefault(g.Key)))
            .OrderByDescending(t => t.LastAdded)
            .ToArray();
    }

    /// <summary>"三体 01.mp4", "三体 02.mp4"… in one folder: the trailing numbers are episodes.</summary>
    private static void InferTrailingEpisodes(List<LocalFile> files)
    {
        foreach (var group in files.Where(f => f.Episode is null && f.TrailingNumber is not null)
                     .GroupBy(f => (Path.GetDirectoryName(f.Path), LocalNameParser.Normalize(f.Title))))
        {
            if (group.Count() < 2) continue;
            foreach (var f in group) f.Episode = f.TrailingNumber;
        }
    }

    private static string KeyOf(LocalFile f) =>
        LocalNameParser.Normalize(f.Title) + (f.Episode is not null ? "|tv" : $"|movie|{f.Year}");

    // ----- TMDB matching --------------------------------------------------------------------------------

    private async Task MatchPendingAsync()
    {
        var pending = Titles.Where(t =>
        {
            lock (_gate)
            {
                return !_data.Matches.TryGetValue(t.Key, out var m)
                       || (m.NotFound && !m.Manual && DateTimeOffset.Now - m.CheckedAt > RetryNotFound);
            }
        }).ToArray();

        if (pending.Length == 0 || !_tmdb.IsConfigured) return;

        using var gate = new SemaphoreSlim(3);
        var changed = false;
        await Task.WhenAll(pending.Select(async title =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var item = await IdentifyAsync(title).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_data.Matches.TryGetValue(title.Key, out var existing) && existing.Manual) return;
                    _data.Matches[title.Key] = item is null
                        ? new LocalMatch { NotFound = true, CheckedAt = DateTimeOffset.Now }
                        : LocalMatch.From(item, manual: false);
                    changed = true;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Offline: try again on the next scan.
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        if (!changed) return;
        lock (_gate)
        {
            Save();
            Volatile.Write(ref _titles, BuildTitles(_data));
        }

        Changed?.Invoke();
    }

    /// <summary>Searches by the parsed title (then the alternative spelling), with and without the year.</summary>
    public async Task<MediaItem?> IdentifyAsync(LocalTitle title, CancellationToken ct = default)
    {
        var kinds = title.IsSeries ? new[] { MediaKind.Tv } : [MediaKind.Movie, MediaKind.Tv];
        var queries = new[] { title.ParsedTitle, title.AltTitle }.Where(q => !string.IsNullOrWhiteSpace(q)).Select(q => q!).Distinct();
        foreach (var kind in kinds)
        {
            foreach (var query in queries)
            {
                var results = await _tmdb.SearchAsync(kind, query, title.Year, ct).ConfigureAwait(false);
                if (results.Count == 0 && title.Year is not null)
                {
                    results = await _tmdb.SearchAsync(kind, query, null, ct).ConfigureAwait(false);
                }

                if (Pick(results, query, title.Year) is { } best) return best;
            }
        }

        return null;
    }

    private static MediaItem? Pick(IReadOnlyList<MediaItem> results, string query, int? year)
    {
        if (results.Count == 0) return null;
        var q = LocalNameParser.Normalize(query);
        var scored = results.Take(8).Select((item, rank) =>
        {
            var names = new[] { item.Title, item.OriginalTitle }.Where(n => n is not null).Select(n => LocalNameParser.Normalize(n!)).ToArray();
            var score = 0.0;
            if (names.Any(n => n == q)) score += 100;
            else if (names.Any(n => n.Contains(q) || (n.Length > 1 && q.Contains(n)))) score += 40;
            if (year is { } y && item.Year is { } iy) score += iy == y ? 30 : Math.Abs(iy - y) == 1 ? 15 : -20;
            score += Math.Log10(1 + item.VoteCount) * 4 - rank * 3;
            return (item, score);
        }).OrderByDescending(x => x.score).First();

        return scored.score >= 40 ? scored.item : null;
    }

    // ----- Watching -------------------------------------------------------------------------------------

    private void RestartWatchers()
    {
        lock (_watchers)
        {
            foreach (var w in _watchers) w.Dispose();
            _watchers.Clear();
            foreach (var folder in Folders.Where(Directory.Exists))
            {
                try
                {
                    var watcher = new FileSystemWatcher(folder)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
                    };
                    watcher.Created += (_, e) => OnFsEvent(e.FullPath);
                    watcher.Deleted += (_, e) => OnFsEvent(e.FullPath);
                    watcher.Renamed += (_, e) => OnFsEvent(e.FullPath);
                    watcher.EnableRaisingEvents = true;
                    _watchers.Add(watcher);
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private void OnFsEvent(string path)
    {
        // Downloads grow for a while: wait for quiet before rescanning.
        if (!LocalNameParser.IsVideo(path) && Path.HasExtension(path)) return;
        _debounce?.Dispose();
        _debounce = new Timer(_ => _ = RescanAsync(), null, TimeSpan.FromSeconds(4), Timeout.InfiniteTimeSpan);
    }

    // ----- Persistence ----------------------------------------------------------------------------------

    private LocalLibraryData Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize(File.ReadAllText(_path), LocalLibraryJsonContext.Default.LocalLibraryData) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new();
        }
    }

    private void Save()
    {
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_data, LocalLibraryJsonContext.Default.LocalLibraryData));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (IOException)
        {
        }
    }

    private static bool IsUnder(string path, string folder) =>
        path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
        || path.Equals(folder, StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        _debounce?.Dispose();
        lock (_watchers)
        {
            foreach (var w in _watchers) w.Dispose();
        }
    }
}

public sealed class LocalLibraryData
{
    public List<string> Folders { get; set; } = [];

    public List<LocalFile> Files { get; set; } = [];

    public Dictionary<string, LocalMatch> Matches { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(LocalLibraryData))]
internal sealed partial class LocalLibraryJsonContext : JsonSerializerContext;

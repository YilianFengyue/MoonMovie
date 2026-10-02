using System.Text.Json;
using System.Text.Json.Serialization;
using MoonMovie.Core.Configuration;
using MoonMovie.Core.Models;

namespace MoonMovie.Core.Playback;

public sealed class WatchProgress
{
    public string MediaKey { get; set; } = "";

    public int TmdbId { get; set; }

    public MediaKind Kind { get; set; }

    public string Title { get; set; } = "";

    public string? PosterPath { get; set; }

    public string? BackdropPath { get; set; }

    public int? Season { get; set; }

    /// <summary>Zero-based index into the source's episode list.</summary>
    public int EpisodeIndex { get; set; }

    public string? EpisodeLabel { get; set; }

    /// <summary>Episodes in the source line at the time, so "next episode" can be offered without a lookup.</summary>
    public int? EpisodeCount { get; set; }

    public long PositionMs { get; set; }

    public long DurationMs { get; set; }

    public string? SourceKey { get; set; }

    /// <summary>The file played, for local titles that are not in the library (opened from Explorer).</summary>
    public string? LocalPath { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    [JsonIgnore]
    public double Fraction => DurationMs > 0 ? Math.Clamp((double)PositionMs / DurationMs, 0, 1) : 0;

    /// <summary>Into the credits: the last 90 s, or the last 8 % of something short (clips, local shorts).</summary>
    [JsonIgnore]
    public bool IsFinished => DurationMs > 0 && DurationMs - PositionMs < Math.Min(90_000, DurationMs * 0.08);

    /// <summary>A finished episode with another one after it.</summary>
    [JsonIgnore]
    public bool HasNextEpisode => Kind == MediaKind.Tv && IsFinished && EpisodeCount is { } n && EpisodeIndex + 1 < n;

    /// <summary>Something the user would want to pick up: half-watched, or the next episode is waiting.</summary>
    [JsonIgnore]
    public bool IsContinuable => !IsFinished || HasNextEpisode;

    public MediaItem ToMediaItem() =>
        new(TmdbId, Kind, Title, null, null, null, 0, 0, PosterPath, BackdropPath, [], null)
        {
            LocalKey = MediaKey.StartsWith("local:", StringComparison.Ordinal) ? MediaKey : null,
        };
}

/// <summary>Per-episode playback positions plus the "continue watching" list, persisted as JSON.</summary>
public sealed class WatchProgressStore
{
    private const int MaxEntries = 2000;

    private readonly string _path = Path.Combine(AppPaths.Data, "progress.json");
    private readonly object _gate = new();
    private Dictionary<string, WatchProgress>? _entries;
    private DateTimeOffset _lastFlush;

    public event EventHandler? Changed;

    public WatchProgress? Get(string mediaKey, int? season, int episodeIndex)
    {
        lock (_gate)
        {
            return Entries.TryGetValue(Key(mediaKey, season, episodeIndex), out var p) ? p : null;
        }
    }

    /// <summary>The most recently watched episode of a title.</summary>
    public WatchProgress? Latest(string mediaKey)
    {
        lock (_gate)
        {
            return Entries.Values.Where(e => e.MediaKey == mediaKey).MaxBy(e => e.UpdatedAt);
        }
    }

    /// <summary>One entry per title, newest first — the history / continue-watching list.</summary>
    public IReadOnlyList<WatchProgress> Recent(int count = 50)
    {
        lock (_gate)
        {
            return Entries.Values
                .GroupBy(e => e.MediaKey)
                .Select(g => g.MaxBy(e => e.UpdatedAt)!)
                .OrderByDescending(e => e.UpdatedAt)
                .Take(count)
                .ToArray();
        }
    }

    public void Save(WatchProgress progress, bool flush = false)
    {
        lock (_gate)
        {
            progress.UpdatedAt = DateTimeOffset.Now;
            Entries[Key(progress.MediaKey, progress.Season, progress.EpisodeIndex)] = progress;

            if (flush || DateTimeOffset.Now - _lastFlush > TimeSpan.FromSeconds(15))
            {
                Flush();
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(string mediaKey)
    {
        lock (_gate)
        {
            foreach (var key in Entries.Where(e => e.Value.MediaKey == mediaKey).Select(e => e.Key).ToArray())
            {
                Entries.Remove(key);
            }

            Flush();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_gate)
        {
            Entries.Clear();
            Flush();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Flush()
    {
        if (Entries.Count > MaxEntries)
        {
            foreach (var old in Entries.OrderBy(e => e.Value.UpdatedAt).Take(Entries.Count - MaxEntries).Select(e => e.Key).ToArray())
            {
                Entries.Remove(old);
            }
        }

        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Entries.Values.ToList(), ProgressJsonContext.Default.ListWatchProgress));
        File.Move(tmp, _path, overwrite: true);
        _lastFlush = DateTimeOffset.Now;
    }

    private Dictionary<string, WatchProgress> Entries => _entries ??= Load();

    private Dictionary<string, WatchProgress> Load()
    {
        try
        {
            var list = File.Exists(_path)
                ? JsonSerializer.Deserialize(File.ReadAllText(_path), ProgressJsonContext.Default.ListWatchProgress) ?? []
                : [];
            return list.ToDictionary(p => Key(p.MediaKey, p.Season, p.EpisodeIndex));
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Key(string mediaKey, int? season, int episodeIndex) => $"{mediaKey}|{season ?? 0}|{episodeIndex}";
}

[JsonSerializable(typeof(List<WatchProgress>))]
internal sealed partial class ProgressJsonContext : JsonSerializerContext;

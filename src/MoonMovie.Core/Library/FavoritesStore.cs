using System.Text.Json;
using System.Text.Json.Serialization;
using MoonMovie.Core.Configuration;
using MoonMovie.Core.Models;

namespace MoonMovie.Core.Library;

public sealed class FavoriteEntry
{
    public string MediaKey { get; set; } = "";

    public int TmdbId { get; set; }

    public MediaKind Kind { get; set; }

    public string Title { get; set; } = "";

    public string? PosterPath { get; set; }

    public DateTimeOffset AddedAt { get; set; }
}

/// <summary>Local favourites; small enough to keep in one JSON file for now.</summary>
public sealed class FavoritesStore
{
    private readonly string _path = Path.Combine(AppPaths.Data, "favorites.json");
    private readonly object _gate = new();
    private List<FavoriteEntry>? _entries;

    public event EventHandler? Changed;

    public bool IsFavorite(string mediaKey)
    {
        lock (_gate)
        {
            return Entries.Any(e => e.MediaKey == mediaKey);
        }
    }

    public IReadOnlyList<FavoriteEntry> All()
    {
        lock (_gate)
        {
            return Entries.OrderByDescending(e => e.AddedAt).ToArray();
        }
    }

    /// <returns>True when the item is now a favourite.</returns>
    public bool Toggle(MediaItem item)
    {
        bool added;
        lock (_gate)
        {
            var existing = Entries.FindIndex(e => e.MediaKey == item.MediaKey);
            if (existing >= 0)
            {
                Entries.RemoveAt(existing);
                added = false;
            }
            else
            {
                Entries.Add(new FavoriteEntry
                {
                    MediaKey = item.MediaKey,
                    TmdbId = item.TmdbId,
                    Kind = item.Kind,
                    Title = item.Title,
                    PosterPath = item.PosterPath,
                    AddedAt = DateTimeOffset.Now,
                });
                added = true;
            }

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Entries, FavoritesJsonContext.Default.ListFavoriteEntry));
            File.Move(tmp, _path, overwrite: true);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return added;
    }

    private List<FavoriteEntry> Entries => _entries ??= Load();

    private List<FavoriteEntry> Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize(File.ReadAllText(_path), FavoritesJsonContext.Default.ListFavoriteEntry) ?? []
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

[JsonSerializable(typeof(List<FavoriteEntry>))]
internal sealed partial class FavoritesJsonContext : JsonSerializerContext;

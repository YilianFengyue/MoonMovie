using System.Text.Json;
using System.Text.Json.Serialization;
using MoonMovie.Core.Configuration;

namespace MoonMovie.Core.Playback;

/// <summary>Per-title playback tweaks the user makes once and expects to stick (per series season).</summary>
public sealed class TitlePrefs
{
    public double SubtitleDelay { get; set; }

    public double AudioDelay { get; set; }

    /// <summary>Seconds into each episode where the opening ends (0: not set).</summary>
    public double IntroEnd { get; set; }

    /// <summary>Seconds before the end where the closing credits start (0: not set).</summary>
    public double OutroLength { get; set; }

    public bool SkipIntro { get; set; } = true;

    public bool SkipOutro { get; set; } = true;
}

public sealed class TitlePrefsStore
{
    private readonly string _path = Path.Combine(AppPaths.Data, "title-prefs.json");
    private readonly object _gate = new();
    private Dictionary<string, TitlePrefs>? _entries;

    public static string Key(string mediaKey, int? season) => $"{mediaKey}|{season ?? 0}";

    public TitlePrefs Get(string key)
    {
        lock (_gate)
        {
            return Entries.TryGetValue(key, out var p) ? p : new TitlePrefs();
        }
    }

    public void Update(string key, Action<TitlePrefs> change)
    {
        lock (_gate)
        {
            if (!Entries.TryGetValue(key, out var prefs)) Entries[key] = prefs = new TitlePrefs();
            change(prefs);
            try
            {
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Entries, TitlePrefsJsonContext.Default.DictionaryStringTitlePrefs));
                File.Move(tmp, _path, overwrite: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private Dictionary<string, TitlePrefs> Entries => _entries ??= Load();

    private Dictionary<string, TitlePrefs> Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize(File.ReadAllText(_path), TitlePrefsJsonContext.Default.DictionaryStringTitlePrefs) ?? []
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return [];
        }
    }
}

[JsonSerializable(typeof(Dictionary<string, TitlePrefs>))]
internal sealed partial class TitlePrefsJsonContext : JsonSerializerContext;

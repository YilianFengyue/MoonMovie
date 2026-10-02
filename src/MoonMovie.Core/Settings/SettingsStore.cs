using System.Text.Json;
using System.Text.Json.Serialization;
using MoonMovie.Core.Configuration;

namespace MoonMovie.Core.Settings;

public sealed class DanmakuSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>0.2 – 1.</summary>
    public double Opacity { get; set; } = 0.9;

    /// <summary>Text size multiplier, 0.6 – 1.6.</summary>
    public double FontScale { get; set; } = 1.0;

    /// <summary>Scroll speed multiplier, 0.5 – 2.</summary>
    public double Speed { get; set; } = 1.0;

    /// <summary>Share of the picture comments may cover, from the top: 0.25 – 1.</summary>
    public double Area { get; set; } = 0.5;

    public bool ShowTop { get; set; } = true;

    public bool ShowBottom { get; set; } = true;

    /// <summary>How many comments per second of video get through.</summary>
    public DanmakuDensity Density { get; set; } = DanmakuDensity.Smart;

    /// <summary>Drop comments that repeat the same text within a few seconds ("哈哈哈" floods).</summary>
    public bool MergeDuplicates { get; set; } = true;

    /// <summary>Space/comma/newline separated; "/regex/" entries are regular expressions.</summary>
    public string BlockWords { get; set; } = "";

    /// <summary>Overrides LOGVAR_BASE_URL from .env when set.</summary>
    public string? ServerUrl { get; set; }

    /// <summary>Overrides LOGVAR_TOKEN from .env when set.</summary>
    public string? Token { get; set; }
}

public enum DanmakuDensity
{
    /// <summary>Caps by display area and drops check-in / timestamp noise in dense moments.</summary>
    Smart,
    Low,
    Medium,
    High,
    All,
}

public sealed class PlaybackSettings
{
    /// <summary>Play the next episode when one ends (the up-next card can still cancel it).</summary>
    public bool AutoNext { get; set; } = true;
}

public enum PlayerEngineKind
{
    /// <summary>libmpv: every format, ASS subtitles, HDR, shaders.</summary>
    Mpv,

    /// <summary>Windows Media Foundation: fallback when libmpv is missing or misbehaves.</summary>
    System,
}

public enum QualityPreset
{
    /// <summary>Pick from the GPU (dedicated memory and vendor).</summary>
    Auto,
    Performance,
    Balanced,
    Quality,
}

public sealed class VideoSettings
{
    public PlayerEngineKind Engine { get; set; } = PlayerEngineKind.Mpv;

    public QualityPreset Quality { get; set; } = QualityPreset.Auto;

    /// <summary>Motion smoothing: resample to the display refresh rate (no 24p judder on 60/120/144 Hz).</summary>
    public bool Interpolation { get; set; }

    /// <summary>Pass HDR through when the display is in HDR mode; otherwise always tone-map to SDR.</summary>
    public bool HdrPassthrough { get; set; } = true;

    /// <summary>Null: Pictures\MoonMovie.</summary>
    public string? ScreenshotFolder { get; set; }
}

public sealed class SubtitleSettings
{
    /// <summary>Text size multiplier for non-ASS (and overridden) subtitles.</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>Vertical position, 0 (top) – 100 (bottom).</summary>
    public int Position { get; set; } = 100;

    /// <summary>Opaque box behind text instead of an outline.</summary>
    public bool Background { get; set; }

    /// <summary>Restyle ASS subtitles with the settings above instead of keeping the fansub styling.</summary>
    public bool OverrideAss { get; set; }

    /// <summary>Preferred subtitle languages, in order.</summary>
    public string Languages { get; set; } = "chi,zho,zh,chs,sc,zh-Hans,eng,en";
}

public sealed class AudioSettings
{
    /// <summary>Dynamic range compression: clear dialogue, tamer explosions.</summary>
    public bool NightMode { get; set; }

    /// <summary>Null: the system default device.</summary>
    public string? Device { get; set; }

    /// <summary>Send Dolby / DTS bitstreams untouched to a receiver (S/PDIF, HDMI).</summary>
    public bool Passthrough { get; set; }

    public string Languages { get; set; } = "chi,zho,zh,cmn,jpn,ja,eng,en";
}

public sealed class AppSettings
{
    public DanmakuSettings Danmaku { get; set; } = new();

    public PlaybackSettings Playback { get; set; } = new();

    public VideoSettings Video { get; set; } = new();

    public SubtitleSettings Subtitles { get; set; } = new();

    public AudioSettings Audio { get; set; } = new();
}

/// <summary>User preferences in data/settings.json; changes apply live through <see cref="Changed"/>.</summary>
public sealed class SettingsStore
{
    private readonly string _path = Path.Combine(AppPaths.Data, "settings.json");
    private readonly object _gate = new();

    public SettingsStore()
    {
        Current = Load();
    }

    public AppSettings Current { get; }

    public event EventHandler? Changed;

    /// <summary>Persists <see cref="Current"/> after the caller mutated it and notifies listeners.</summary>
    public void Save()
    {
        lock (_gate)
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, SettingsJsonContext.Default.AppSettings));
            File.Move(tmp, _path, overwrite: true);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private AppSettings Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize(File.ReadAllText(_path), SettingsJsonContext.Default.AppSettings) ?? new()
                : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

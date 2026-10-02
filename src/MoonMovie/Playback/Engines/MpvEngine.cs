using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using MoonMovie.Core.Settings;
using MoonMovie.Playback.Mpv;

namespace MoonMovie.Playback.Engines;

public sealed record MediaTrack(long Id, string Kind, string Label, bool Selected);

public sealed record Chapter(double Time, string Title);

/// <summary>libmpv engine: the default. Beyond <see cref="IPlaybackEngine"/> it exposes tracks, picture,
/// audio and subtitle controls, screenshots, chapters and decoder stats for the player's panels.</summary>
public sealed class MpvEngine : IPlaybackEngine
{
    private readonly MpvPlayer _player;
    private readonly MpvVideoView _view;
    private double _position;
    private double _duration;
    private bool _paused;
    private bool _pausedForCache;
    private bool _seeking;
    private bool _eof;
    private bool _loaded;
    private bool _restarted;

    public MpvEngine(DispatcherQueue ui, AppSettings settings, (int Width, int Height) initialPixels)
    {
        _view = new MpvVideoView { IsHitTestVisible = false };
        _player = new MpvPlayer(ui, initialPixels.Width, initialPixels.Height);
        _view.Attach(_player);
        Upscaler = new Upscaler(_player);

        foreach (var (name, format) in new[]
                 {
                     ("time-pos", MpvFormat.Double), ("duration", MpvFormat.Double), ("pause", MpvFormat.Flag),
                     ("paused-for-cache", MpvFormat.Flag), ("seeking", MpvFormat.Flag), ("eof-reached", MpvFormat.Flag),
                 })
        {
            _player.Observe(name, format);
        }

        _player.PropertyChanged += OnProperty;
        _player.FileLoaded += () =>
        {
            _loaded = true;
            Opened?.Invoke();
            StateChanged?.Invoke();
        };
        _player.PlaybackRestart += () =>
        {
            _restarted = true;
            StateChanged?.Invoke();
        };
        _player.EndFile += (reason, error) =>
        {
            if (reason == 4) Failed?.Invoke("mpv: " + LibMpvError(error));
        };
        _player.Log += line => Log?.Invoke(line);

        _player.SetProperty("volume-max", "150");
        ApplySettings(settings);
    }

    public string Name => "mpv";

    public Upscaler Upscaler { get; }

    public FrameworkElement View => _view;

    public event Action? Opened;

    public event Action? StateChanged;

    public event Action? Ended;

    public event Action<string>? Failed;

    public event Action? PositionChanged;

    public event Action<string>? Log;

    public EngineState State =>
        !_loaded || !_restarted ? EngineState.Opening
        : _eof ? EngineState.Ended
        : _pausedForCache || _seeking ? EngineState.Buffering
        : _paused ? EngineState.Paused
        : EngineState.Playing;

    public TimeSpan Position => TimeSpan.FromSeconds(_position);

    public TimeSpan Duration => TimeSpan.FromSeconds(_duration);

    public double BufferedAhead => _player.GetDouble("demuxer-cache-duration") ?? 0;

    public double Rate
    {
        get => _player.GetDouble("speed") ?? 1;
        set => _player.SetProperty("speed", Num(value));
    }

    public double Volume
    {
        get => (_player.GetDouble("volume") ?? 100) / 100;
        set => _player.SetProperty("volume", Num(Math.Clamp(value, 0, 1.5) * 100));
    }

    public bool Muted
    {
        get => _player.GetString("mute") == "yes";
        set => _player.SetProperty("mute", value ? "yes" : "no");
    }

    public Task OpenAsync(string url, TimeSpan start, bool isHls)
    {
        _loaded = _restarted = _eof = false;
        _position = start.TotalSeconds;
        _duration = 0;
        _player.SetPause(false);
        _player.Load(url, start.TotalSeconds);
        StateChanged?.Invoke();
        return Task.CompletedTask;
    }

    public void Play()
    {
        if (_eof) _player.Seek(0);
        _player.SetPause(false);
    }

    public void Pause() => _player.SetPause(true);

    public void Seek(TimeSpan position, bool exact = false)
    {
        _position = position.TotalSeconds;
        _player.Seek(position.TotalSeconds, exact);
    }

    /// <summary>The VIDEO_TS folder that "dvd://" reads from.</summary>
    public void SetDvdDevice(string path) => _player.SetProperty("dvd-device", path);

    /// <summary>Shown as the title in the system media flyout.</summary>
    public void SetMediaTitle(string title) => _player.SetProperty("force-media-title", title);

    private void OnProperty(string name, object? value)
    {
        switch (name)
        {
            case "time-pos":
                if (value is double t)
                {
                    _position = t;
                    PositionChanged?.Invoke();
                }

                return;
            case "duration":
                _duration = value is double d ? d : 0;
                return;
            case "pause":
                _paused = value is true;
                break;
            case "paused-for-cache":
                _pausedForCache = value is true;
                break;
            case "seeking":
                _seeking = value is true;
                break;
            case "eof-reached":
                var wasEof = _eof;
                _eof = value is true;
                if (_eof && !wasEof && _loaded) Ended?.Invoke();
                break;
        }

        StateChanged?.Invoke();
    }

    // ----- Settings ---------------------------------------------------------------------------------------

    public void ApplySettings(AppSettings settings)
    {
        ApplyQuality(settings.Video.Quality);
        SetInterpolation(settings.Video.Interpolation);
        _player.SetProperty("target-colorspace-hint", settings.Video.HdrPassthrough ? "yes" : "no");
        _player.SetProperty("hdr-compute-peak", "yes");
        _player.SetProperty("tone-mapping", "bt.2390");
        _player.SetProperty("screenshot-directory", settings.Video.ScreenshotFolder ?? DefaultScreenshotFolder);
        _player.SetProperty("screenshot-template", "%{media-title} %wH.%wM.%wS");
        _player.SetProperty("screenshot-format", "png");

        ApplySubtitleStyle(settings.Subtitles);
        _player.SetProperty("slang", settings.Subtitles.Languages);
        _player.SetProperty("sub-auto", "fuzzy");
        _player.SetProperty("alang", settings.Audio.Languages);
        SetNightMode(settings.Audio.NightMode);
        _player.SetProperty("audio-device", string.IsNullOrEmpty(settings.Audio.Device) ? "auto" : settings.Audio.Device);
        _player.SetProperty("audio-spdif", settings.Audio.Passthrough ? "ac3,eac3,dts,dts-hd,truehd" : "");
    }

    public static string DefaultScreenshotFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "MoonMovie");

    /// <summary>Scalers and debanding per preset (libplacebo / gpu-next).</summary>
    public void ApplyQuality(QualityPreset preset)
    {
        var effective = preset == QualityPreset.Auto ? GpuInfo.Recommended : preset;
        var (scale, cscale, dscale, deband) = effective switch
        {
            QualityPreset.Performance => ("bilinear", "bilinear", "bilinear", "no"),
            QualityPreset.Quality => ("ewa_lanczossharp", "ewa_lanczossharp", "mitchell", "yes"),
            _ => ("spline36", "spline36", "mitchell", "yes"),
        };
        _player.SetProperty("scale", scale);
        _player.SetProperty("cscale", cscale);
        _player.SetProperty("dscale", dscale);
        _player.SetProperty("deband", deband);
        EffectiveQuality = effective;
    }

    public QualityPreset EffectiveQuality { get; private set; }

    public void SetInterpolation(bool on)
    {
        _player.SetProperty("video-sync", on ? "display-resample" : "audio");
        _player.SetProperty("interpolation", on ? "yes" : "no");
        _player.SetProperty("tscale", "oversample");
    }

    // ----- Picture ----------------------------------------------------------------------------------------

    /// <summary>brightness / contrast / saturation / gamma, −100…100.</summary>
    public void SetAdjust(string property, int value) => _player.SetProperty(property, value.ToString(CultureInfo.InvariantCulture));

    /// <summary>"-1" original, "16:9", "4:3", "2.35:1"…</summary>
    public void SetAspect(string aspect) => _player.SetProperty("video-aspect-override", aspect);

    /// <summary>Crop to fill the screen (no black bars) or fit.</summary>
    public void SetFill(bool fill) => _player.SetProperty("panscan", fill ? "1.0" : "0.0");

    public void SetRotation(int degrees) => _player.SetProperty("video-rotate", degrees.ToString(CultureInfo.InvariantCulture));

    public void SetZoom(double log2Zoom) => _player.SetProperty("video-zoom", Num(log2Zoom));

    /// <summary>Saves a PNG (with or without subtitles) into the screenshot folder.</summary>
    public void Screenshot(bool withSubtitles) => _player.CommandAsync("screenshot", withSubtitles ? "subtitles" : "video");

    /// <summary>Saves the current frame (no subtitles) to a file; the extension picks the format.</summary>
    public void ScreenshotToFile(string path) => _player.CommandAsync("screenshot-to-file", path, "video");

    public void FrameStep(bool back) => _player.CommandAsync(back ? "frame-back-step" : "frame-step");

    /// <summary>Cycles A → B → off; returns the new state for the HUD.</summary>
    public string CycleAbLoop()
    {
        _player.Command("ab-loop");
        var a = _player.GetString("ab-loop-a");
        var b = _player.GetString("ab-loop-b");
        return a is null or "no" ? "A-B 循环已关闭" : b is null or "no" ? "已设 A 点" : "A-B 循环中";
    }

    // ----- Tracks -----------------------------------------------------------------------------------------

    /// <summary>"audio", "sub" or "video" tracks, labelled for people ("中文 · ASS · 内嵌").</summary>
    public IReadOnlyList<MediaTrack> Tracks(string kind)
    {
        var list = new List<MediaTrack>();
        var count = _player.GetInt64("track-list/count") ?? 0;
        for (var i = 0; i < count; i++)
        {
            if (_player.GetString($"track-list/{i}/type") != kind) continue;
            var id = _player.GetInt64($"track-list/{i}/id") ?? 0;
            var title = _player.GetString($"track-list/{i}/title");
            var lang = _player.GetString($"track-list/{i}/lang");
            var codec = _player.GetString($"track-list/{i}/codec");
            var external = _player.GetString($"track-list/{i}/external") == "yes";
            var selected = _player.GetString($"track-list/{i}/selected") == "yes";
            var channels = kind == "audio" ? _player.GetString($"track-list/{i}/demux-channels") : null;

            var parts = new List<string>();
            parts.Add(!string.IsNullOrWhiteSpace(title) ? title! : LanguageName(lang) ?? $"轨道 {id}");
            if (!string.IsNullOrWhiteSpace(title) && LanguageName(lang) is { } l) parts.Add(l);
            if (!string.IsNullOrEmpty(codec)) parts.Add(codec!.ToUpperInvariant());
            if (!string.IsNullOrEmpty(channels)) parts.Add(channels!);
            if (external) parts.Add("外挂");
            list.Add(new MediaTrack(id, kind, string.Join(" · ", parts), selected));
        }

        return list;
    }

    public void SelectTrack(string kind, long? id)
    {
        var property = kind switch { "audio" => "aid", "video" => "vid", _ => "sid" };
        _player.SetProperty(property, id is { } v ? v.ToString(CultureInfo.InvariantCulture) : "no");
    }

    public void SelectSecondarySubtitle(long? id) =>
        _player.SetProperty("secondary-sid", id is { } v ? v.ToString(CultureInfo.InvariantCulture) : "no");

    public long? SecondarySubtitle => long.TryParse(_player.GetString("secondary-sid"), out var v) ? v : null;

    public void AddSubtitle(string path) => _player.CommandAsync("sub-add", path, "select");

    public bool SubtitlesVisible
    {
        get => _player.GetString("sub-visibility") != "no";
        set => _player.SetProperty("sub-visibility", value ? "yes" : "no");
    }

    public double SubtitleDelay
    {
        get => _player.GetDouble("sub-delay") ?? 0;
        set => _player.SetProperty("sub-delay", Num(value));
    }

    public double AudioDelay
    {
        get => _player.GetDouble("audio-delay") ?? 0;
        set => _player.SetProperty("audio-delay", Num(value));
    }

    public void ApplySubtitleStyle(SubtitleSettings s)
    {
        _player.SetProperty("sub-font", "Microsoft YaHei UI");
        _player.SetProperty("sub-scale", Num(Math.Clamp(s.Scale, 0.5, 2.5)));
        _player.SetProperty("sub-pos", Math.Clamp(s.Position, 0, 100).ToString(CultureInfo.InvariantCulture));
        _player.SetProperty("sub-border-style", s.Background ? "opaque-box" : "outline-and-shadow");
        _player.SetProperty("sub-back-color", "#99000000");
        _player.SetProperty("sub-border-size", s.Background ? "4" : "2.5");
        _player.SetProperty("sub-ass-override", s.OverrideAss ? "force" : "scale");
    }

    // ----- Audio ------------------------------------------------------------------------------------------

    public void SetNightMode(bool on) =>
        _player.SetProperty("af", on ? "@night:lavfi=[dynaudnorm=f=250:g=31:p=0.6:m=8]" : "");

    /// <summary>(name, description) of the outputs mpv can use; "auto" is the system default.</summary>
    public IReadOnlyList<(string Name, string Description)> AudioDevices()
    {
        var list = new List<(string, string)>();
        var count = _player.GetInt64("audio-device-list/count") ?? 0;
        for (var i = 0; i < count; i++)
        {
            var name = _player.GetString($"audio-device-list/{i}/name");
            var description = _player.GetString($"audio-device-list/{i}/description");
            if (name is null || (!name.StartsWith("wasapi/", StringComparison.Ordinal) && name != "auto")) continue;
            list.Add((name, name == "auto" ? "系统默认" : description ?? name));
        }

        return list;
    }

    public string AudioDevice
    {
        get => _player.GetString("audio-device") ?? "auto";
        set => _player.SetProperty("audio-device", value);
    }

    // ----- Chapters & stats -------------------------------------------------------------------------------

    public IReadOnlyList<Chapter> Chapters()
    {
        var list = new List<Chapter>();
        var count = _player.GetInt64("chapter-list/count") ?? 0;
        for (var i = 0; i < count; i++)
        {
            var time = _player.GetDouble($"chapter-list/{i}/time") ?? 0;
            list.Add(new Chapter(time, _player.GetString($"chapter-list/{i}/title") ?? $"第 {i + 1} 章"));
        }

        return list;
    }

    /// <summary>Rows for the info panel (label, value).</summary>
    public IReadOnlyList<(string Label, string Value)> Stats()
    {
        var width = _player.GetInt64("width");
        var height = _player.GetInt64("height");
        var fps = _player.GetDouble("container-fps");
        var hwdec = _player.GetString("hwdec-current");
        var bitrate = _player.GetDouble("video-bitrate");
        var outW = _player.GetInt64("osd-width");
        var outH = _player.GetInt64("osd-height");
        var primaries = _player.GetString("video-params/primaries");
        var gamma = _player.GetString("video-params/gamma");
        return
        [
            ("内核", $"mpv {(_player.GetString("mpv-version") ?? "").Replace("mpv ", "")}"),
            ("视频", $"{_player.GetString("video-codec") ?? "-"}"),
            ("分辨率", width is null ? "-" : $"{width}×{height}  {fps:0.###} fps"),
            ("输出", outW is null ? "-" : $"{outW}×{outH}"),
            ("解码", string.IsNullOrEmpty(hwdec) || hwdec == "no" ? "软件解码" : $"硬件解码（{hwdec}）"),
            ("色彩", primaries is null ? "-" : $"{primaries} · {gamma}"),
            ("码率", bitrate is > 0 ? $"{bitrate / 1_000_000:0.0} Mbps" : "-"),
            ("音频", $"{_player.GetString("audio-codec-name") ?? "-"} · {_player.GetString("audio-params/channel-count") ?? "-"} 声道"),
            ("缓冲", $"{BufferedAhead:0} 秒"),
            ("丢帧", $"渲染 {_player.GetInt64("frame-drop-count") ?? 0} · 解码 {_player.GetInt64("decoder-frame-drop-count") ?? 0}"),
            ("超分", Upscaler.Status(hwdec is not (null or "" or "no"))),
            ("画质档位", EffectiveQuality switch
            {
                QualityPreset.Performance => "性能",
                QualityPreset.Quality => "画质",
                _ => "均衡",
            } + (GpuInfo.Name.Length > 0 ? $"（{GpuInfo.Name}）" : "")),
        ];
    }

    // ----- Helpers ----------------------------------------------------------------------------------------

    private static string Num(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    private static string LibMpvError(int error) => error switch
    {
        -13 => "无法打开（网络或片源失效）",
        -14 => "没有可播放的音视频流",
        -17 => "格式不支持",
        _ => $"错误 {error}",
    };

    private static string? LanguageName(string? code) => code?.ToLowerInvariant() switch
    {
        null or "" => null,
        "chi" or "zho" or "zh" or "chs" or "sc" or "zh-hans" or "cmn" => "中文",
        "cht" or "tc" or "zh-hant" => "繁体中文",
        "eng" or "en" => "英语",
        "jpn" or "ja" => "日语",
        "kor" or "ko" => "韩语",
        "yue" => "粤语",
        "fre" or "fra" or "fr" => "法语",
        "ger" or "deu" or "de" => "德语",
        "spa" or "es" => "西班牙语",
        "rus" or "ru" => "俄语",
        _ => code,
    };

    public void Dispose()
    {
        _view.Detach();
        _player.Dispose();
    }
}

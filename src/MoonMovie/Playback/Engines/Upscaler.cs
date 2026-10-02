using System.Globalization;
using MoonMovie.Core.Settings;
using MoonMovie.Playback.Mpv;

namespace MoonMovie.Playback.Engines;

/// <summary>
/// Super-resolution on an mpv instance: Anime4K shader chains or RTX Video Super Resolution. Only engaged while
/// the picture is actually enlarged (source smaller than the output), re-checked as the window changes size, and
/// stepped down a level when the GPU starts dropping frames.
/// </summary>
public sealed class Upscaler(MpvPlayer player)
{
    private const double EngageRatio = 1.2;   // same threshold the Anime4K upscale passes use internally
    private const int DropLimit = 12;          // dropped frames within the window below → step down
    private static readonly TimeSpan DropWindow = TimeSpan.FromSeconds(6);

    // Engaging, resizing (fullscreen) and the first shader compile all stall a few frames: not a GPU limit.
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(8);

    private static readonly string ShaderFolder = Path.Combine(AppContext.BaseDirectory, "Assets", "Shaders");

    private static readonly Dictionary<UpscaleMode, string[]> Chains = new()
    {
        [UpscaleMode.Anime4KFast] =
        [
            "Anime4K_Clamp_Highlights", "Anime4K_Restore_CNN_M", "Anime4K_Upscale_CNN_x2_M",
            "Anime4K_AutoDownscalePre_x2", "Anime4K_AutoDownscalePre_x4", "Anime4K_Upscale_CNN_x2_S",
        ],
        [UpscaleMode.Anime4KQuality] =
        [
            "Anime4K_Clamp_Highlights", "Anime4K_Restore_CNN_VL", "Anime4K_Upscale_CNN_x2_VL",
            "Anime4K_AutoDownscalePre_x2", "Anime4K_AutoDownscalePre_x4", "Anime4K_Upscale_CNN_x2_M",
        ],
        [UpscaleMode.Anime4KRestore] =
        [
            "Anime4K_Clamp_Highlights", "Anime4K_Restore_CNN_M", "Anime4K_Upscale_CNN_x2_M", "Anime4K_Restore_CNN_S",
            "Anime4K_AutoDownscalePre_x2", "Anime4K_AutoDownscalePre_x4", "Anime4K_Upscale_CNN_x2_S",
        ],
    };

    private string _appliedShaders = "";
    private double _appliedVsrScale;
    private long _dropBaseline = -1;
    private DateTimeOffset _settledAt;
    private (long, long, long, long) _lastSizes;
    private DateTimeOffset _dropWindowStart;

    /// <summary>What the user (or the title's defaults) asked for.</summary>
    public UpscaleMode Mode { get; private set; }

    /// <summary>Whether the chosen mode is running right now (false while the source already fills the output).</summary>
    public bool Engaged { get; private set; }

    /// <summary>"1080p → 2160p" while engaged.</summary>
    public string? Scaling { get; private set; }

    /// <summary>Raised after an automatic step down, with the mode now in use.</summary>
    public event Action<UpscaleMode>? SteppedDown;

    public static bool ShadersAvailable => File.Exists(Path.Combine(ShaderFolder, "Anime4K_Upscale_CNN_x2_M.glsl"));

    /// <summary>RTX VSR needs an NVIDIA RTX card; whether it engages also depends on zero-copy hardware decoding.</summary>
    public static bool RtxAvailable => GpuInfo.Name.Contains("RTX", StringComparison.OrdinalIgnoreCase)
                                       && GpuInfo.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);

    public static string Label(UpscaleMode mode) => mode switch
    {
        UpscaleMode.Anime4KFast => "Anime4K 流畅",
        UpscaleMode.Anime4KQuality => "Anime4K 高质量",
        UpscaleMode.Anime4KRestore => "Anime4K 低清增强",
        UpscaleMode.RtxVsr => "RTX 超分",
        _ => "关",
    };

    public void SetMode(UpscaleMode mode)
    {
        Mode = mode;
        _dropBaseline = -1;
        Evaluate(playing: false);
    }

    /// <summary>Called about once a second: engages, retunes or releases the upscaler for the current sizes.</summary>
    public void Evaluate(bool playing)
    {
        var (videoW, videoH) = (player.GetInt64("width") ?? 0, player.GetInt64("height") ?? 0);
        var (outW, outH) = (player.GetInt64("osd-width") ?? 0, player.GetInt64("osd-height") ?? 0);

        // Fit the video into the output to find the real enlargement.
        var factor = videoW > 0 && videoH > 0 && outW > 0 && outH > 0
            ? Math.Min((double)outW / videoW, (double)outH / videoH)
            : 0;
        var engage = Mode != UpscaleMode.Off && factor >= EngageRatio;

        var sizes = (videoW, videoH, outW, outH);
        if (sizes != _lastSizes)
        {
            _lastSizes = sizes;
            Unsettle();
        }

        if (engage && Mode == UpscaleMode.RtxVsr && player.GetString("hwdec-current") != "d3d11va")
        {
            engage = false; // VSR runs on the decoder's D3D11 surfaces; copy-back or software decoding cannot use it
        }

        ApplyShaders(engage && Chains.TryGetValue(Mode, out var chain) ? chain : null);
        ApplyVsr(engage && Mode == UpscaleMode.RtxVsr ? Math.Clamp(Math.Round(factor, 1), 1, 4) : 0);

        Engaged = engage;
        Scaling = engage ? $"{videoH}p → {(long)Math.Round(videoH * factor)}p" : null;
        if (engage && playing) WatchDrops();
        else _dropBaseline = -1;
    }

    /// <summary>One line for the info panel.</summary>
    public string Status(bool hardwareDecoding) => Mode switch
    {
        UpscaleMode.Off => "关",
        _ when Engaged => $"{Label(Mode)} · {Scaling}",
        UpscaleMode.RtxVsr when !hardwareDecoding => $"{Label(Mode)} · 待命（需要硬件解码）",
        _ => $"{Label(Mode)} · 待命（片源已达窗口分辨率）",
    };

    private void ApplyShaders(string[]? chain)
    {
        var value = chain is null
            ? ""
            : string.Join(";", chain.Select(name => Path.Combine(ShaderFolder, name + ".glsl")));
        if (value == _appliedShaders) return;
        _appliedShaders = value;
        Unsettle();
        player.SetProperty("glsl-shaders", value);
    }

    private void ApplyVsr(double scale)
    {
        if (Math.Abs(scale - _appliedVsrScale) < 0.05) return;
        _appliedVsrScale = scale;
        Unsettle();
        player.SetProperty("vf", scale > 0
            ? $"@vsr:d3d11vpp=scaling-mode=nvidia:scale={scale.ToString("0.#", CultureInfo.InvariantCulture)}"
            : "");
    }

    private void Unsettle()
    {
        _settledAt = DateTimeOffset.Now + SettleTime;
        _dropBaseline = -1;
    }

    /// <summary>Too many dropped frames in a short window means the GPU cannot keep up: one level down.</summary>
    private void WatchDrops()
    {
        var drops = player.GetInt64("frame-drop-count") ?? 0;
        var now = DateTimeOffset.Now;
        if (now < _settledAt)
        {
            _dropBaseline = -1;
            return;
        }

        if (_dropBaseline < 0 || now - _dropWindowStart > DropWindow)
        {
            _dropBaseline = drops;
            _dropWindowStart = now;
            return;
        }

        if (drops - _dropBaseline < DropLimit) return;

        var lower = Mode switch
        {
            UpscaleMode.Anime4KQuality or UpscaleMode.Anime4KRestore => UpscaleMode.Anime4KFast,
            _ => UpscaleMode.Off,
        };
        SetMode(lower);
        SteppedDown?.Invoke(lower);
    }
}

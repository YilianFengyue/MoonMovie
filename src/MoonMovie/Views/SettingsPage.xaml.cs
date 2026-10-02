using MoonMovie.Core.Caching;
using MoonMovie.Core.Downloads;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using MoonMovie.Controls;
using MoonMovie.Core.Configuration;
using MoonMovie.Core.Danmaku;
using MoonMovie.Core.Settings;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace MoonMovie.Views;

/// <summary>Playback and 弹幕 preferences, the comment server, caches. Everything saves as it changes.</summary>
public sealed partial class SettingsPage : Page
{
    private static readonly (string Label, double Value)[] Areas = [("1/4", 0.25), ("半屏", 0.5), ("3/4", 0.75), ("满屏", 1.0)];

    private readonly SettingsStore _settings = App.Services.GetRequiredService<SettingsStore>();
    private readonly DanmakuClient _danmaku = App.Services.GetRequiredService<DanmakuClient>();
    private readonly List<(Button Chip, double Value)> _areaChips = [];
    private readonly List<(Button Chip, DanmakuDensity Value)> _densityChips = [];
    private readonly Button _autoNextChip;
    private readonly Button _danmakuOnChip;
    private readonly Button _topChip;
    private readonly Button _bottomChip;
    private readonly Button _mergeChip;
    private readonly DispatcherQueueTimer _saveTimer;
    private bool _syncing = true;

    public SettingsPage()
    {
        InitializeComponent();

        _saveTimer = DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(400);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => _settings.Save();

        _autoNextChip = Chips.Create("已开启", () => { Playback.AutoNext = !Playback.AutoNext; Changed(); });
        _danmakuOnChip = Chips.Create("已开启", () => { Danmaku.Enabled = !Danmaku.Enabled; Changed(); });
        AutoNextChips.Children.Add(_autoNextChip);
        BuildPlayerRows();
        DanmakuOnHost.Child = _danmakuOnChip;

        foreach (var (label, value) in Areas)
        {
            var chip = Chips.Create(label, () => { Danmaku.Area = value; Changed(); });
            _areaChips.Add((chip, value));
            AreaChips.Children.Add(chip);
        }

        foreach (var (label, value) in PlayerPage.DensityLevels)
        {
            var chip = Chips.Create(label, () => { Danmaku.Density = value; Changed(); });
            _densityChips.Add((chip, value));
            DensityChips.Children.Add(chip);
        }

        _topChip = Chips.Create("顶部", () => { Danmaku.ShowTop = !Danmaku.ShowTop; Changed(); });
        _bottomChip = Chips.Create("底部", () => { Danmaku.ShowBottom = !Danmaku.ShowBottom; Changed(); });
        _mergeChip = Chips.Create("合并重复", () => { Danmaku.MergeDuplicates = !Danmaku.MergeDuplicates; Changed(); });
        KindChips.Children.Add(_topChip);
        KindChips.Children.Add(_bottomChip);
        KindChips.Children.Add(_mergeChip);

        BlockWordsBox.Text = Danmaku.BlockWords;
        ServerBox.Text = Danmaku.ServerUrl ?? string.Empty;
        if (App.Services.GetRequiredService<EnvFile>().Get("LOGVAR_BASE_URL") is { Length: > 0 } envUrl)
        {
            ServerBox.PlaceholderText = $"{envUrl}（来自 .env）";
        }
        TokenBox.Text = Danmaku.Token ?? string.Empty;
        TmdbKeyBox.Password = _settings.Current.Services.TmdbApiKey ?? string.Empty;
        if (!App.Services.GetRequiredService<Core.Tmdb.TmdbOptions>().IsConfigured)
        {
            TmdbKeyCaption.Text = "还没有 TMDB 密钥：MoonMovie 需要它来获取海报、简介和剧集。在 themoviedb.org 免费申请后填在右边，然后重启 MoonMovie";
            TmdbKeyCaption.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xF2, 0xC9, 0x4C));
        }

        DataFolderText.Text = AppPaths.Root;
        VersionText.Text = $"MoonMovie {typeof(App).Assembly.GetName().Version?.ToString(3)}";

        Sync();
        Loaded += async (_, _) => await UpdateCacheSizesAsync();
    }

    private DanmakuSettings Danmaku => _settings.Current.Danmaku;

    private PlaybackSettings Playback => _settings.Current.Playback;

    private readonly List<(Button Chip, Func<bool> IsOn)> _playerChips = [];

    /// <summary>Engine, quality, smoothing, subtitle and audio defaults (the player panels change the same settings).</summary>
    private void BuildPlayerRows()
    {
        var video = _settings.Current.Video;
        var subs = _settings.Current.Subtitles;
        var audio = _settings.Current.Audio;

        void Add(Panel host, string label, Func<bool> isOn, Action toggle)
        {
            var chip = Chips.Create(label, () => { toggle(); Changed(); });
            _playerChips.Add((chip, isOn));
            host.Children.Add(chip);
        }

        Add(EngineChips, "mpv", () => video.Engine == PlayerEngineKind.Mpv, () => video.Engine = PlayerEngineKind.Mpv);
        Add(EngineChips, "系统", () => video.Engine == PlayerEngineKind.System, () => video.Engine = PlayerEngineKind.System);

        foreach (var (label, value) in new[] { ("自动", QualityPreset.Auto), ("性能", QualityPreset.Performance), ("均衡", QualityPreset.Balanced), ("画质", QualityPreset.Quality) })
        {
            Add(QualityChipsHost, label, () => video.Quality == value, () => video.Quality = value);
        }

        var gpu = MoonMovie.Playback.Engines.GpuInfo.Name;
        var recommended = MoonMovie.Playback.Engines.GpuInfo.Recommended switch
        {
            QualityPreset.Performance => "性能",
            QualityPreset.Quality => "画质",
            _ => "均衡",
        };
        QualityChipsHostCaption.Text = gpu.Length > 0
            ? $"自动：按显卡选择 · {gpu} → {recommended}。性能档省电，画质档用更好的缩放与去色带"
            : "自动：按显卡选择。性能档省电，画质档用更好的缩放与去色带";

        foreach (var (label, value) in new[]
                 {
                     ("关", UpscaleMode.Off), ("流畅", UpscaleMode.Anime4KFast), ("高质量", UpscaleMode.Anime4KQuality),
                     ("低清增强", UpscaleMode.Anime4KRestore),
                 })
        {
            Add(AnimeUpscaleChips, label, () => video.AnimeUpscale == value, () => video.AnimeUpscale = value);
        }

        Add(UpscaleChips, "关", () => video.Upscale == UpscaleMode.Off, () => video.Upscale = UpscaleMode.Off);
        if (MoonMovie.Playback.Engines.Upscaler.RtxAvailable)
        {
            Add(UpscaleChips, "RTX 超分", () => video.Upscale == UpscaleMode.RtxVsr, () => video.Upscale = UpscaleMode.RtxVsr);
        }

        foreach (var gb in new[] { 2, 5, 10, 20 })
        {
            Add(SegmentCacheChips, $"{gb} GB", () => _settings.Current.Cache.SegmentCacheGb == gb, () =>
            {
                _settings.Current.Cache.SegmentCacheGb = gb;
                App.Services.GetRequiredService<SegmentCache>().Trim();
            });
        }

        foreach (var n in new[] { 1, 2, 3 })
        {
            Add(DownloadConcurrencyChips, n.ToString(), () => _settings.Current.Downloads.Concurrent == n,
                () => _settings.Current.Downloads.Concurrent = n);
        }

        Add(InterpolationChips, "运动平滑", () => video.Interpolation, () => video.Interpolation = !video.Interpolation);
        Add(SubtitleStyleChips, "背景板", () => subs.Background, () => subs.Background = !subs.Background);
        Add(SubtitleStyleChips, "统一样式", () => subs.OverrideAss, () => subs.OverrideAss = !subs.OverrideAss);
        Add(AudioChipsHost, "夜间模式", () => audio.NightMode, () => audio.NightMode = !audio.NightMode);
        Add(AudioChipsHost, "源码输出", () => audio.Passthrough, () => audio.Passthrough = !audio.Passthrough);
    }

    private void Changed()
    {
        Sync();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Sync()
    {
        _syncing = true;
        Chips.Set(_autoNextChip, Playback.AutoNext);
        foreach (var (chip, isOn) in _playerChips) Chips.Set(chip, isOn());
        Chips.SetLabel(_autoNextChip, Playback.AutoNext ? "已开启" : "已关闭");
        Chips.Set(_danmakuOnChip, Danmaku.Enabled);
        Chips.SetLabel(_danmakuOnChip, Danmaku.Enabled ? "已开启" : "已关闭");
        foreach (var (chip, value) in _areaChips) Chips.Set(chip, Math.Abs(Danmaku.Area - value) < 0.01);
        foreach (var (chip, value) in _densityChips) Chips.Set(chip, Danmaku.Density == value);
        Chips.Set(_topChip, Danmaku.ShowTop);
        Chips.Set(_bottomChip, Danmaku.ShowBottom);
        Chips.Set(_mergeChip, Danmaku.MergeDuplicates);

        OpacitySlider.Value = Math.Round(Danmaku.Opacity * 100);
        FontSlider.Value = Math.Round(Danmaku.FontScale * 100);
        SpeedSlider.Value = Math.Round(Danmaku.Speed * 100);
        OpacityValue.Text = $"{Math.Round(Danmaku.Opacity * 100)}%";
        FontValue.Text = $"{Math.Round(Danmaku.FontScale * 100)}%";
        SpeedValue.Text = $"{Danmaku.Speed:0.0#}x";
        _syncing = false;
    }

    private void OnSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncing) return;
        Danmaku.Opacity = OpacitySlider.Value / 100;
        Danmaku.FontScale = FontSlider.Value / 100;
        Danmaku.Speed = SpeedSlider.Value / 100;
        Changed();
    }

    private void OnBlockWordsLostFocus(object sender, RoutedEventArgs e)
    {
        if (BlockWordsBox.Text == Danmaku.BlockWords) return;
        Danmaku.BlockWords = BlockWordsBox.Text;
        Changed();
    }

    private void OnServerLostFocus(object sender, RoutedEventArgs e)
    {
        var url = string.IsNullOrWhiteSpace(ServerBox.Text) ? null : ServerBox.Text.Trim();
        var token = string.IsNullOrWhiteSpace(TokenBox.Text) ? null : TokenBox.Text.Trim();
        if (url == Danmaku.ServerUrl && token == Danmaku.Token) return;
        Danmaku.ServerUrl = url;
        Danmaku.Token = token;
        Changed();
        SetServerStatus(null, "未检测");
    }

    /// <summary>Opened because the TMDB key is missing: go straight to that field.</summary>
    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not "tmdb") return;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            TmdbKeyBox.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.3, AnimationDesired = false });
            TmdbKeyBox.Focus(FocusState.Programmatic);
        });
    }

    private void OnTmdbKeyLostFocus(object sender, RoutedEventArgs e)
    {
        var key = string.IsNullOrWhiteSpace(TmdbKeyBox.Password) ? null : TmdbKeyBox.Password.Trim();
        if (key == _settings.Current.Services.TmdbApiKey) return;
        _settings.Current.Services.TmdbApiKey = key;
        _settings.Save();
        TmdbKeyCaption.Text = "已保存，重启 MoonMovie 后生效";
    }

    private async void OnTestServer(object sender, RoutedEventArgs e)
    {
        OnServerLostFocus(sender, e);
        TestButton.IsEnabled = false;
        SetServerStatus(null, "正在连接…");
        var result = await _danmaku.PingAsync();
        TestButton.IsEnabled = true;
        SetServerStatus(result.Ok, result.Message);
    }

    private void SetServerStatus(bool? ok, string text)
    {
        ServerStatus.Text = text;
        ServerDot.Fill = new SolidColorBrush(ok switch
        {
            true => Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xC2, 0x7A),
            false => Windows.UI.Color.FromArgb(0xFF, 0xE5, 0x5B, 0x4F),
            null => Windows.UI.Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF),
        });
    }

    // ----- Storage ------------------------------------------------------------------------------------

    private static string DanmakuCache => Path.Combine(AppPaths.Root, "cache", "danmaku");

    private async Task UpdateCacheSizesAsync()
    {
        var (images, data) = await Task.Run(() => (
            Measure(AppPaths.ImageCache),
            Measure(AppPaths.ApiCache) + Measure(DanmakuCache)));
        ImageCacheText.Text = $"海报与剧照 · {Format(images)}";
        var segments = await Task.Run(() => App.Services.GetRequiredService<SegmentCache>().SizeBytes);
        SegmentCacheText.Text = $"看过的片段存在本地，回看和往回拖不用重新下载 · 已用 {Format(segments)}";
        DownloadFolderText.Text = App.Services.GetRequiredService<DownloadManager>().Folder;
        DataCacheText.Text = $"影视资料与弹幕 · {Format(data)} · 清理后会重新从网络获取";
    }

    private async void OnClearImages(object sender, RoutedEventArgs e)
    {
        await Task.Run(() => Clear(AppPaths.ImageCache));
        await UpdateCacheSizesAsync();
    }

    private async void OnClearData(object sender, RoutedEventArgs e)
    {
        await Task.Run(() =>
        {
            Clear(AppPaths.ApiCache);
            Clear(DanmakuCache);
        });
        await UpdateCacheSizesAsync();
    }

    private async void OnClearSegments(object sender, RoutedEventArgs e)
    {
        await Task.Run(() => App.Services.GetRequiredService<SegmentCache>().Clear());
        await UpdateCacheSizesAsync();
    }

    private async void OnChangeDownloadFolder(object sender, RoutedEventArgs e)
    {
        if (await Services.LocalPlayback.PickFolderAsync() is not { } folder) return;
        _settings.Current.Downloads.Folder = folder;
        _settings.Save();
        DownloadFolderText.Text = folder;
    }

    private void OnOpenDataFolder(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.Root}\"") { UseShellExecute = true });

    private static long Measure(string directory)
    {
        if (!Directory.Exists(directory)) return 0;
        long total = 0;
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            try { total += file.Length; }
            catch (IOException) { }
        }

        return total;
    }

    /// <summary>Best effort: files in use are skipped and simply survive until next time.</summary>
    private static void Clear(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string Format(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };
}

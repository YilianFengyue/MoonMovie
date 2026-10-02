using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using MoonMovie.Core.Configuration;
using MoonMovie.Core.Danmaku;
using MoonMovie.Playback;
using MoonMovie.Playback.Mpv;
using MoonMovie.Services;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace MoonMovie.Views;

/// <summary>
/// M0 engine lab (debug builds): plays a <see cref="PlaybackRequest"/> or a raw URL through libmpv with the
/// danmaku layer and XAML chrome on top, and shows decoder / timing stats. Not part of the product UI.
/// </summary>
public sealed partial class MpvLabPage : Page
{
    private readonly Stopwatch _sinceLoad = new();
    private readonly DispatcherQueueTimer _statsTimer;
    private MpvPlayer? _player;
    private double _position;
    private double _duration;
    private bool _paused;
    private long? _firstFrameMs;
    private string _source = "";
    private bool _dragging;

    public MpvLabPage()
    {
        InitializeComponent();
        _statsTimer = DispatcherQueue.CreateTimer();
        _statsTimer.Interval = TimeSpan.FromMilliseconds(500);
        _statsTimer.Tick += (_, _) => SafeDispatch.Run(UpdateStats);

        // The slider handles pointer input itself; listen to handled events too.
        Seek.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => _dragging = true), true);
        Seek.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) =>
        {
            _dragging = false;
            _player?.Seek(Seek.Value);
        }), true);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        App.MainWindow.SetImmersive(true);
        VideoView.UpdateLayout();

        try
        {
            var (w, h) = VideoView.PixelSize;
            _player = new MpvPlayer(DispatcherQueue, w, h);
        }
        catch (Exception ex) when (ex is DllNotFoundException or InvalidOperationException or EntryPointNotFoundException)
        {
            Stats.Text = "mpv 初始化失败：" + ex.Message;
            Lab("init failed: " + ex);
            return;
        }

        VideoView.Attach(_player);
        _player.Observe("time-pos", MpvFormat.Double);
        _player.Observe("duration", MpvFormat.Double);
        _player.Observe("pause", MpvFormat.Flag);
        _player.PropertyChanged += OnProperty;
        _player.PlaybackRestart += () =>
        {
            _firstFrameMs ??= _sinceLoad.ElapsedMilliseconds;
            Lab($"playback-restart at {_sinceLoad.ElapsedMilliseconds} ms");
        };
        _player.FileLoaded += () => Lab($"file-loaded at {_sinceLoad.ElapsedMilliseconds} ms");
        _player.EndFile += (reason, error) => Lab($"end-file reason={reason} error={error}");
        _player.Log += Lab;

        Danmaku.ApplySettings(App.Services.GetRequiredService<Core.Settings.SettingsStore>().Current.Danmaku);
        Danmaku.SetComments(SyntheticComments());

        var url = await ResolveUrlAsync(e.Parameter);
        if (url is null)
        {
            Stats.Text = "没有可播放的地址";
            return;
        }

        _source = url;
        Lab($"mpv api {MpvPlayer.Version} · load {url}");
        _sinceLoad.Restart();
        _player.Load(url);
        _statsTimer.Start();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _statsTimer.Stop();
        VideoView.Detach();
        _player?.Dispose();
        _player = null;
        App.MainWindow.SetCompactOverlay(false);
        App.MainWindow.SetFullScreen(false);
        App.MainWindow.SetImmersive(false);
    }

    private static async Task<string?> ResolveUrlAsync(object? parameter)
    {
        switch (parameter)
        {
            case string raw:
                return raw;
            case PlaybackRequest request:
                var episode = request.Source.Candidate.PrimaryLine.Episodes[request.EpisodeIndex];
                var proxy = App.Services.GetRequiredService<MediaProxy>();
                var uri = episode.Url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
                    ? await proxy.PlaylistUriAsync(episode.Url)
                    : await proxy.FileUriAsync(episode.Url);
                return uri.AbsoluteUri;
            default:
                return null;
        }
    }

    private void OnProperty(string name, object? value)
    {
        switch (name)
        {
            case "time-pos" when value is double t:
                _position = t;
                Danmaku.Sync(TimeSpan.FromSeconds(t), !_paused, 1);
                if (!_dragging) Seek.Value = t;
                break;
            case "duration" when value is double d:
                _duration = d;
                Seek.Maximum = d;
                break;
            case "pause" when value is bool p:
                _paused = p;
                PlayGlyph.Glyph = p ? "" : "";
                Danmaku.Sync(TimeSpan.FromSeconds(_position), !p, 1);
                break;
        }

        TimeLabel.Text = $"{TimeSpan.FromSeconds(_position):h\\:mm\\:ss} / {TimeSpan.FromSeconds(_duration):h\\:mm\\:ss}";
    }

    private void UpdateStats()
    {
        if (_player is null) return;
        var lines = new[]
        {
            $"mpv        {_player.GetString("mpv-version")}",
            $"hwdec      {_player.GetString("hwdec-current") ?? "-"}",
            $"video      {_player.GetString("video-codec") ?? "-"}",
            $"size       {_player.GetInt64("width")}x{_player.GetInt64("height")} @ {_player.GetDouble("container-fps"):0.##} fps",
            $"output     {VideoView.PixelSize.Width}x{VideoView.PixelSize.Height} px",
            $"dropped    vo={_player.GetInt64("frame-drop-count")} decoder={_player.GetInt64("decoder-frame-drop-count")}",
            $"cache      {_player.GetDouble("demuxer-cache-duration"):0.0} s",
            $"first      {(_firstFrameMs is { } f ? f + " ms" : "…")}",
            $"source     {(_source.Length > 60 ? _source[..60] + "…" : _source)}",
        };
        Stats.Text = string.Join("\n", lines);
    }

    private void OnPlayPause(object sender, RoutedEventArgs e) => _player?.SetPause(!_paused);

    private void OnSeekReleased(object sender, PointerRoutedEventArgs e) => _dragging = false;

    private void OnFullScreen(object sender, RoutedEventArgs e) => App.MainWindow.SetFullScreen(!App.MainWindow.IsFullScreen);

    private void OnPip(object sender, RoutedEventArgs e) => App.MainWindow.SetCompactOverlay(!App.MainWindow.IsCompactOverlay);

    private static DanmakuComment[] SyntheticComments()
    {
        var rng = new Random(3);
        string[] samples = ["mpv 内核测试", "画面在弹幕下面吗", "4K 硬解", "前方高能", "这画质可以", "哈哈哈哈", "弹幕同步正常"];
        return Enumerable.Range(0, 4000)
            .Select(_ => new DanmakuComment(rng.NextDouble() * 3600, DanmakuMode.Scroll, 0xFFFFFF, samples[rng.Next(samples.Length)]))
            .OrderBy(c => c.Time)
            .ToArray();
    }

    private static void Lab(string line)
    {
        try
        {
            File.AppendAllText(Path.Combine(AppPaths.Root, "mpv-lab.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }
}

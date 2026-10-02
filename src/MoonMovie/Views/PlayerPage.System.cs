using System.Runtime.InteropServices;
using MoonMovie.Core.Settings;
using MoonMovie.Playback;
using MoonMovie.Playback.Engines;

namespace MoonMovie.Views;

/// <summary>
/// Player ↔ engine and Windows: picks the playback engine (mpv, falling back to Media Foundation), drives the
/// system media flyout and media keys, and keeps the display awake while something is playing.
/// </summary>
public sealed partial class PlayerPage
{
    private SystemMediaControls? _media;
    private bool _keepingAwake;

    /// <summary>mpv can boost to 150 %; the system engine stops at 100 %.</summary>
    private double MaxVolume => _engine is MpvEngine ? 1.5 : 1.0;

    private void CreateEngine()
    {
        var settings = _settings.Current;
        IPlaybackEngine? engine = null;
        string? fallbackReason = null;

        if (settings.Video.Engine == PlayerEngineKind.Mpv)
        {
            try
            {
                engine = new MpvEngine(DispatcherQueue, settings, InitialPixels());
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
                                           or InvalidOperationException)
            {
                fallbackReason = ex.Message;
                PlayerLog("mpv unavailable, using system engine: " + ex);
            }
        }

        engine ??= new SystemEngine(DispatcherQueue);
        _engine = engine;
        VideoHost.Children.Add(engine.View);

        engine.Opened += OnMediaOpened;
        engine.Failed += OnMediaFailed;
        engine.Ended += OnMediaEnded;
        engine.StateChanged += UpdatePlaybackState;
        engine.PositionChanged += () => SyncDanmaku();
        if (engine is MpvEngine mpv)
        {
            mpv.Log += PlayerLog;
            mpv.Opened += OnTracksMaybeChanged;
        }

        _volume = Math.Min(_volume, MaxVolume);
        VolumeBar.Maximum = MaxVolume * 100;
        engine.Volume = _volume;
        ConfigureEnginePanels();

        if (fallbackReason is not null)
        {
            ShowToast("mpv 内核不可用，已改用系统内核", duration: TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>The window's client size in physical pixels: mpv's first output size before layout settles.</summary>
    private static (int Width, int Height) InitialPixels()
    {
        var root = App.MainWindow.Content as Microsoft.UI.Xaml.FrameworkElement;
        var scale = root?.XamlRoot?.RasterizationScale ?? 1.0;
        var width = root?.ActualWidth ?? 1280;
        var height = root?.ActualHeight ?? 720;
        return ((int)Math.Max(64, width * scale), (int)Math.Max(64, height * scale));
    }

    private void InitSystemMedia()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        _media = new SystemMediaControls(hwnd, DispatcherQueue);
        _media.PlayPressed += () => _engine?.Play();
        _media.PausePressed += () => _engine?.Pause();
        _media.NextPressed += () => PlayEpisode(_episodeIndex + 1);
        _media.PreviousPressed += () => PlayEpisode(_episodeIndex - 1);
    }

    private void DisposeSystemMedia()
    {
        _media?.Dispose();
        _media = null;
    }

    /// <summary>Title, episode and artwork for the system media flyout.</summary>
    private void UpdateSystemMediaInfo()
    {
        var headline = EpisodeHeadline(_episodeIndex);
        // Also names screenshots ("绝命毒师 第 2 集 00.12.31.png"); path-hostile characters are dropped.
        var title = headline.Length > 0 ? $"{_request.Item.Title} {headline}" : _request.Item.Title;
        Mpv?.SetMediaTitle(string.Concat(title.Where(c => !Path.GetInvalidFileNameChars().Contains(c))));
        _media?.SetInfo(_request.Item.Title, headline.Length > 0 ? headline : _request.Item.MetaLine,
            _tmdb.ImageUrl(_request.Item.BackdropPath ?? _request.Item.PosterPath, "w780"));
        _media?.SetEpisodeButtons(_episodeIndex > 0, HasNext);
    }

    /// <summary>No screen saver, display sleep or system sleep while playing; released on pause and on leave.</summary>
    private void KeepAwake(bool on)
    {
        if (on == _keepingAwake) return;
        _keepingAwake = on;
        SetThreadExecutionState(on ? EsContinuous | EsDisplayRequired | EsSystemRequired : EsContinuous);
    }

    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
}

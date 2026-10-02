using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;

namespace MoonMovie.Playback.Engines;

/// <summary>Media Foundation (Windows.Media.Playback) — the fallback engine.</summary>
public sealed class SystemEngine : IPlaybackEngine
{
    private readonly DispatcherQueue _ui;
    private readonly MediaPlayerElement _view;
    private MediaPlayer? _player;
    private TimeSpan _pendingStart;
    private int _openVersion;

    public SystemEngine(DispatcherQueue ui)
    {
        _ui = ui;
        _player = new MediaPlayer { AutoPlay = true };
        _player.CommandManager.IsEnabled = false; // the page drives the system media controls itself
        _player.MediaOpened += (_, _) => _ui.Enqueue(OnOpened);
        _player.MediaFailed += (_, e) => _ui.Enqueue(() => Failed?.Invoke(e.ErrorMessage));
        _player.MediaEnded += (_, _) => _ui.Enqueue(() => Ended?.Invoke());
        _player.PlaybackSession.PlaybackStateChanged += (_, _) => _ui.Enqueue(() => StateChanged?.Invoke());
        _view = new MediaPlayerElement
        {
            AreTransportControlsEnabled = false,
            IsHitTestVisible = false,
            Stretch = Stretch.Uniform,
        };
        _view.SetMediaPlayer(_player);
    }

    public string Name => "系统";

    public FrameworkElement View => _view;

    public EngineState State => _player?.PlaybackSession.PlaybackState switch
    {
        MediaPlaybackState.Opening => EngineState.Opening,
        MediaPlaybackState.Buffering => EngineState.Buffering,
        MediaPlaybackState.Playing => EngineState.Playing,
        MediaPlaybackState.Paused => EngineState.Paused,
        _ => EngineState.Idle,
    };

    public TimeSpan Position => _player?.PlaybackSession.Position ?? TimeSpan.Zero;

    public TimeSpan Duration => _player?.PlaybackSession.NaturalDuration ?? TimeSpan.Zero;

    public double BufferedAhead
    {
        get
        {
            if (_player is null) return 0;
            var position = Position;
            try
            {
                foreach (var range in _player.PlaybackSession.GetBufferedRanges())
                {
                    if (range.Start <= position + TimeSpan.FromSeconds(1) && range.End >= position)
                    {
                        return (range.End - position).TotalSeconds;
                    }
                }
            }
            catch (System.Runtime.InteropServices.COMException)
            {
            }

            return 0;
        }
    }

    public double Rate
    {
        get => _player?.PlaybackSession.PlaybackRate is > 0 and var r ? r : 1.0;
        set
        {
            if (_player is not null) _player.PlaybackSession.PlaybackRate = value;
        }
    }

    public double Volume
    {
        get => _player?.Volume ?? 1;
        set
        {
            if (_player is not null) _player.Volume = Math.Clamp(value, 0, 1);
        }
    }

    public bool Muted
    {
        get => _player?.IsMuted ?? false;
        set
        {
            if (_player is not null) _player.IsMuted = value;
        }
    }

    public event Action? Opened;

    public event Action? StateChanged;

    public event Action? Ended;

    public event Action<string>? Failed;

    public event Action? PositionChanged
    {
        add { }
        remove { }
    }

    public async Task OpenAsync(string url, TimeSpan start, bool isHls)
    {
        if (_player is null) return;
        var version = ++_openVersion;
        _pendingStart = start;

        MediaSource source;
        if (isHls)
        {
            var result = await AdaptiveMediaSource.CreateFromUriAsync(new Uri(url));
            if (version != _openVersion) return;
            if (result.Status != AdaptiveMediaSourceCreationStatus.Success)
            {
                Failed?.Invoke($"无法解析播放列表（{result.Status}）");
                return;
            }

            // Start at the best rendition: resource sites rarely offer more than one anyway.
            if (result.MediaSource.AvailableBitrates.Count > 0)
            {
                result.MediaSource.InitialBitrate = result.MediaSource.AvailableBitrates.Max();
            }

            source = MediaSource.CreateFromAdaptiveMediaSource(result.MediaSource);
        }
        else
        {
            source = MediaSource.CreateFromUri(new Uri(url));
        }

        if (version != _openVersion || _player is null) return;
        _player.Source = new MediaPlaybackItem(source);
    }

    private void OnOpened()
    {
        if (_player is null) return;
        if (_pendingStart > TimeSpan.Zero) _player.PlaybackSession.Position = _pendingStart;
        _pendingStart = TimeSpan.Zero;
        Opened?.Invoke();
    }

    public void Play() => _player?.Play();

    public void Pause() => _player?.Pause();

    public void Seek(TimeSpan position, bool exact = false)
    {
        if (_player is not null) _player.PlaybackSession.Position = position;
    }

    public void Dispose()
    {
        if (_player is null) return;
        _player.Pause();
        _view.SetMediaPlayer(null);
        _player.Dispose();
        _player = null;
    }
}

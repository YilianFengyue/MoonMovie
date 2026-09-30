using System.Collections.ObjectModel;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using MoonMovie.Core.Models;
using MoonMovie.Core.Playback;
using MoonMovie.Core.Sources;
using MoonMovie.Playback;
using MoonMovie.Services;
using MoonMovie.ViewModels;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace MoonMovie.Views;

public sealed partial class PlayerPage : Page
{
    private static readonly double[] Speeds = [0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 3.0];
    private static readonly TimeSpan ChromeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(14);
    private static double _volume = 1.0;

    private readonly MediaProxy _proxy = App.Services.GetRequiredService<MediaProxy>();
    private readonly WatchProgressStore _progress = App.Services.GetRequiredService<WatchProgressStore>();
    private readonly DispatcherQueueTimer _tick;
    private readonly DispatcherQueueTimer _chromeTimer;
    private readonly DispatcherQueueTimer _toastTimer;
    private readonly ObservableCollection<PlayerEpisodeItem> _episodes = [];
    private readonly HashSet<string> _failedSources = [];

    private MediaPlayer? _player;
    private PlaybackRequest _request = null!;
    private SourceItemViewModel _source = null!;
    private int _episodeIndex;
    private int _openVersion;
    private TimeSpan _pendingSeek;
    private TimeSpan _duration;
    private DateTimeOffset _stallSince = DateTimeOffset.MaxValue;
    private DateTimeOffset _lastSave;
    private bool _seeking;
    private bool _updatingSeekBar;
    private bool _chromeVisible = true;
    private bool _sideOpen;
    private Action? _toastAction;
    private Windows.Foundation.Point _lastPointer = new(-100, -100);

    public PlayerPage()
    {
        InitializeComponent();
        IsTabStop = true; // keyboard shortcuts need focus inside the page

        _tick = DispatcherQueue.CreateTimer();
        _tick.Interval = TimeSpan.FromMilliseconds(250);
        _tick.Tick += (_, _) => OnTick();

        _chromeTimer = DispatcherQueue.CreateTimer();
        _chromeTimer.Interval = ChromeTimeout;
        _chromeTimer.IsRepeating = false;
        _chromeTimer.Tick += (_, _) => HideChromeIfIdle();

        _toastTimer = DispatcherQueue.CreateTimer();
        _toastTimer.IsRepeating = false;
        _toastTimer.Tick += (_, _) => Toast.Visibility = Visibility.Collapsed;

        SeekBar.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => _seeking = true), true);
        SeekBar.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => EndSeekDrag()), true);
        SeekBar.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => EndSeekDrag()), true);
        AddHandler(KeyDownEvent, new KeyEventHandler(OnKeyDown), true);

        foreach (var speed in Speeds)
        {
            var item = new RadioMenuFlyoutItem { Text = $"{speed:0.##}x", GroupName = "speed", IsChecked = speed == 1.0, Tag = speed };
            item.Click += (_, _) => SetSpeed(speed);
            SpeedMenu.Items.Add(item);
        }

        EpisodesRepeater.ItemsSource = _episodes;
        ElementCompositionPreview.GetElementVisual(CenterGlyphHost).Opacity = 0;
        _proxy.AdsRemoved += OnAdsRemoved;
    }

    // ----- Lifecycle -------------------------------------------------------------------------------------

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not PlaybackRequest request)
        {
            return;
        }

        _request = request;
        _source = request.Source;
        _episodeIndex = request.EpisodeIndex;
        SourcesRepeater.ItemsSource = request.Sources.Items;
        request.Sources.Chosen += OnSourceChosen;

        App.MainWindow.SetImmersive(true);

        _player = new MediaPlayer { AutoPlay = true, Volume = _volume };
        _player.MediaOpened += (_, _) => DispatcherQueue.TryEnqueue(OnMediaOpened);
        _player.MediaFailed += (_, args) => DispatcherQueue.TryEnqueue(() => OnMediaFailed(args.ErrorMessage));
        _player.MediaEnded += (_, _) => DispatcherQueue.TryEnqueue(OnMediaEnded);
        _player.PlaybackSession.PlaybackStateChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdatePlaybackState);
        Video.SetMediaPlayer(_player);
        VolumeBar.Value = _volume * 100;

        BuildEpisodeList();
        _ = OpenAsync(resume: true);
        _tick.Start();
        ShowChrome();
        Focus(FocusState.Programmatic);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        SaveProgress(flush: true);

        _tick.Stop();
        _chromeTimer.Stop();
        _toastTimer.Stop();
        _proxy.AdsRemoved -= OnAdsRemoved;
        _request.Sources.Chosen -= OnSourceChosen;

        if (_player is not null)
        {
            _player.Pause();
            Video.SetMediaPlayer(null);
            _player.Dispose();
            _player = null;
        }

        App.MainWindow.SetFullScreen(false);
        App.MainWindow.SetTitleBarVisible(true);
        App.MainWindow.SetImmersive(false);
    }

    // ----- Opening, failover, episodes ------------------------------------------------------------------

    private PlayLine Line => _source.Candidate.PrimaryLine;

    private async Task OpenAsync(bool resume, TimeSpan? startAt = null)
    {
        if (_player is null) return;

        var version = ++_openVersion;
        _episodeIndex = Math.Clamp(_episodeIndex, 0, Line.Episodes.Count - 1);
        var episode = Line.Episodes[_episodeIndex];

        ErrorPanel.Visibility = Visibility.Collapsed;
        BufferRing.Visibility = Visibility.Visible;
        _duration = TimeSpan.Zero;
        _stallSince = DateTimeOffset.Now;
        UpdateTitles();
        UpdateEpisodeMarkers();

        var saved = _progress.Get(_request.Item.MediaKey, _request.Season, _episodeIndex);
        _pendingSeek = startAt ?? (resume && saved is { IsFinished: false, PositionMs: > 30_000 }
            ? TimeSpan.FromMilliseconds(saved.PositionMs)
            : TimeSpan.Zero);

        try
        {
            MediaSource source;
            if (episode.Url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                var uri = await _proxy.PlaylistUriAsync(episode.Url);
                var result = await AdaptiveMediaSource.CreateFromUriAsync(uri);
                if (version != _openVersion) return;
                if (result.Status != AdaptiveMediaSourceCreationStatus.Success)
                {
                    OnMediaFailed($"无法解析播放列表（{result.Status}）");
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
                source = MediaSource.CreateFromUri(await _proxy.FileUriAsync(episode.Url));
            }

            if (version != _openVersion) return;
            _player.Source = new MediaPlaybackItem(source);
            _player.PlaybackSession.PlaybackRate = CurrentSpeed;
        }
        catch (Exception ex)
        {
            if (version == _openVersion) OnMediaFailed(ex.Message);
        }
    }

    private void OnMediaOpened()
    {
        if (_player is null) return;

        _duration = _player.PlaybackSession.NaturalDuration;
        _stallSince = DateTimeOffset.MaxValue;
        _failedSources.Clear();

        if (_pendingSeek > TimeSpan.Zero && (_duration == TimeSpan.Zero || _pendingSeek < _duration - TimeSpan.FromSeconds(30)))
        {
            var resumeAt = _pendingSeek;
            _player.PlaybackSession.Position = resumeAt;
            ShowToast($"从 {TimeText.Format(resumeAt)} 继续播放", "从头播放", () =>
            {
                if (_player is not null) _player.PlaybackSession.Position = TimeSpan.Zero;
            }, TimeSpan.FromSeconds(7));
        }

        _pendingSeek = TimeSpan.Zero;
    }

    private void OnMediaFailed(string? reason)
    {
        _failedSources.Add(_source.Candidate.Identity);
        var position = _player?.PlaybackSession.Position ?? TimeSpan.Zero;

        // Automatic failover to the next reachable source, keeping the position.
        var next = _request.Sources.Items.FirstOrDefault(i =>
            i.State is ProbeOutcome.Ok or ProbeOutcome.Slow
            && !_failedSources.Contains(i.Candidate.Identity)
            && i.Candidate.PrimaryLine.Episodes.Count > _episodeIndex);

        if (next is not null)
        {
            ShowToast($"{_source.SiteName} 无法播放，已切换到 {next.SiteName}");
            SwitchSource(next, position > TimeSpan.Zero ? position : _pendingSeek);
            return;
        }

        BufferRing.Visibility = Visibility.Collapsed;
        ErrorText.Text = $"所有可用片源都无法播放这一集。{(string.IsNullOrWhiteSpace(reason) ? string.Empty : "\n" + reason)}";
        ErrorPanel.Visibility = Visibility.Visible;
        ShowChrome(pin: true);
    }

    private void OnMediaEnded()
    {
        SaveProgress(flush: true);
        if (_episodeIndex + 1 < Line.Episodes.Count)
        {
            ShowToast($"即将播放 {EpisodeLabel(_episodeIndex + 1)}");
            PlayEpisode(_episodeIndex + 1);
        }
        else
        {
            ShowChrome(pin: true);
        }
    }

    private void SwitchSource(SourceItemViewModel source, TimeSpan startAt)
    {
        SaveProgress(flush: false);
        _source = source;
        BuildEpisodeList();
        _ = OpenAsync(resume: false, startAt);
    }

    private void OnSourceChosen(object? sender, SourceItemViewModel chosen)
    {
        if (chosen == _source) return;
        var position = _player?.PlaybackSession.Position ?? TimeSpan.Zero;
        _failedSources.Clear();
        CloseSidePanel();
        ShowToast($"已切换到 {chosen.SiteName}");
        SwitchSource(chosen, position);
    }

    private void PlayEpisode(int index)
    {
        if (index == _episodeIndex || index < 0 || index >= Line.Episodes.Count) return;
        SaveProgress(flush: true);
        _episodeIndex = index;
        _ = OpenAsync(resume: true);
    }

    private void BuildEpisodeList()
    {
        _episodes.Clear();
        for (var i = 0; i < Line.Episodes.Count; i++)
        {
            var watched = _progress.Get(_request.Item.MediaKey, _request.Season, i);
            _episodes.Add(new PlayerEpisodeItem(i, EpisodeLabel(i), EpisodeName(i), watched?.Fraction ?? 0) { IsCurrent = i == _episodeIndex });
        }

        var multiple = Line.Episodes.Count > 1;
        EpisodesButton.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
        EpisodesTab.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
        UpdateEpisodeMarkers();
    }

    private void UpdateEpisodeMarkers()
    {
        foreach (var e in _episodes) e.IsCurrent = e.Index == _episodeIndex;
        NextButton.Visibility = _episodeIndex + 1 < Line.Episodes.Count ? Visibility.Visible : Visibility.Collapsed;
    }

    private string EpisodeLabel(int index) =>
        _request.Item.Kind == MediaKind.Tv ? $"第 {index + 1} 集" : Line.Episodes[index].Name;

    private string? EpisodeName(int index) =>
        _request.Item.Kind == MediaKind.Tv
            ? _request.Episodes.FirstOrDefault(e => e.Number == index + 1)?.Name
            : null;

    private void UpdateTitles()
    {
        TitleText.Text = _request.Item.Title;
        var parts = new List<string>(3);
        if (_request.Item.Kind == MediaKind.Tv)
        {
            var label = _request.Season is > 1 ? $"第 {_request.Season} 季 · {EpisodeLabel(_episodeIndex)}" : EpisodeLabel(_episodeIndex);
            var name = EpisodeName(_episodeIndex);
            parts.Add(name is null || name.Contains("集") ? label : $"{label}  {name}");
        }
        else if (Line.Episodes.Count > 1)
        {
            parts.Add(Line.Episodes[_episodeIndex].Name);
        }

        parts.Add(_source.SiteName);
        SubtitleText.Text = string.Join("  ·  ", parts);
    }

    // ----- Ticking: position, stall watchdog, progress ----------------------------------------------------

    private void OnTick()
    {
        if (_player is null) return;
        var session = _player.PlaybackSession;
        var position = session.Position;

        if (_duration == TimeSpan.Zero && session.NaturalDuration > TimeSpan.Zero)
        {
            _duration = session.NaturalDuration;
        }

        if (!_seeking)
        {
            _updatingSeekBar = true;
            SeekBar.Maximum = Math.Max(1, _duration.TotalSeconds);
            SeekBar.Value = Math.Min(position.TotalSeconds, SeekBar.Maximum);
            _updatingSeekBar = false;
            PositionText.Text = TimeText.Format(position);
        }

        DurationText.Text = TimeText.Format(_duration);

        // A source that stops delivering data is as bad as one that fails outright.
        if (session.PlaybackState is MediaPlaybackState.Opening or MediaPlaybackState.Buffering)
        {
            if (_stallSince == DateTimeOffset.MaxValue) _stallSince = DateTimeOffset.Now;
            if (DateTimeOffset.Now - _stallSince > StallLimit && ErrorPanel.Visibility != Visibility.Visible)
            {
                _stallSince = DateTimeOffset.MaxValue;
                OnMediaFailed("缓冲超时");
            }
        }
        else
        {
            _stallSince = DateTimeOffset.MaxValue;
        }

        if (session.PlaybackState == MediaPlaybackState.Playing && DateTimeOffset.Now - _lastSave > TimeSpan.FromSeconds(5))
        {
            SaveProgress(flush: false);
        }
    }

    private void SaveProgress(bool flush)
    {
        if (_player is null || _duration <= TimeSpan.Zero) return;
        var position = _player.PlaybackSession.Position;
        if (position < TimeSpan.FromSeconds(5)) return;

        var item = _request.Item;
        _progress.Save(new WatchProgress
        {
            MediaKey = item.MediaKey,
            TmdbId = item.TmdbId,
            Kind = item.Kind,
            Title = item.Title,
            PosterPath = item.PosterPath,
            BackdropPath = item.BackdropPath,
            Season = _request.Season,
            EpisodeIndex = _episodeIndex,
            EpisodeLabel = item.Kind == MediaKind.Tv ? EpisodeLabel(_episodeIndex) : null,
            PositionMs = (long)position.TotalMilliseconds,
            DurationMs = (long)_duration.TotalMilliseconds,
            SourceKey = _source.Candidate.Identity,
        }, flush);
        _lastSave = DateTimeOffset.Now;
    }

    private void UpdatePlaybackState()
    {
        if (_player is null) return;
        var state = _player.PlaybackSession.PlaybackState;
        BufferRing.Visibility = state is MediaPlaybackState.Opening or MediaPlaybackState.Buffering
            ? Visibility.Visible
            : Visibility.Collapsed;
        PlayPauseGlyph.Glyph = state == MediaPlaybackState.Playing ? "" : "";

        if (state == MediaPlaybackState.Paused) ShowChrome(pin: true);
        else if (state == MediaPlaybackState.Playing) ShowChrome();
    }

    // ----- Transport ------------------------------------------------------------------------------------

    private void TogglePlay()
    {
        if (_player is null) return;
        var playing = _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
        if (playing) _player.Pause(); else _player.Play();
        FlashCenter(playing ? "" : "");
    }

    private void SeekBy(double seconds)
    {
        if (_player is null) return;
        var target = _player.PlaybackSession.Position + TimeSpan.FromSeconds(seconds);
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        if (_duration > TimeSpan.Zero && target > _duration) target = _duration - TimeSpan.FromSeconds(1);
        _player.PlaybackSession.Position = target;
        FlashCenter(seconds < 0 ? "" : "");
        ShowChrome();
    }

    private double CurrentSpeed => _player?.PlaybackSession.PlaybackRate is > 0 and var r ? r : 1.0;

    private void SetSpeed(double speed)
    {
        if (_player is null) return;
        _player.PlaybackSession.PlaybackRate = speed;
        SpeedText.Text = $"{speed:0.0#}x";
        foreach (var item in SpeedMenu.Items.OfType<RadioMenuFlyoutItem>()) item.IsChecked = (double)item.Tag == speed;
    }

    private void StepSpeed(int direction)
    {
        var index = Array.IndexOf(Speeds, Speeds.MinBy(s => Math.Abs(s - CurrentSpeed)));
        var next = Speeds[Math.Clamp(index + direction, 0, Speeds.Length - 1)];
        SetSpeed(next);
        ShowToast($"播放速度 {next:0.0#}x");
    }

    private void SetVolume(double volume)
    {
        _volume = Math.Clamp(volume, 0, 1);
        if (_player is not null)
        {
            _player.Volume = _volume;
            _player.IsMuted = false;
        }

        VolumeBar.Value = _volume * 100;
        UpdateVolumeGlyph();
    }

    private void UpdateVolumeGlyph() =>
        VolumeGlyph.Glyph = _player?.IsMuted == true || _volume == 0 ? "" : _volume < 0.5 ? "" : "";

    private void ToggleFullScreen()
    {
        var full = !App.MainWindow.IsFullScreen;
        App.MainWindow.SetFullScreen(full);
        FullScreenGlyph.Glyph = full ? "" : "";
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => TogglePlay();

    private void OnBack10Click(object sender, RoutedEventArgs e) => SeekBy(-10);

    private void OnForward10Click(object sender, RoutedEventArgs e) => SeekBy(10);

    private void OnNextClick(object sender, RoutedEventArgs e) => PlayEpisode(_episodeIndex + 1);

    private void OnFullScreenClick(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void OnMuteClick(object sender, RoutedEventArgs e)
    {
        if (_player is null) return;
        _player.IsMuted = !_player.IsMuted;
        UpdateVolumeGlyph();
    }

    private void OnVolumeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _volume = e.NewValue / 100;
        if (_player is not null)
        {
            _player.Volume = _volume;
            if (_volume > 0) _player.IsMuted = false;
        }

        UpdateVolumeGlyph();
    }

    private void OnSeekValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingSeekBar || _player is null) return;
        PositionText.Text = TimeText.Format(TimeSpan.FromSeconds(e.NewValue));
        if (!_seeking)
        {
            _player.PlaybackSession.Position = TimeSpan.FromSeconds(e.NewValue);
        }
    }

    private void EndSeekDrag()
    {
        if (!_seeking) return;
        _seeking = false;
        if (_player is not null)
        {
            _player.PlaybackSession.Position = TimeSpan.FromSeconds(SeekBar.Value);
        }
    }

    private void OnRetryClick(object sender, RoutedEventArgs e)
    {
        _failedSources.Clear();
        _ = OpenAsync(resume: true);
    }

    // ----- Surface & keyboard ---------------------------------------------------------------------------

    private void OnSurfaceTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_sideOpen)
        {
            CloseSidePanel();
            return;
        }

        TogglePlay();
    }

    private void OnSurfaceDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        TogglePlay(); // undo the toggle from the first tap
        ToggleFullScreen();
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                     & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        switch (e.Key)
        {
            case VirtualKey.Space or VirtualKey.K:
                TogglePlay();
                break;
            case VirtualKey.Left:
                SeekBy(shift ? -30 : -5);
                break;
            case VirtualKey.Right:
                SeekBy(shift ? 30 : 5);
                break;
            case VirtualKey.J:
                SeekBy(-10);
                break;
            case VirtualKey.L:
                SeekBy(10);
                break;
            case VirtualKey.Up:
                SetVolume(_volume + 0.05);
                ShowToast($"音量 {Math.Round(_volume * 100)}");
                break;
            case VirtualKey.Down:
                SetVolume(_volume - 0.05);
                ShowToast($"音量 {Math.Round(_volume * 100)}");
                break;
            case VirtualKey.F:
                ToggleFullScreen();
                break;
            case VirtualKey.M:
                OnMuteClick(this, new RoutedEventArgs());
                break;
            case VirtualKey.N:
                PlayEpisode(_episodeIndex + 1);
                break;
            case (VirtualKey)219: // [
                StepSpeed(-1);
                break;
            case (VirtualKey)221: // ]
                StepSpeed(1);
                break;
            case VirtualKey.Escape:
                if (_sideOpen) CloseSidePanel();
                else if (App.MainWindow.IsFullScreen) ToggleFullScreen();
                else App.MainWindow.GoBack();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    // ----- Chrome visibility ----------------------------------------------------------------------------

    /// <summary>
    /// Only real movement wakes the chrome: hiding it changes what is under a resting cursor, and XAML reports that
    /// as a pointer move — which would otherwise bring the chrome straight back.
    /// </summary>
    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root).Position;
        if (Math.Abs(point.X - _lastPointer.X) + Math.Abs(point.Y - _lastPointer.Y) < 3) return;
        _lastPointer = point;
        ShowChrome();
    }

    private void ShowChrome(bool pin = false)
    {
        if (!_chromeVisible)
        {
            _chromeVisible = true;
            AnimateChrome(1);
            App.MainWindow.SetTitleBarVisible(true);
        }

        _chromeTimer.Stop();
        if (!pin) _chromeTimer.Start();
    }

    private void HideChromeIfIdle()
    {
        var playing = _player?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
        if (!playing || _sideOpen || _seeking || SpeedMenu.IsOpen) return;

        _chromeVisible = false;
        AnimateChrome(0);
        App.MainWindow.SetTitleBarVisible(false);
    }

    private void AnimateChrome(float to)
    {
        var visual = ElementCompositionPreview.GetElementVisual(Chrome);
        var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, to);
        fade.Duration = TimeSpan.FromMilliseconds(to > 0 ? 160 : 420);
        visual.StartAnimation("Opacity", fade);
        Chrome.IsHitTestVisible = to > 0;
    }

    /// <summary>Big glyph that pops and fades in the centre, like a console player.</summary>
    private void FlashCenter(string glyph)
    {
        CenterGlyph.Glyph = glyph;
        var visual = ElementCompositionPreview.GetElementVisual(CenterGlyphHost);
        visual.CenterPoint = new Vector3(46, 46, 0);
        var compositor = visual.Compositor;

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0.95f);
        fade.InsertKeyFrame(1f, 0f, compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(0.6f, 1f)));
        fade.Duration = TimeSpan.FromMilliseconds(560);

        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0f, new Vector3(0.8f, 0.8f, 1f));
        scale.InsertKeyFrame(1f, new Vector3(1.15f, 1.15f, 1f));
        scale.Duration = TimeSpan.FromMilliseconds(560);

        visual.StartAnimation("Opacity", fade);
        visual.StartAnimation("Scale", scale);
    }

    // ----- Toast ----------------------------------------------------------------------------------------

    private void ShowToast(string text, string? action = null, Action? onAction = null, TimeSpan? duration = null)
    {
        ToastText.Text = text;
        _toastAction = onAction;
        ToastAction.Content = action;
        ToastAction.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Interval = duration ?? TimeSpan.FromSeconds(3);
        _toastTimer.Start();
    }

    private void OnToastActionClick(object sender, RoutedEventArgs e)
    {
        _toastAction?.Invoke();
        Toast.Visibility = Visibility.Collapsed;
    }

    private void OnAdsRemoved(int segments, double seconds) =>
        DispatcherQueue.TryEnqueue(() => ShowToast($"已跳过片源插播广告 {Math.Round(seconds)} 秒"));

    // ----- Side panel -----------------------------------------------------------------------------------

    private void OnEpisodesClick(object sender, RoutedEventArgs e) => OpenSidePanel(EpisodesTab);

    private void OnSourcesClick(object sender, RoutedEventArgs e) => OpenSidePanel(SourcesTab);

    private void OpenSidePanel(SelectorBarItem tab)
    {
        _sideOpen = true;
        SideLayer.Visibility = Visibility.Visible;
        PanelTabs.SelectedItem = tab.Visibility == Visibility.Visible ? tab : SourcesTab;
        ShowChrome(pin: true);
    }

    private void CloseSidePanel()
    {
        _sideOpen = false;
        SideLayer.Visibility = Visibility.Collapsed;
        ShowChrome();
    }

    private void OnSideDismiss(object sender, RoutedEventArgs e) => CloseSidePanel();

    private void OnSideDismissTapped(object sender, TappedRoutedEventArgs e) => CloseSidePanel();

    private void OnPanelTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var episodes = sender.SelectedItem == EpisodesTab;
        EpisodesView.Visibility = episodes ? Visibility.Visible : Visibility.Collapsed;
        SourcesView.Visibility = episodes ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnEpisodeRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: int index })
        {
            CloseSidePanel();
            PlayEpisode(index);
        }
    }
}

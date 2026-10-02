using System.Collections.ObjectModel;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using MoonMovie.Animations;
using MoonMovie.Core.Models;
using MoonMovie.Core.Playback;
using MoonMovie.Core.Sources;
using MoonMovie.Core.Tmdb;
using MoonMovie.Imaging;
using MoonMovie.Playback;
using MoonMovie.Services;
using MoonMovie.ViewModels;
using MoonMovie.Playback.Engines;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace MoonMovie.Views;

public sealed partial class PlayerPage : Page
{
    private static readonly double[] Speeds = [0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 3.0];
    private static readonly TimeSpan ChromeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(14);
    private static readonly TimeSpan PausedInfoDelay = TimeSpan.FromSeconds(2.2);
    private static readonly TimeSpan NextUpLead = TimeSpan.FromSeconds(30);
    private static double _volume = 1.0;
    private static bool _showRemaining;

    private readonly MediaProxy _proxy = App.Services.GetRequiredService<MediaProxy>();
    private readonly WatchProgressStore _progress = App.Services.GetRequiredService<WatchProgressStore>();
    private readonly TmdbClient _tmdb = App.Services.GetRequiredService<TmdbClient>();
    private readonly DispatcherQueueTimer _tick;
    private readonly DispatcherQueueTimer _chromeTimer;
    private readonly DispatcherQueueTimer _toastTimer;
    private readonly DispatcherQueueTimer _pausedTimer;
    private readonly DispatcherQueueTimer _hudTimer;
    private readonly ObservableCollection<PlayerEpisodeItem> _episodes = [];
    private readonly HashSet<string> _failedSources = [];

    private IPlaybackEngine? _engine;
    private PlaybackRequest _request = null!;
    private SourceItemViewModel _source = null!;
    private int _episodeIndex;
    private int _openVersion;
    private TimeSpan _pendingSeek;
    private double _speed = 1.0;
    private TimeSpan _duration;
    private DateTimeOffset _stallSince = DateTimeOffset.MaxValue;
    private DateTimeOffset _lastSave;
    private bool _awaitingFirstFrame;
    private bool _chromeVisible = true;
    private bool _sideOpen;
    private bool _cursorHidden;
    private bool _nextShown;
    private bool _nextCancelled;
    private bool _pausedInfoShown;
    private bool _thumbTaken;
    private bool _nextPrefetched;
    private int _taskbarTick;
    private string? _thumbPath;
    private Action? _toastAction;
    private Windows.Foundation.Point _lastPointer = new(-100, -100);

    public PlayerPage()
    {
        InitializeComponent();
        IsTabStop = true; // keyboard shortcuts need focus inside the page
        // Focusing in OnNavigatedTo fails while the page is not in the tree yet (e.g. opened from a card click).
        Loaded += (_, _) => Focus(FocusState.Programmatic);

        _tick = CreateTimer(TimeSpan.FromMilliseconds(250), repeating: true, OnTick);
        _chromeTimer = CreateTimer(ChromeTimeout, repeating: false, HideChromeIfIdle);
        _toastTimer = CreateTimer(TimeSpan.FromSeconds(3), repeating: false, () => Toast.Visibility = Visibility.Collapsed);
        _pausedTimer = CreateTimer(PausedInfoDelay, repeating: false, ShowPausedInfo);
        _hudTimer = CreateTimer(TimeSpan.FromSeconds(1.1), repeating: false, () => Motion.FadeTo(Hud, 0, TimeSpan.FromMilliseconds(300)));

        AddHandler(KeyDownEvent, new KeyEventHandler(OnKeyDown), true);
        SeekBar.SeekRequested += (_, time) => SeekTo(time);
        SeekBar.Scrubbing += (_, time) =>
        {
            if (time is { } t) UpdateTimeText(t);
            ShowChrome(pin: time is not null);
        };

        foreach (var speed in Speeds)
        {
            var item = new RadioMenuFlyoutItem { Text = $"{speed:0.##}x", GroupName = "speed", IsChecked = speed == 1.0, Tag = speed };
            item.Click += (_, _) => SetSpeed(speed);
            SpeedMenu.Items.Add(item);
        }

        EpisodesRepeater.ItemsSource = _episodes;
        ElementCompositionPreview.GetElementVisual(CenterGlyphHost).Opacity = 0;
        _proxy.AdsRemoved += OnAdsRemoved;
        InitDanmaku();
    }

    private DispatcherQueueTimer CreateTimer(TimeSpan interval, bool repeating, Action tick)
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = interval;
        timer.IsRepeating = repeating;
        timer.Tick += (_, _) => tick();
        return timer;
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
        ImageEx.SetUrl(LoadingBackdrop, _tmdb.ImageUrl(request.Item.BackdropPath, "w1280"));

        CreateEngine();
        InitSystemMedia();
        InitTaskbar();
        VolumeBar.Value = _volume * 100;

        BuildEpisodeList();
        StartDanmaku();
        _ = OpenAsync(resume: true);
        _tick.Start();
        ShowChrome();
        Focus(FocusState.Programmatic);
#if DEBUG
        // QA hook: MOONMOVIE_DEBUG_PLAYER_CHROME=1 keeps the controls up (for screenshots).
        if (Environment.GetEnvironmentVariable("MOONMOVIE_DEBUG_PLAYER_CHROME") == "1")
        {
            _ = Task.Delay(6000).ContinueWith(_ => DispatcherQueue.TryEnqueue(() => ShowChrome(pin: true)));
        }

        // QA hook: MOONMOVIE_DEBUG_PLAYER_PANEL=comments|danmaku|info opens that after a few seconds.
        if (Environment.GetEnvironmentVariable("MOONMOVIE_DEBUG_PLAYER_PANEL") is { Length: > 0 } debugPanel)
        {
            _ = Task.Delay(5000).ContinueWith(_ => DispatcherQueue.TryEnqueue(() =>
            {
                if (debugPanel == "info") ToggleInfoPanel();
                else OpenSidePanel(debugPanel switch { "danmaku" => DanmakuTab, "picture" => PictureTab, _ => CommentsTab });
            }));
        }
#endif
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        SaveProgress(flush: true);

        foreach (var timer in new[] { _tick, _chromeTimer, _toastTimer, _pausedTimer, _hudTimer }) timer.Stop();
        _proxy.AdsRemoved -= OnAdsRemoved;
        _request.Sources.Chosen -= OnSourceChosen;
        SetCursorHidden(false);
        StopDanmaku();
        KeepAwake(false);

        DisposeSystemMedia();
        DisposeTaskbar();
        if (_engine is not null)
        {
            _engine.Pause();
            VideoHost.Children.Clear();
            _engine.Dispose();
            _engine = null;
        }

        App.MainWindow.SetCompactOverlay(false);
        App.MainWindow.SetFullScreen(false);
        App.MainWindow.SetTitleBarVisible(true);
        App.MainWindow.SetImmersive(false);
    }

    // ----- Opening, failover, episodes ------------------------------------------------------------------

    private PlayLine Line => _source.Candidate.PrimaryLine;

    private async Task OpenAsync(bool resume, TimeSpan? startAt = null)
    {
        if (_engine is null) return;

        var version = ++_openVersion;
        _episodeIndex = Math.Clamp(_episodeIndex, 0, Line.Episodes.Count - 1);
        var episode = Line.Episodes[_episodeIndex];

        ErrorPanel.Visibility = Visibility.Collapsed;
        ResetSkips();
        _thumbTaken = false;
        _nextPrefetched = false;
        HideNextCard(resetCancel: true);
        HidePausedInfo();
        ShowLoading(LocalPlayback.IsLocal(_source.Candidate) ? "正在打开…" : $"正在连接 {_source.SiteName}…");
        _duration = TimeSpan.Zero;
        _stallSince = DateTimeOffset.Now;
        UpdateTitles();
        UpdateEpisodeMarkers();
        _biliStream = null;
        _pgcClips = [];
        SyncBiliQualities();
        ConfigureBiliPanels();
        LoadDanmakuForEpisode();

        var saved = _progress.Get(_request.Item.MediaKey, _request.Season, _episodeIndex);
        _pendingSeek = startAt ?? (resume && saved is { IsFinished: false, PositionMs: > 30_000 }
            ? TimeSpan.FromMilliseconds(saved.PositionMs)
            : TimeSpan.Zero);

        try
        {
            if (LocalPlayback.IsLocal(_source.Candidate))
            {
                await OpenLocalAsync(episode.Url);
                return;
            }

            if (BiliPlayback.IsBiliUrl(episode.Url))
            {
                await OpenBiliAsync(episode.Url, version, resume);
                return;
            }

            if (Core.Bilibili.BiliPgcSource.IsPgcUrl(episode.Url))
            {
                await OpenPgcAsync(episode.Url, version);
                return;
            }

            var isHls = episode.Url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);
            var uri = isHls ? await _proxy.PlaylistUriAsync(episode.Url) : await _proxy.FileUriAsync(episode.Url);
            if (version != _openVersion || _engine is null) return;

            _awaitingFirstFrame = true;
            var speed = _speed;
            await _engine.OpenAsync(uri.AbsoluteUri, _pendingSeek, isHls); // escaped: mpv sends the string verbatim
            _engine.Rate = speed;
            UpdateSystemMediaInfo();
        }
        catch (Exception ex)
        {
            if (version == _openVersion) OnMediaFailed(ex.Message);
        }
    }

    /// <summary>Files go to the engine as they are: no proxy, no ad stripping.</summary>
    private async Task OpenLocalAsync(string path)
    {
        if (_engine is null) return;
        if (path.Length == 0)
        {
            OnMediaFailed("本地没有这一集");
            return;
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            OnMediaFailed("文件不存在，或所在的磁盘没有连接");
            return;
        }

        var (target, dvdDevice) = LocalPlayback.Resolve(path);
        if (dvdDevice is not null) Mpv?.SetDvdDevice(dvdDevice);
        _awaitingFirstFrame = true;
        var speed = _speed;
        await _engine.OpenAsync(target, _pendingSeek, isHls: false);
        _engine.Rate = speed;
        UpdateSystemMediaInfo();
    }

    /// <summary>Subtitle files dropped on the window while playing.</summary>
    public void LoadSubtitles(IReadOnlyList<string> paths)
    {
        if (Mpv is not { } mpv)
        {
            ShowToast("系统内核不支持外挂字幕");
            return;
        }

        foreach (var path in paths) mpv.AddSubtitle(path);
        ShowToast(paths.Count == 1 ? $"已加载字幕 {Path.GetFileName(paths[0])}" : $"已加载 {paths.Count} 个字幕");
    }

    private void OnMediaOpened()
    {
        if (_engine is null) return;

        _duration = _engine.Duration;
        _stallSince = DateTimeOffset.MaxValue;
        _failedSources.Clear();
        LogVideoTracks("opened");
        LoadingText.Text = "正在缓冲…";

        if (_pendingSeek > TimeSpan.Zero)
        {
            // The engine already opened at this position; offer the way back to the start.
            var resumeAt = _pendingSeek;
            ShowToast($"从 {TimeText.Format(resumeAt)} 继续播放", "从头播放", () => _engine?.Seek(TimeSpan.Zero),
                TimeSpan.FromSeconds(7));
        }

        _pendingSeek = TimeSpan.Zero;
    }

    private void OnMediaFailed(string? reason)
    {
        _failedSources.Add(_source.Candidate.Identity);
        var position = _engine?.Position ?? TimeSpan.Zero;

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

        HideLoading();
        BufferRing.Visibility = Visibility.Collapsed;
        var lead = _request.Sources.Items.All(i => i.IsLocal) ? "这个文件无法播放。" : "所有可用片源都无法播放这一集。";
        ErrorText.Text = $"{lead}{(string.IsNullOrWhiteSpace(reason) ? string.Empty : "\n" + reason)}";
        ErrorPanel.Visibility = Visibility.Visible;
        ShowChrome(pin: true);
    }

    private void OnMediaEnded()
    {
        SaveProgress(flush: true);
        if (HasNext && !_nextCancelled && _settings.Current.Playback.AutoNext)
        {
            PlayEpisode(_episodeIndex + 1);
        }
        else
        {
            HideNextCard(resetCancel: false);
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
        var position = _engine?.Position ?? TimeSpan.Zero;
        _failedSources.Clear();
        CloseSidePanel();
        ShowToast($"已切换到 {chosen.SiteName}");
        SwitchSource(chosen, position);
    }

    private bool HasNext => _episodeIndex + 1 < Line.Episodes.Count;

    private void PlayEpisode(int index)
    {
        if (index == _episodeIndex || index < 0 || index >= Line.Episodes.Count) return;
        SaveProgress(flush: true);
        _episodeIndex = index;

        // Back to the files on disk whenever they have this episode (an online source only filled a gap).
        if (_request.Sources.Items.FirstOrDefault(i => i.IsLocal) is { } local && local != _source
            && local.Candidate.PrimaryLine.Episodes.ElementAtOrDefault(index)?.Url is { Length: > 0 })
        {
            _failedSources.Remove(local.Candidate.Identity);
            _source = local;
            BuildEpisodeList();
        }

        _ = OpenAsync(resume: true);
    }

    private void BuildEpisodeList()
    {
        _episodes.Clear();
        for (var i = 0; i < Line.Episodes.Count; i++)
        {
            var watched = _progress.Get(_request.Item.MediaKey, _request.Season, i);
            // 「会员」 on B站正版 episodes this account cannot play in full (they switch to another source).
            var label = Line.Episodes[i].Badge is { } badge && !IsBiliVip ? $"{EpisodeLabel(i)} · {badge}" : EpisodeLabel(i);
            _episodes.Add(new PlayerEpisodeItem(i, label, EpisodeName(i), watched?.Fraction ?? 0, StillUrl(i))
            {
                IsCurrent = i == _episodeIndex,
            });
        }

        var multiple = Line.Episodes.Count > 1;
        EpisodesButton.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
        // A collapsed SelectorBarItem blanks the whole bar, so single-episode titles drop the tab instead.
        if (!multiple) PanelTabs.Items.Remove(EpisodesTab);
        else if (!PanelTabs.Items.Contains(EpisodesTab)) PanelTabs.Items.Insert(0, EpisodesTab);
        UpdateEpisodeMarkers();
    }

    private void UpdateEpisodeMarkers()
    {
        foreach (var e in _episodes) e.IsCurrent = e.Index == _episodeIndex;
        NextButton.Visibility = HasNext ? Visibility.Visible : Visibility.Collapsed;
    }

    private string EpisodeLabel(int index) =>
        _request.Item.Kind == MediaKind.Tv ? $"第 {index + 1} 集" : Line.Episodes[index].Name;

    private EpisodeInfo? TmdbEpisode(int index) =>
        _request.Item.Kind == MediaKind.Tv ? _request.Episodes.FirstOrDefault(e => e.Number == index + 1) : null;

    private string? EpisodeName(int index) => TmdbEpisode(index)?.Name is { } name && !name.Contains('集')
        ? name
        : _request.Episodes.Count == 0 && Core.Bilibili.BiliPgcSource.IsPgcUrl(Line.Episodes[index].Url) ? Line.Episodes[index].Name : null;

    private string? StillUrl(int index) => _tmdb.ImageUrl(TmdbEpisode(index)?.StillPath, "w300");

    /// <summary>"第 2 季 · 第 3 集  名称" for series; the version name for multi-version films.</summary>
    private string EpisodeHeadline(int index)
    {
        if (_request.Item.Kind != MediaKind.Tv)
        {
            return Line.Episodes.Count > 1 ? Line.Episodes[index].Name : string.Empty;
        }

        var label = _request.Season is > 1 ? $"第 {_request.Season} 季 · {EpisodeLabel(index)}" : EpisodeLabel(index);
        return EpisodeName(index) is { } name ? $"{label}  {name}" : label;
    }

    private void UpdateTitles()
    {
        var headline = EpisodeHeadline(_episodeIndex);
        TitleText.Text = _request.Item.Title;
        SubtitleText.Text = headline.Length > 0 ? $"{headline}  ·  {_source.SiteName}" : _source.SiteName;
        NowPlayingText.Text = headline;
        LoadingTitle.Text = headline.Length > 0 ? $"{_request.Item.Title}  {headline}" : _request.Item.Title;

        PausedTitle.Text = _request.Item.Title;
        PausedMeta.Text = headline.Length > 0 ? headline : _request.Item.MetaLine;
        PausedOverview.Text = TmdbEpisode(_episodeIndex)?.Overview ?? _request.Item.Overview ?? string.Empty;
    }

    // ----- Ticking: position, buffer, stall watchdog, next-up, progress --------------------------------

    private void OnTick()
    {
        if (_engine is null) return;
        var position = _engine.Position;
        var state = _engine.State;

        if (_duration == TimeSpan.Zero && _engine.Duration > TimeSpan.Zero)
        {
            _duration = _engine.Duration;
            UpdateMarks(); // the credits mark needs the duration
        }

        var buffered = _duration > TimeSpan.Zero ? (position.TotalSeconds + _engine.BufferedAhead) / _duration.TotalSeconds : 0;
        SeekBar.Update(position, _duration, buffered);
        if (!SeekBar.IsDragging) UpdateTimeText(position);

        // A source that stops delivering data is as bad as one that fails outright.
        if (state is EngineState.Opening or EngineState.Buffering)
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

        UpdateNextCard(position);
        UpdateSkips(position);
        if (++_taskbarTick % 4 == 0) UpdateTaskbar();
        ReportBiliProgress(force: false);
        GrabThumbnail(position, state);
        PrefetchNext(position);
        TickUpscaler(state);
        SyncDanmaku(position);
        if (InfoPanel.Visibility == Visibility.Visible) UpdateInfoPanel();

        if (state == EngineState.Playing && DateTimeOffset.Now - _lastSave > TimeSpan.FromSeconds(5))
        {
            SaveProgress(flush: false);
        }
    }

#if DEBUG
    private DateTimeOffset _lastDiag;
#endif

    /// <summary>What just opened and through which engine — the first thing to check on playback problems.</summary>
    private void LogVideoTracks(string when)
    {
        if (_engine is MpvEngine mpv)
        {
            PlayerLog($"{when} engine=mpv source={_source.SiteName} ep={_episodeIndex + 1} " +
                      string.Join(" ", mpv.Stats().Select(r => $"{r.Label}={r.Value}")));
        }
        else if (_engine is not null)
        {
            PlayerLog($"{when} engine={_engine.Name} source={_source.SiteName} ep={_episodeIndex + 1}");
        }
    }

    private static void PlayerLog(string line)
    {
        try
        {
            File.AppendAllText(Path.Combine(Core.Configuration.AppPaths.Root, "player.log"),
                $"[{DateTimeOffset.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }

    private void UpdateTimeText(TimeSpan position)
    {
        var shown = _showRemaining && _duration > TimeSpan.Zero ? $"-{TimeText.Format(_duration - position)}" : TimeText.Format(position);
        TimeLabel.Text = $"{shown} / {TimeText.Format(_duration)}";
    }

    /// <summary>Two minutes before the end, the next episode's opening segments go into the disk cache.</summary>
    private void PrefetchNext(TimeSpan position)
    {
        if (_nextPrefetched || !HasNext || _duration <= TimeSpan.FromMinutes(3) || _duration - position > TimeSpan.FromMinutes(2)) return;
        _nextPrefetched = true;
        var next = Line.Episodes[_episodeIndex + 1].Url;
        if (next.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)) _ = _proxy.PrefetchAsync(next, segments: 4);
    }

    /// <summary>Local titles TMDB does not know have no artwork: a frame from the file stands in on 继续观看.</summary>
    private void GrabThumbnail(TimeSpan position, EngineState state)
    {
        if (_thumbTaken || state != EngineState.Playing || position < TimeSpan.FromSeconds(8)
            || _request.Item.BackdropPath is not null || Mpv is not { } mpv || !LocalPlayback.IsLocal(_source.Candidate))
        {
            return;
        }

        _thumbTaken = true;
        var folder = Path.Combine(Core.Configuration.AppPaths.Root, "cache", "thumbs");
        Directory.CreateDirectory(folder);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(_request.Item.MediaKey)));
        _thumbPath = Path.Combine(folder, hash[..16] + ".jpg");
        mpv.ScreenshotToFile(_thumbPath);
    }

    private void SaveProgress(bool flush)
    {
        if (flush) ReportBiliProgress(force: true);
        // B站 clips are companions to a title, not something to continue: they stay out of history.
        if (_engine is null || _duration <= TimeSpan.Zero || _request.Bili is not null
            || _request.Item.LocalKey?.StartsWith("bili", StringComparison.Ordinal) == true) return;
        var position = _engine.Position;
        if (position < TimeSpan.FromSeconds(5)) return;

        var item = _request.Item;
        _progress.Save(new WatchProgress
        {
            MediaKey = item.MediaKey,
            TmdbId = item.TmdbId,
            Kind = item.Kind,
            Title = item.Title,
            PosterPath = item.PosterPath,
            BackdropPath = item.BackdropPath ?? _thumbPath,
            Season = _request.Season,
            EpisodeIndex = _episodeIndex,
            EpisodeLabel = item.Kind == MediaKind.Tv ? EpisodeLabel(_episodeIndex) : null,
            EpisodeCount = Line.Episodes.Count,
            PositionMs = (long)position.TotalMilliseconds,
            DurationMs = (long)_duration.TotalMilliseconds,
            SourceKey = _source.Candidate.Identity,
            LocalPath = LocalPlayback.IsLocalPath(Line.Episodes[_episodeIndex].Url) ? Line.Episodes[_episodeIndex].Url : null,
        }, flush);
        _lastSave = DateTimeOffset.Now;
    }

    private void UpdatePlaybackState()
    {
        if (_engine is null) return;
        var state = _engine.State;
        PlayPauseGlyph.Glyph = state == EngineState.Playing ? "\uE769" : "\uE768";
        KeepAwake(state is EngineState.Playing or EngineState.Buffering or EngineState.Opening);
        UpdateTaskbar();
        _media?.SetPlaying(state switch
        {
            EngineState.Playing or EngineState.Buffering => true,
            EngineState.Paused or EngineState.Ended => false,
            _ => null,
        });
        SyncDanmaku();

        if (state == EngineState.Playing && _awaitingFirstFrame)
        {
            _awaitingFirstFrame = false;
            HideLoading();
        }

        BufferRing.Visibility = !_awaitingFirstFrame && state is EngineState.Buffering
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (state == EngineState.Paused && !_awaitingFirstFrame)
        {
            ShowChrome(pin: true);
            _pausedTimer.Stop();
            _pausedTimer.Start();
        }
        else if (state == EngineState.Playing)
        {
            _pausedTimer.Stop();
            HidePausedInfo();
            ShowChrome();
        }
    }

    // ----- Loading cover, paused info, next-up ----------------------------------------------------------

    private void ShowLoading(string text)
    {
        LoadingText.Text = text;
        LoadingCover.Visibility = Visibility.Visible;
        LoadingCover.Opacity = 1;
    }

    private async void HideLoading()
    {
        await Motion.SlideFadeAsync(LoadingCover, 0, 0, 0, 0, null, 0, TimeSpan.FromMilliseconds(450));
        if (!_awaitingFirstFrame) LoadingCover.Visibility = Visibility.Collapsed;
    }

    private void ShowPausedInfo()
    {
        if (_engine?.State != EngineState.Paused || _sideOpen) return;
        _pausedInfoShown = true;
        _ = Motion.SlideFadeAsync(PausedInfo, 0, 0, 14, 0, 0, 1, TimeSpan.FromMilliseconds(600));
    }

    private void HidePausedInfo()
    {
        _pausedTimer.Stop();
        if (!_pausedInfoShown) return;
        _pausedInfoShown = false;
        Motion.FadeTo(PausedInfo, 0, TimeSpan.FromMilliseconds(220));
    }

    private void UpdateNextCard(TimeSpan position)
    {
        var remaining = _duration - position;
        var eligible = HasNext && _request.Item.Kind == MediaKind.Tv && _duration > TimeSpan.FromMinutes(3)
                       && remaining <= NextUpLead && remaining > TimeSpan.Zero && !_nextCancelled;

        if (eligible && !_nextShown)
        {
            _nextShown = true;
            NextTitle.Text = EpisodeHeadline(_episodeIndex + 1);
            ImageEx.SetUrl(NextStill, StillUrl(_episodeIndex + 1) ?? _tmdb.ImageUrl(_request.Item.BackdropPath, "w300"));
            NextCard.Visibility = Visibility.Visible;
            _ = Motion.SlideFadeAsync(NextCard, 24, 0, 0, 0, 0, 1, TimeSpan.FromMilliseconds(420));
        }
        else if (!eligible && _nextShown && remaining > NextUpLead)
        {
            HideNextCard(resetCancel: false); // user seeked back
        }

        if (_nextShown)
        {
            NextCountdown.Text = _settings.Current.Playback.AutoNext
                ? $"下一集 · {Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds))} 秒后播放"
                : "下一集";
        }
    }

    private async void HideNextCard(bool resetCancel)
    {
        if (resetCancel) _nextCancelled = false;
        if (!_nextShown) return;
        _nextShown = false;
        await Motion.SlideFadeAsync(NextCard, 0, 24, 0, 0, null, 0, TimeSpan.FromMilliseconds(200), decelerate: false);
        if (!_nextShown) NextCard.Visibility = Visibility.Collapsed;
    }

    private void OnNextNowClick(object sender, RoutedEventArgs e) => PlayEpisode(_episodeIndex + 1);

    private void OnNextCancelClick(object sender, RoutedEventArgs e)
    {
        _nextCancelled = true;
        HideNextCard(resetCancel: false);
    }

    // ----- Transport ------------------------------------------------------------------------------------

    private void TogglePlay()
    {
        if (_engine is null) return;
        var playing = _engine.State is EngineState.Playing or EngineState.Buffering;
        if (playing) _engine.Pause(); else _engine.Play();
        FlashCenter(playing ? "" : "");
    }

    private void SeekTo(TimeSpan target)
    {
        if (_engine is null) return;
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        if (_duration > TimeSpan.Zero && target > _duration) target = _duration - TimeSpan.FromSeconds(1);
        _engine.Seek(target);
        UpdateTimeText(target);
        SyncDanmaku(target);
    }

    private void SeekBy(double seconds)
    {
        if (_engine is null) return;
        SeekTo(_engine.Position + TimeSpan.FromSeconds(seconds));
        Ripple(seconds);
        ShowChrome();
    }

    /// <summary>Side ripple like a console player: left for back, right for forward, with the step.</summary>
    private void Ripple(double seconds)
    {
        var (host, text) = seconds < 0 ? (RippleBack, RippleBackText) : (RippleForward, RippleForwardText);
        text.Text = $"{Math.Abs(seconds):0} 秒";
        host.Opacity = 1;
        Motion.FadeTo(host, 0, TimeSpan.FromMilliseconds(650));
    }

    private double CurrentSpeed => _speed;

    private void SetSpeed(double speed)
    {
        _speed = speed;
        if (_engine is not null) _engine.Rate = speed;
        SyncDanmaku();
        SpeedText.Text = $"{speed:0.0#}x";
        foreach (var item in SpeedMenu.Items.OfType<RadioMenuFlyoutItem>()) item.IsChecked = (double)item.Tag == speed;
    }

    private void StepSpeed(int direction)
    {
        var index = Array.IndexOf(Speeds, Speeds.MinBy(s => Math.Abs(s - CurrentSpeed)));
        var next = Speeds[Math.Clamp(index + direction, 0, Speeds.Length - 1)];
        SetSpeed(next);
        ShowHud("", (next - Speeds[0]) / (Speeds[^1] - Speeds[0]), $"{next:0.0#}x");
    }

    private void SetVolume(double volume)
    {
        _volume = Math.Clamp(volume, 0, MaxVolume);
        if (_engine is not null)
        {
            _engine.Volume = _volume;
            _engine.Muted = false;
        }

        VolumeBar.Value = _volume * 100;
        UpdateVolumeGlyph();
        ShowHud(VolumeGlyph.Glyph, _volume / MaxVolume, $"{Math.Round(_volume * 100)}");
    }

    private void UpdateVolumeGlyph() =>
        VolumeGlyph.Glyph = _engine?.Muted == true || _volume == 0 ? "" : _volume < 0.5 ? "" : "";

    private void ShowHud(string glyph, double fraction, string text)
    {
        HudGlyph.Glyph = glyph;
        HudFill.Width = 140 * Math.Clamp(fraction, 0, 1);
        HudText.Text = text;
        Hud.Opacity = 1;
        _hudTimer.Stop();
        _hudTimer.Start();
    }

    private void ToggleFullScreen()
    {
        if (App.MainWindow.IsCompactOverlay) TogglePip();
        var full = !App.MainWindow.IsFullScreen;
        App.MainWindow.SetFullScreen(full);
        FullScreenGlyph.Glyph = full ? "" : "";
    }

    private void TogglePip()
    {
        var compact = !App.MainWindow.IsCompactOverlay;
        if (compact && App.MainWindow.IsFullScreen)
        {
            App.MainWindow.SetFullScreen(false);
            FullScreenGlyph.Glyph = "";
        }

        App.MainWindow.SetCompactOverlay(compact);
        PipGlyph.Glyph = compact ? "" : "";

        // The mini window keeps only transport essentials.
        var full = compact ? Visibility.Collapsed : Visibility.Visible;
        NowPlayingText.Visibility = full;
        VolumeBar.Visibility = full;
        EpisodesButton.Visibility = compact || Line.Episodes.Count <= 1 ? Visibility.Collapsed : Visibility.Visible;
        SpeedButton.Visibility = full;
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => TogglePlay();

    private void OnBack10Click(object sender, RoutedEventArgs e) => SeekBy(-10);

    private void OnForward10Click(object sender, RoutedEventArgs e) => SeekBy(10);

    private void OnNextClick(object sender, RoutedEventArgs e) => PlayEpisode(_episodeIndex + 1);

    private void OnFullScreenClick(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void OnPipClick(object sender, RoutedEventArgs e) => TogglePip();

    private void OnTimeClick(object sender, RoutedEventArgs e)
    {
        _showRemaining = !_showRemaining;
        UpdateTimeText(_engine?.Position ?? TimeSpan.Zero);
    }

    private void OnMuteClick(object sender, RoutedEventArgs e)
    {
        if (_engine is null) return;
        _engine.Muted = !_engine.Muted;
        UpdateVolumeGlyph();
        ShowHud(VolumeGlyph.Glyph, _engine.Muted ? 0 : _volume / MaxVolume, _engine.Muted ? "静音" : $"{Math.Round(_volume * 100)}");
    }

    private void OnVolumeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _volume = e.NewValue / 100;
        if (_engine is not null)
        {
            _engine.Volume = _volume;
            if (_volume > 0) _engine.Muted = false;
        }

        UpdateVolumeGlyph();
    }

    private void OnRetryClick(object sender, RoutedEventArgs e)
    {
        _failedSources.Clear();
        _ = OpenAsync(resume: true);
    }

    // ----- Surface & keyboard ---------------------------------------------------------------------------

    private void OnSurfaceTapped(object sender, TappedRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
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
        var ctrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                    & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        switch (e.Key)
        {
            // mpv-only extras (ignored on the system engine).
            case VirtualKey.S when ctrl:
                TakeScreenshot(withSubtitles: shift);
                break;
            case VirtualKey.L when ctrl:
                if (Mpv is { } loop) ShowHud("", 1, loop.CycleAbLoop());
                break;
            case VirtualKey.I:
                ToggleInfoPanel();
                break;
            case VirtualKey.V when shift:
                CycleTrack("sub");
                break;
            case VirtualKey.V:
                ToggleSubtitles();
                break;
            case VirtualKey.A:
                CycleTrack("audio");
                break;
            case VirtualKey.Z:
                NudgeSubtitle(-0.1);
                break;
            case VirtualKey.X:
                NudgeSubtitle(0.1);
                break;
            case (VirtualKey)188: // ,
                Mpv?.FrameStep(back: true);
                break;
            case (VirtualKey)190: // .
                Mpv?.FrameStep(back: false);
                break;
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
                break;
            case VirtualKey.Down:
                SetVolume(_volume - 0.05);
                break;
            case VirtualKey.F:
                ToggleFullScreen();
                break;
            case VirtualKey.P:
                TogglePip();
                break;
            case VirtualKey.M:
                OnMuteClick(this, new RoutedEventArgs());
                break;
            case VirtualKey.N:
                PlayEpisode(_episodeIndex + 1);
                break;
            case VirtualKey.E when Line.Episodes.Count > 1:
                OpenSidePanel(EpisodesTab);
                break;
            case VirtualKey.S:
                OpenSidePanel(SourcesTab);
                break;
            case VirtualKey.D:
                ToggleDanmaku();
                break;
            case VirtualKey.B:
                OpenSidePanel(DanmakuTab);
                break;
            case VirtualKey.C when _request.Bili is not null:
                OpenSidePanel(CommentsTab);
                break;
            case (VirtualKey)219: // [
                StepSpeed(-1);
                break;
            case (VirtualKey)221: // ]
                StepSpeed(1);
                break;
            case VirtualKey.Escape:
                if (_sideOpen) CloseSidePanel();
                else if (App.MainWindow.IsCompactOverlay) TogglePip();
                else if (App.MainWindow.IsFullScreen) ToggleFullScreen();
                else App.MainWindow.GoBack();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    // ----- Chrome, cursor and caption buttons hide together ---------------------------------------------

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

    private void OnPointerExitedRoot(object sender, PointerRoutedEventArgs e) => SetCursorHidden(false);

    private void ShowChrome(bool pin = false)
    {
        SetCursorHidden(false);
        if (!_chromeVisible)
        {
            _chromeVisible = true;
            Chrome.IsHitTestVisible = true;
            Motion.FadeTo(Chrome, 1, TimeSpan.FromMilliseconds(160));
            App.MainWindow.SetTitleBarVisible(true);
        }

        // Bottom-pinned danmaku step up above the controls instead of sitting among them.
        Danmaku.BottomInset = BottomBar.ActualHeight + 32;
        _chromeTimer.Stop();
        if (!pin) _chromeTimer.Start();
    }

    private void HideChromeIfIdle()
    {
        var playing = _engine?.State == EngineState.Playing;
        if (!playing || _sideOpen || SeekBar.IsDragging || SpeedMenu.IsOpen || _nextShown) return;

        _chromeVisible = false;
        Chrome.IsHitTestVisible = false;
        Danmaku.BottomInset = 24;
        Motion.FadeTo(Chrome, 0, TimeSpan.FromMilliseconds(420));
        App.MainWindow.SetTitleBarVisible(false);
        SetCursorHidden(true);
    }

    private void SetCursorHidden(bool hidden)
    {
        if (hidden == _cursorHidden) return;
        _cursorHidden = hidden;
        // ShowCursor keeps a display counter; step it until it crosses zero in the wanted direction.
        if (hidden)
        {
            while (ShowCursor(false) >= 0) { }
        }
        else
        {
            while (ShowCursor(true) < 0) { }
        }
    }

    [DllImport("user32.dll")]
    private static extern int ShowCursor(bool show);

    /// <summary>Big glyph that pops and fades in the centre (pure visual element: composition is safe here).</summary>
    private void FlashCenter(string glyph)
    {
        CenterGlyph.Glyph = glyph;
        var visual = ElementCompositionPreview.GetElementVisual(CenterGlyphHost);
        visual.CenterPoint = new Vector3(48, 48, 0);
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
        HidePausedInfo();
        SideLayer.Visibility = Visibility.Visible;
        PanelTabs.SelectedItem = PanelTabs.Items.Contains(tab) ? tab : PanelTabs.Items.FirstOrDefault();
        _ = Motion.SlideFadeAsync(SidePanel, 40, 0, 0, 0, 0, 1, TimeSpan.FromMilliseconds(320));
        ShowChrome(pin: true);
    }

    private void CloseSidePanel()
    {
        _sideOpen = false;
        SideLayer.Visibility = Visibility.Collapsed;
        ShowChrome();
        Focus(FocusState.Programmatic); // the focused panel control just disappeared
    }

    private void OnSideDismiss(object sender, RoutedEventArgs e) => CloseSidePanel();

    private void OnSideDismissTapped(object sender, TappedRoutedEventArgs e) => CloseSidePanel();

    private void OnPanelTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        EpisodesView.Visibility = sender.SelectedItem == EpisodesTab ? Visibility.Visible : Visibility.Collapsed;
        SourcesView.Visibility = sender.SelectedItem == SourcesTab ? Visibility.Visible : Visibility.Collapsed;
        DanmakuView.Visibility = sender.SelectedItem == DanmakuTab ? Visibility.Visible : Visibility.Collapsed;
        PictureView.Visibility = sender.SelectedItem == PictureTab ? Visibility.Visible : Visibility.Collapsed;
        AudioView.Visibility = sender.SelectedItem == AudioTab ? Visibility.Visible : Visibility.Collapsed;
        SubtitleView.Visibility = sender.SelectedItem == SubtitleTab ? Visibility.Visible : Visibility.Collapsed;
        CommentsView.Visibility = sender.SelectedItem == CommentsTab ? Visibility.Visible : Visibility.Collapsed;
        if (sender.SelectedItem == CommentsTab) _ = LoadBiliCommentsAsync();
        if (sender.SelectedItem == DanmakuTab) OnDanmakuTabShown();
        if (sender.SelectedItem == PictureTab || sender.SelectedItem == AudioTab || sender.SelectedItem == SubtitleTab)
        {
            OnTuningTabShown();
            SyncPictureUi();
            SyncAudioUi();
            SyncSubtitleUi();
        }
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

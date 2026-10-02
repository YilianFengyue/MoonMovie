using System.Collections.ObjectModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MoonMovie.Core.Bilibili;
using MoonMovie.Core.Danmaku;
using MoonMovie.Core.Models;
using MoonMovie.Core.Sources;
using MoonMovie.Imaging;
using MoonMovie.Services;
using MoonMovie.ViewModels;

namespace MoonMovie.Views;

/// <summary>
/// Player: B站 videos and B站正版 episodes — DASH streams through mpv, B站's own danmaku, the 评论 tab, B站画质,
/// progress into the account's history, and (正版) B站's OP/ED marks.
/// </summary>
public sealed partial class PlayerPage
{
    private readonly BiliClient _bili = App.Services.GetRequiredService<BiliClient>();
    private readonly ObservableCollection<BiliCommentViewModel> _biliComments = [];
    private BiliStream? _biliStream;
    private bool _biliCommentsLoaded;
    private int _biliCommentPage;
    private int? _biliQualityCap;
    private DateTimeOffset _biliLastReport;
    private IReadOnlyList<BiliClip> _pgcClips = [];
    private bool _danmakuFromBili;
    private long _commentsAid;

    private BiliVideoDetail? Bili => _request.Bili;

    private bool IsBiliVip => App.Services.GetRequiredService<BiliAccountService>().Account?.IsVip == true;

    private string CurrentUrl => Line.Episodes[_episodeIndex].Url;

    /// <summary>The B站 part or 正版 episode playing now: its aid (comments) and cid (danmaku).</summary>
    private (long Aid, long Cid)? BiliIds
    {
        get
        {
            if (BiliPlayback.IsBiliUrl(CurrentUrl) && Bili is { } bili) return (bili.Video.Aid, BiliPlayback.Parse(CurrentUrl).Cid);
            if (BiliPgcSource.IsPgcUrl(CurrentUrl))
            {
                var ids = BiliPgcSource.Parse(CurrentUrl);
                return (ids.Aid, ids.Cid);
            }

            return null;
        }
    }

    /// <summary>
    /// B站 videos swap 片源 (there is only B站) for 评论. B站正版 keeps 片源 and adds 评论 for the episode
    /// playing; other sources have no 评论. Runs at every open: episodes and source switches change it.
    /// </summary>
    private void ConfigureBiliPanels()
    {
        var aid = BiliIds?.Aid ?? 0;
        if (aid != _commentsAid)
        {
            _commentsAid = aid;
            _biliComments.Clear();
            _biliCommentsLoaded = false;
            _biliCommentPage = 0;
            if (aid != 0 && _sideOpen && PanelTabs.SelectedItem == CommentsTab) _ = LoadBiliCommentsAsync();
        }

        CommentsRepeater.ItemsSource = _biliComments;
        if (aid == 0)
        {
            if (PanelTabs.SelectedItem == CommentsTab) PanelTabs.SelectedItem = PanelTabs.Items.FirstOrDefault(i => i != CommentsTab);
            PanelTabs.Items.Remove(CommentsTab);
            return;
        }

        if (!PanelTabs.Items.Contains(CommentsTab)) PanelTabs.Items.Add(CommentsTab);
        if (Bili is not { } bili)
        {
            // 正版: the episode, not an uploader.
            var episode = Line.Episodes[_episodeIndex];
            BiliTitle.Text = _request.Item.Kind == MediaKind.Tv
                ? $"{_request.Item.Title} · {EpisodeLabel(_episodeIndex)}  {episode.Name}"
                : _request.Item.Title;
            BiliAuthor.Text = _source.Candidate.Category is { } category ? $"{_source.SiteName} · {category}" : _source.SiteName;
            BiliAuthorFaceHost.Visibility = Visibility.Collapsed;
            BiliStats.Visibility = Visibility.Collapsed;
            BiliDescription.Visibility = Visibility.Collapsed;
            return;
        }

        PanelTabs.Items.Remove(SourcesTab);
        var video = bili.Video;
        BiliTitle.Text = video.Title;
        BiliAuthor.Text = $"{video.Author} · {BiliText.Age(video.Published)}";
        ImageEx.SetUrl(BiliAuthorFace, BiliClient.Thumb(bili.AuthorFace, 56, 56));
        BiliStats.Text = string.Join("  ·  ", new[]
        {
            $"{BiliText.Count(video.Plays)} 播放",
            $"{BiliText.Count(video.DanmakuCount)} 弹幕",
            bili.Likes > 0 ? $"{BiliText.Count(bili.Likes)} 点赞" : null,
            bili.Coins > 0 ? $"{BiliText.Count(bili.Coins)} 投币" : null,
            bili.Favorites > 0 ? $"{BiliText.Count(bili.Favorites)} 收藏" : null,
        }.Where(s => s is not null));
        BiliDescription.Text = video.Description ?? string.Empty;
        BiliDescription.Visibility = string.IsNullOrWhiteSpace(video.Description) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Resolves the part's DASH streams (they expire, so only now) and opens them with B站's headers.</summary>
    private async Task OpenBiliAsync(string url, int version, bool resume)
    {
        if (Mpv is not { } mpv)
        {
            OnMediaFailed("B站视频需要 mpv 内核（设置 → 播放内核）");
            return;
        }

        var (bvid, cid) = BiliPlayback.Parse(url);
        BiliStream stream;
        try
        {
            stream = await _bili.StreamAsync(bvid, cid, [12, 7, 13], _biliQualityCap); // HEVC, then AVC, then AV1
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or BiliException or System.Text.Json.JsonException)
        {
            if (version == _openVersion) OnMediaFailed(ex is BiliException b ? b.Message : "连接 B站失败");
            return;
        }

        if (version != _openVersion || _engine is null) return;
        _biliStream = stream;
        SyncBiliQualities();

        // Signed in: continue where B站 history says (also from the phone), unless near the end.
        if (resume && _pendingSeek == TimeSpan.Zero && stream.ResumeSeconds is > 15 and var at
            && (Bili?.Pages.ElementAtOrDefault(_episodeIndex)?.DurationSeconds is not { } length || at < length - 20))
        {
            _pendingSeek = TimeSpan.FromSeconds(at);
        }

        _awaitingFirstFrame = true;
        var speed = _speed;
        await mpv.OpenStreamAsync(stream.VideoUrl, stream.AudioUrl, _pendingSeek,
            new Dictionary<string, string> { ["Referer"] = BiliClient.Referer }, BiliClient.UserAgent);
        _engine.Rate = speed;
        UpdateSystemMediaInfo();
    }

    /// <summary>
    /// A B站正版 episode. Without 大会员 a 会员 episode is only a preview: another reachable source takes over for
    /// it (with a note), or, with none, the preview plays and says so.
    /// </summary>
    private async Task OpenPgcAsync(string url, int version)
    {
        if (Mpv is not { } mpv)
        {
            OnMediaFailed("B站正版需要 mpv 内核（设置 → 播放内核）");
            return;
        }

        var (ep, cid, _, _, _) = BiliPgcSource.Parse(url);
        BiliStream stream;
        try
        {
            stream = await _bili.PgcStreamAsync(ep, cid, [12, 7, 13], _biliQualityCap);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or BiliException or System.Text.Json.JsonException)
        {
            if (version == _openVersion) OnMediaFailed(ex is BiliException b ? b.Message : "连接 B站失败");
            return;
        }

        if (version != _openVersion || _engine is null) return;
        if (stream.IsPreview)
        {
            var other = _request.Sources.Items.FirstOrDefault(i =>
                !i.IsOfficial && i.State is ProbeOutcome.Ok or ProbeOutcome.Slow
                && !_failedSources.Contains(i.Candidate.Identity)
                && i.Candidate.PrimaryLine.Episodes.Count > _episodeIndex);
            if (other is not null)
            {
                ShowToast($"这一集是 B站大会员内容，已换用 {other.SiteName}", duration: TimeSpan.FromSeconds(6));
                SwitchSource(other, _pendingSeek);
                return;
            }

            ShowToast("正在试看：这一集需要 B站大会员", duration: TimeSpan.FromSeconds(8));
        }

        _biliStream = stream;
        _pgcClips = stream.Clips ?? [];
        SyncBiliQualities();
        UpdateMarks();

        _awaitingFirstFrame = true;
        var speed = _speed;
        await mpv.OpenStreamAsync(stream.VideoUrl, stream.AudioUrl, _pendingSeek,
            new Dictionary<string, string> { ["Referer"] = BiliClient.Referer }, BiliClient.UserAgent);
        _engine.Rate = speed;
        UpdateSystemMediaInfo();
    }

    /// <summary>The part's own danmaku pool, filtered by the same density and block rules as LogVar's.</summary>
    private async void LoadBiliDanmaku()
    {
        if (_danmakuEpisode == _episodeIndex && _danmakuFromBili) return;
        if (BiliIds is not { } ids) return;
        _danmakuEpisode = _episodeIndex;
        _danmakuFromBili = true;
        _danmakuCts?.Cancel();
        var cts = _danmakuCts = new CancellationTokenSource();
        Danmaku.Clear();
        Danmaku.Offset = 0;
        UpdateOffsetText();
        SetDanmakuStatus("正在加载 B站弹幕…", string.Empty, busy: true);

        try
        {
            var raw = await Task.Run(() => _bili.DanmakuAsync(ids.Cid, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            var match = new DanmakuMatch(0, "哔哩哔哩", Line.Episodes.Count > 1 ? Line.Episodes[_episodeIndex].Name : Bili?.Video.Title ?? "");
            ApplyTrack(new DanmakuTrack(match, _danmaku.Filter(raw), raw), announce: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            if (!cts.IsCancellationRequested) SetDanmakuStatus("B站弹幕加载失败", ex.Message, busy: false);
        }
    }

    /// <summary>Hot comments: the first page when the tab first opens, more on request (signed in).</summary>
    private async Task LoadBiliCommentsAsync(bool more = false)
    {
        if ((_biliCommentsLoaded && !more) || BiliIds is not { } ids) return;
        _biliCommentsLoaded = true;
        CommentsRing.IsActive = true;
        CommentsRing.Visibility = Visibility.Visible;
        CommentsNote.Visibility = Visibility.Collapsed;
        CommentsMoreButton.Visibility = Visibility.Collapsed;
        BiliLinkButton.Visibility = Visibility.Collapsed;
        try
        {
            var page = more ? _biliCommentPage + 1 : 1;
            var comments = await _bili.CommentsAsync(ids.Aid, page);
            if (ids.Aid != _commentsAid) return; // the episode changed meanwhile
            _biliCommentPage = page;
            if (!more) _biliComments.Clear();
            var aid = ids.Aid;
            foreach (var c in comments.Items) _biliComments.Add(new BiliCommentViewModel(c, (root, pn) => _bili.RepliesAsync(aid, root, pn)));
            CommentsMoreButton.Visibility = comments.HasMore ? Visibility.Visible : Visibility.Collapsed;
            CommentsCount.Text = comments.Total > 0 ? $"共 {BiliText.Count(comments.Total)} 条" : string.Empty;
            if (comments.Items.Count == 0)
            {
                CommentsNote.Text = "还没有评论";
                CommentsNote.Visibility = Visibility.Visible;
            }
            else if (comments.LimitedForGuests)
            {
                CommentsNote.Text = "未关联 B站账号时只能看到少量热门评论。";
                CommentsNote.Visibility = Visibility.Visible;
                BiliLinkButton.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or BiliException or System.Text.Json.JsonException)
        {
            _biliCommentsLoaded = false; // try again next time the tab opens
            CommentsNote.Text = "评论加载失败，稍后再试";
            CommentsNote.Visibility = Visibility.Visible;
        }
        finally
        {
            CommentsRing.IsActive = false;
            CommentsRing.Visibility = Visibility.Collapsed; // no empty band above the list
        }
    }

    private async void OnMoreComments(object sender, RoutedEventArgs e) => await LoadBiliCommentsAsync(more: true);

    /// <summary>Link the account from here, then reload comments and reopen at the better quality.</summary>
    private async void OnLinkBiliFromPlayer(object sender, RoutedEventArgs e)
    {
        var position = _engine?.Position ?? TimeSpan.Zero;
        if (!await Controls.BiliLoginDialog.ShowAsync(XamlRoot)) return;
        _biliCommentsLoaded = false;
        _ = LoadBiliCommentsAsync();
        _ = OpenAsync(resume: false, position);
        ShowToast("已关联 B站账号，正在切换到更高画质");
    }

    /// <summary>B站画质 chips: the playable ones switch (keeping the position), locked ones explain why.</summary>
    private void SyncBiliQualities()
    {
        if (_biliStream is not { Qualities.Count: > 0 } stream)
        {
            BiliQualitySection.Visibility = Visibility.Collapsed;
            return;
        }

        BiliQualitySection.Visibility = Visibility.Visible;
        BiliQualityChips.Children.Clear();
        foreach (var quality in stream.Qualities)
        {
            var label = quality.Label;
            var chip = Controls.Chips.Create(label, () =>
            {
                if (quality.Id == _biliStream?.Quality) return;
                _biliQualityCap = quality.Id;
                var position = _engine?.Position ?? TimeSpan.Zero;
                _ = OpenAsync(resume: false, position);
                ShowToast($"正在切换到 {label}");
            });
            Controls.Chips.Set(chip, quality.Id == stream.Quality);
            if (!quality.Available)
            {
                chip.IsEnabled = false;
                chip.Opacity = 0.4; // visibly locked, not just unclickable
                ToolTipService.SetToolTip(chip, _bili.IsSignedIn ? "需要大会员" : "关联 B站账号后可用");
            }

            BiliQualityChips.Children.Add(chip);
        }

        BiliQualityNote.Text = stream.Qualities.Any(q => !q.Available) ? (_bili.IsSignedIn ? "部分画质需要大会员" : "关联 B站账号可解锁更高画质") : string.Empty;
    }

    /// <summary>Signed in: the position goes into B站 history every 15 s and when leaving the part.</summary>
    private void ReportBiliProgress(bool force)
    {
        if (BiliIds is not { } ids || !_bili.IsSignedIn || _engine is null) return;
        if (!force && (_engine.State != Playback.Engines.EngineState.Playing || DateTimeOffset.Now - _biliLastReport < TimeSpan.FromSeconds(15))) return;
        var seconds = (int)_engine.Position.TotalSeconds;
        if (seconds < 5) return;
        _biliLastReport = DateTimeOffset.Now;
        var url = CurrentUrl;
        _ = Task.Run(async () =>
        {
            try
            {
                if (BiliPgcSource.IsPgcUrl(url))
                {
                    var (ep, cid, aid, season, type) = BiliPgcSource.Parse(url);
                    await _bili.ReportPgcProgressAsync(aid, cid, ep, season, type, seconds);
                }
                else
                {
                    await _bili.ReportProgressAsync(ids.Aid, ids.Cid, seconds);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or BiliException or System.Text.Json.JsonException)
            {
            }
        });
    }

    private void OnOpenBiliInBrowser(object sender, RoutedEventArgs e)
    {
        string web;
        if (Bili is { } bili) web = bili.Video.WebUrl + (_episodeIndex > 0 ? $"?p={_episodeIndex + 1}" : string.Empty);
        else if (BiliPgcSource.IsPgcUrl(CurrentUrl)) web = BiliPgcSource.WebUrl(BiliPgcSource.Parse(CurrentUrl).EpId);
        else return;
        _engine?.Pause();
        _ = Windows.System.Launcher.LaunchUriAsync(new Uri(web));
    }

    /// <summary>Info panel row: what B站 gave this session.</summary>
    private (string Label, string Value)? BiliStreamRow => _biliStream is { } s
        ? ("B站画质", $"{s.QualityLabel} · {s.Codec}" + (s.Quality <= 32 ? "（登录后可看 1080P）" : string.Empty))
        : null;
}

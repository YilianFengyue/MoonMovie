using System.Collections.ObjectModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MoonMovie.Core.Bilibili;
using MoonMovie.Core.Danmaku;
using MoonMovie.Imaging;
using MoonMovie.Services;
using MoonMovie.ViewModels;

namespace MoonMovie.Views;

/// <summary>Player: B站 videos — DASH streams through mpv, B站's own danmaku, and the 评论 tab.</summary>
public sealed partial class PlayerPage
{
    private readonly BiliClient _bili = App.Services.GetRequiredService<BiliClient>();
    private readonly ObservableCollection<BiliCommentViewModel> _biliComments = [];
    private BiliStream? _biliStream;
    private bool _biliCommentsLoaded;
    private int _biliCommentPage;
    private int? _biliQualityCap;
    private DateTimeOffset _biliLastReport;

    private BiliVideoDetail? Bili => _request.Bili;

    /// <summary>B站 videos swap 片源 (there is only B站) for 评论; everything else drops 评论.</summary>
    private void ConfigureBiliPanels()
    {
        if (Bili is not { } bili)
        {
            PanelTabs.Items.Remove(CommentsTab);
            return;
        }

        PanelTabs.Items.Remove(SourcesTab);
        CommentsRepeater.ItemsSource = _biliComments;
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

    /// <summary>The part's own danmaku pool, filtered by the same density and block rules as LogVar's.</summary>
    private async void LoadBiliDanmaku()
    {
        if (_danmakuEpisode == _episodeIndex) return;
        _danmakuEpisode = _episodeIndex;
        _danmakuCts?.Cancel();
        var cts = _danmakuCts = new CancellationTokenSource();
        Danmaku.Clear();
        Danmaku.Offset = 0;
        UpdateOffsetText();
        SetDanmakuStatus("正在加载 B站弹幕…", string.Empty, busy: true);

        try
        {
            var (_, cid) = BiliPlayback.Parse(Line.Episodes[_episodeIndex].Url);
            var raw = await Task.Run(() => _bili.DanmakuAsync(cid, cts.Token), cts.Token);
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
        if ((_biliCommentsLoaded && !more) || Bili is not { } bili) return;
        _biliCommentsLoaded = true;
        CommentsRing.IsActive = true;
        CommentsRing.Visibility = Visibility.Visible;
        CommentsNote.Visibility = Visibility.Collapsed;
        CommentsMoreButton.Visibility = Visibility.Collapsed;
        BiliLinkButton.Visibility = Visibility.Collapsed;
        try
        {
            var page = more ? _biliCommentPage + 1 : 1;
            var comments = await _bili.CommentsAsync(bili.Video.Aid, page);
            _biliCommentPage = page;
            if (!more) _biliComments.Clear();
            var aid = bili.Video.Aid;
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
        if (Bili is not { } bili || !_bili.IsSignedIn || _engine is null) return;
        if (!force && (_engine.State != Playback.Engines.EngineState.Playing || DateTimeOffset.Now - _biliLastReport < TimeSpan.FromSeconds(15))) return;
        var seconds = (int)_engine.Position.TotalSeconds;
        if (seconds < 5) return;
        _biliLastReport = DateTimeOffset.Now;
        var (_, cid) = BiliPlayback.Parse(Line.Episodes[_episodeIndex].Url);
        _ = Task.Run(async () =>
        {
            try
            {
                await _bili.ReportProgressAsync(bili.Video.Aid, cid, seconds);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or BiliException or System.Text.Json.JsonException)
            {
            }
        });
    }

    private void OnOpenBiliInBrowser(object sender, RoutedEventArgs e)
    {
        if (Bili is not { } bili) return;
        _engine?.Pause();
        var part = _episodeIndex > 0 ? $"?p={_episodeIndex + 1}" : string.Empty;
        _ = Windows.System.Launcher.LaunchUriAsync(new Uri(bili.Video.WebUrl + part));
    }

    /// <summary>Info panel row: what B站 gave this session.</summary>
    private (string Label, string Value)? BiliStreamRow => _biliStream is { } s
        ? ("B站画质", $"{s.QualityLabel} · {s.Codec}" + (s.Quality <= 32 ? "（登录后可看 1080P）" : string.Empty))
        : null;
}

using System.Collections.ObjectModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
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
    private async Task OpenBiliAsync(string url, int version)
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
            stream = await _bili.StreamAsync(bvid, cid, [12, 7, 13]); // HEVC, then AVC, then AV1
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or BiliException or System.Text.Json.JsonException)
        {
            if (version == _openVersion) OnMediaFailed(ex is BiliException b ? b.Message : "连接 B站失败");
            return;
        }

        if (version != _openVersion || _engine is null) return;
        _biliStream = stream;
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

    /// <summary>Hot comments, loaded the first time the tab opens.</summary>
    private async Task LoadBiliCommentsAsync()
    {
        if (_biliCommentsLoaded || Bili is not { } bili) return;
        _biliCommentsLoaded = true;
        CommentsRing.IsActive = true;
        CommentsRing.Visibility = Visibility.Visible;
        CommentsNote.Visibility = Visibility.Collapsed;
        try
        {
            var comments = await _bili.CommentsAsync(bili.Video.Aid);
            _biliComments.Clear();
            foreach (var c in comments.Items) _biliComments.Add(new BiliCommentViewModel(c));
            CommentsCount.Text = comments.Total > 0 ? $"共 {BiliText.Count(comments.Total)} 条" : string.Empty;
            if (comments.Items.Count == 0)
            {
                CommentsNote.Text = "还没有评论";
                CommentsNote.Visibility = Visibility.Visible;
            }
            else if (comments.LimitedForGuests)
            {
                CommentsNote.Text = "未登录时 B站只显示少量热门评论；登录 B站账号的功能即将推出。";
                CommentsNote.Visibility = Visibility.Visible;
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

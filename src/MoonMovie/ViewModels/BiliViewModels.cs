using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using MoonMovie.Core.Bilibili;
using MoonMovie.Services;

namespace MoonMovie.ViewModels;

/// <summary>A B站 video card: cover with duration, title, UP主 · plays · age.</summary>
public sealed partial class BiliVideoViewModel(BiliVideo video)
{
    public BiliVideo Video { get; } = video;

    public string Title => Video.Title;

    public string? CoverUrl => BiliClient.Thumb(Video.Cover, 480, 270);

    public string DurationText => BiliText.Duration(Video.DurationSeconds);

    /// <summary>"影视飓风 · 141.9万播放 · 3天前".</summary>
    public string Meta => $"{Video.Author} · {BiliText.Count(Video.Plays)}播放 · {BiliText.Age(Video.Published)}";

    public string AutomationName => $"{Title}，{Meta}";

    [RelayCommand]
    private void Play() => _ = BiliPlayback.PlayAsync(Video);

    [RelayCommand]
    private void OpenInBrowser() => _ = Windows.System.Launcher.LaunchUriAsync(new Uri(Video.WebUrl));
}

/// <summary>
/// A comment in the player's 评论 tab. Its thread starts with B站's few preview replies; 「查看全部」 loads the
/// replies page by page in place, 「收起」 folds back to the preview.
/// </summary>
public sealed partial class BiliCommentViewModel : ObservableObject
{
    private readonly BiliComment comment;
    private readonly Func<long, int, Task<BiliComments>>? _loadReplies;
    private int _page;
    private bool _hasMore;

    public BiliCommentViewModel(BiliComment comment, Func<long, int, Task<BiliComments>>? loadReplies = null)
    {
        this.comment = comment;
        _loadReplies = loadReplies;
        ShowPreviews();
    }

    public ObservableCollection<BiliCommentViewModel> Replies { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThreadActionText), nameof(ThreadActionVisibility), nameof(CollapseVisibility))]
    public partial bool Expanded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThreadActionVisibility), nameof(LoadingVisibility))]
    public partial bool Loading { get; set; }

    public Visibility ThreadVisibility => comment.ReplyCount > 0 && _loadReplies is not null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>「查看全部 238 条回复」 first, then 「加载更多回复」 while pages remain.</summary>
    public string ThreadActionText => Expanded ? "加载更多回复" : $"查看全部 {comment.ReplyCount} 条回复";

    public Visibility ThreadActionVisibility =>
        !Loading && (Expanded ? _hasMore : comment.ReplyCount > Replies.Count) ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CollapseVisibility => Expanded ? Visibility.Visible : Visibility.Collapsed;

    public Visibility LoadingVisibility => Loading ? Visibility.Visible : Visibility.Collapsed;

    [RelayCommand]
    private async Task MoreRepliesAsync()
    {
        if (_loadReplies is null || Loading) return;
        Loading = true;
        try
        {
            var page = Expanded ? _page + 1 : 1;
            var replies = await _loadReplies(comment.Id, page);
            if (page == 1) Replies.Clear();
            foreach (var r in replies.Items) Replies.Add(new BiliCommentViewModel(r));
            _page = page;
            _hasMore = replies.HasMore;
            Expanded = true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or BiliException or System.Text.Json.JsonException)
        {
            // Keep what is shown; the button stays for another try.
        }
        finally
        {
            Loading = false;
        }
    }

    [RelayCommand]
    private void CollapseReplies()
    {
        ShowPreviews();
        Expanded = false;
        OnPropertyChanged(nameof(ThreadActionVisibility));
    }

    private void ShowPreviews()
    {
        Replies.Clear();
        foreach (var r in comment.Previews ?? []) Replies.Add(new BiliCommentViewModel(r));
        _page = 0;
        _hasMore = false;
    }

    public string User => comment.User;

    public string? AvatarUrl => BiliClient.Thumb(comment.Avatar, 72, 72);

    public string Initial => comment.User.Length > 0 ? comment.User[..1] : "?";

    public string Text => comment.Text;

    /// <summary>"3天前 · 广东".</summary>
    public string Meta => comment.Location is { Length: > 0 } where ? $"{BiliText.Age(comment.Time)} · {where}" : BiliText.Age(comment.Time);

    public string Likes => comment.Likes > 0 ? BiliText.Count(comment.Likes) : string.Empty;

    public Visibility PinnedVisibility => comment.Pinned ? Visibility.Visible : Visibility.Collapsed;

}

/// <summary>Numbers and dates the way B站 writes them.</summary>
public static class BiliText
{
    public static string Count(long n) => n switch
    {
        >= 100_000_000 => $"{n / 100_000_000.0:0.#}亿",
        >= 10_000 => $"{n / 10_000.0:0.#}万",
        _ => n.ToString(),
    };

    public static string Duration(int seconds) =>
        seconds >= 3600 ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}" : $"{seconds / 60:00}:{seconds % 60:00}";

    public static string Age(DateTimeOffset at)
    {
        var age = DateTimeOffset.Now - at;
        if (age.TotalMinutes < 60) return $"{Math.Max(1, (int)age.TotalMinutes)}分钟前";
        if (age.TotalHours < 24) return $"{(int)age.TotalHours}小时前";
        if (age.TotalDays < 30) return $"{(int)age.TotalDays}天前";
        var local = at.ToLocalTime();
        return local.Year == DateTime.Now.Year ? $"{local.Month}月{local.Day}日" : $"{local.Year}年{local.Month}月";
    }
}

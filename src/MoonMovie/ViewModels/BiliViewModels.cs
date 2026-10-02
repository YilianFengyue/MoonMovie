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

/// <summary>A comment in the player's 评论 tab.</summary>
public sealed class BiliCommentViewModel(BiliComment comment)
{
    public string User => comment.User;

    public string? AvatarUrl => BiliClient.Thumb(comment.Avatar, 72, 72);

    public string Initial => comment.User.Length > 0 ? comment.User[..1] : "?";

    public string Text => comment.Text;

    /// <summary>"3天前 · 广东".</summary>
    public string Meta => comment.Location is { Length: > 0 } where ? $"{BiliText.Age(comment.Time)} · {where}" : BiliText.Age(comment.Time);

    public string Likes => comment.Likes > 0 ? BiliText.Count(comment.Likes) : string.Empty;

    public Visibility PinnedVisibility => comment.Pinned ? Visibility.Visible : Visibility.Collapsed;

    public Visibility RepliesVisibility => comment.ReplyCount > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string RepliesText => $"{comment.ReplyCount} 条回复";
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

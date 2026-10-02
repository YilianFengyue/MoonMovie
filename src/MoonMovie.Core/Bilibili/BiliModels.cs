namespace MoonMovie.Core.Bilibili;

/// <summary>Search order, as B站 names it (totalrank / click / pubdate / dm / stow).</summary>
public enum BiliOrder
{
    Relevance,
    Plays,
    Newest,
    Danmaku,
    Favorites,
}

/// <summary>A video in search results or "related".</summary>
public sealed record BiliVideo(
    string Bvid,
    long Aid,
    string Title,
    string Author,
    long Mid,
    string Cover,
    int DurationSeconds,
    long Plays,
    long DanmakuCount,
    DateTimeOffset Published,
    string? Description)
{
    public string WebUrl => $"https://www.bilibili.com/video/{Bvid}";
}

/// <summary>One part ("P1", "P2") of a video.</summary>
public sealed record BiliPage(long Cid, int Number, string Title, int DurationSeconds);

public sealed record BiliVideoDetail(
    BiliVideo Video,
    IReadOnlyList<BiliPage> Pages,
    string? AuthorFace,
    long Likes,
    long Coins,
    long Favorites,
    long Replies);

/// <summary>Separate DASH video and audio URLs (they expire after a couple of hours: resolve at play time).</summary>
public sealed record BiliStream(string VideoUrl, string? AudioUrl, int Quality, string QualityLabel, string Codec, int Width, int Height);

public sealed record BiliComment(
    long Id,
    string User,
    string? Avatar,
    string Text,
    long Likes,
    DateTimeOffset Time,
    int ReplyCount,
    bool Pinned,
    string? Location);

/// <summary>Comments plus whether more exist than a guest may see.</summary>
public sealed record BiliComments(IReadOnlyList<BiliComment> Items, long Total, bool LimitedForGuests);

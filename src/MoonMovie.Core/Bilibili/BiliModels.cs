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

/// <summary>A quality B站 lists for a video (e.g. 80 "高清 1080P"); <see cref="Available"/> is false when the
/// account may not play it (needs login or 大会员).</summary>
public sealed record BiliQuality(int Id, string Label, bool Available);

/// <summary>Separate DASH video and audio URLs (they expire after a couple of hours: resolve at play time).</summary>
/// <param name="ResumeSeconds">Where this account stopped last time (B站's own history), when it was this part.</param>
public sealed record BiliStream(
    string VideoUrl,
    string? AudioUrl,
    int Quality,
    string QualityLabel,
    string Codec,
    int Width,
    int Height,
    IReadOnlyList<BiliQuality> Qualities,
    double? ResumeSeconds);

/// <summary>Cookies of a signed-in B站 account (from the QR login).</summary>
public sealed record BiliCredentials(string SessData, string BiliJct, string UserId, string UserIdMd5, string? RefreshToken);

public sealed record BiliAccount(long Mid, string Name, string? Face, bool IsVip, string? VipLabel, int Level);

public enum BiliLoginState
{
    Waiting,
    Scanned,
    Expired,
    Done,
}

public sealed record BiliLoginPoll(BiliLoginState State, BiliCredentials? Credentials, long Code = 0, string? Message = null);

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

/// <summary>A page of comments, whether more pages follow, and whether a guest is seeing only a sample.</summary>
public sealed record BiliComments(IReadOnlyList<BiliComment> Items, long Total, bool LimitedForGuests, bool HasMore);

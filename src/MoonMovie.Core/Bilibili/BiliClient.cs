using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MoonMovie.Core.Configuration;
using MoonMovie.Core.Danmaku;

namespace MoonMovie.Core.Bilibili;

public sealed class BiliException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// B站 web API as a guest: search, video details, DASH streams, danmaku and hot comments. Identity is a guest
/// <c>buvid3</c> cookie; endpoints that want it are signed with WBI (keys refreshed daily). Requests go out
/// directly (no system proxy) and slowly, like a browser tab would.
/// </summary>
public sealed partial class BiliClient
{
    public const string Referer = "https://www.bilibili.com/";
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Safari/537.36";

    private const string Api = "https://api.bilibili.com";

    private static readonly int[] MixinTable =
    [
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35, 27, 43, 5, 49, 33, 9, 42, 19, 29, 28, 14, 39,
        12, 38, 41, 13, 37, 48, 7, 16, 24, 55, 40, 61, 26, 17, 0, 1, 60, 51, 30, 4, 22, 25, 54, 21, 56, 59, 6, 63,
        57, 62, 11, 36, 20, 34, 44, 52,
    ];

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _identityGate = new(1, 1);
    private readonly SemaphoreSlim _requestGate = new(3, 3);
    private readonly string _statePath = Path.Combine(AppPaths.Data, "bili.json");
    private BiliState _state;

    /// <summary>The signed-in account's cookies (null: guest). Set by the app from its credential vault.</summary>
    public BiliCredentials? Credentials { get; set; }

    public bool IsSignedIn => Credentials is not null;

    public BiliClient(HttpClient http)
    {
        _http = http;
        _state = LoadState();
    }

    // ----- Public API -----------------------------------------------------------------------------------

    public async Task<IReadOnlyList<BiliVideo>> SearchAsync(string keyword, BiliOrder order = BiliOrder.Relevance, int page = 1,
        CancellationToken ct = default)
    {
        var data = await GetAsync("/x/web-interface/search/type", new()
        {
            ["search_type"] = "video",
            ["keyword"] = keyword,
            ["page"] = page.ToString(CultureInfo.InvariantCulture),
            ["order"] = order switch
            {
                BiliOrder.Plays => "click",
                BiliOrder.Newest => "pubdate",
                BiliOrder.Danmaku => "dm",
                BiliOrder.Favorites => "stow",
                _ => "totalrank",
            },
        }, signed: false, ct).ConfigureAwait(false);

        if (!data.TryGetProperty("result", out var results) || results.ValueKind != JsonValueKind.Array) return [];
        return results.EnumerateArray()
            .Where(r => Str(r, "type") is null or "video" && Str(r, "bvid") is { Length: > 0 })
            .Select(r => new BiliVideo(
                Str(r, "bvid")!,
                Long(r, "aid"),
                Plain(Str(r, "title")),
                Str(r, "author") ?? "",
                Long(r, "mid"),
                Absolute(Str(r, "pic")),
                ParseDuration(Str(r, "duration")),
                Long(r, "play"),
                Long(r, "video_review"),
                DateTimeOffset.FromUnixTimeSeconds(Long(r, "pubdate")),
                Str(r, "description")))
            .ToArray();
    }

    public async Task<BiliVideoDetail> DetailAsync(string bvid, CancellationToken ct = default)
    {
        var d = await GetAsync("/x/web-interface/view", new() { ["bvid"] = bvid }, signed: false, ct).ConfigureAwait(false);
        var owner = d.GetProperty("owner");
        var stat = d.GetProperty("stat");
        var video = new BiliVideo(
            Str(d, "bvid") ?? bvid,
            Long(d, "aid"),
            Plain(Str(d, "title")),
            Str(owner, "name") ?? "",
            Long(owner, "mid"),
            Absolute(Str(d, "pic")),
            (int)Long(d, "duration"),
            Long(stat, "view"),
            Long(stat, "danmaku"),
            DateTimeOffset.FromUnixTimeSeconds(Long(d, "pubdate")),
            Str(d, "desc"));
        var pages = d.TryGetProperty("pages", out var p) && p.ValueKind == JsonValueKind.Array
            ? p.EnumerateArray().Select(x => new BiliPage(Long(x, "cid"), (int)Long(x, "page"), Str(x, "part") ?? "", (int)Long(x, "duration"))).ToArray()
            : [new BiliPage(Long(d, "cid"), 1, video.Title, video.DurationSeconds)];
        return new BiliVideoDetail(video, pages, Absolute(Str(owner, "face")), Long(stat, "like"), Long(stat, "coin"),
            Long(stat, "favorite"), Long(stat, "reply"));
    }

    /// <summary>
    /// The best stream B站 offers this session (a guest gets up to 480P): highest quality first, then the codec
    /// order given (e.g. HEVC, AVC, AV1), on a regular CDN host rather than a peer-to-peer edge.
    /// </summary>
    public async Task<BiliStream> StreamAsync(string bvid, long cid, IReadOnlyList<int> codecPreference, int? maxQuality = null,
        CancellationToken ct = default)
    {
        var d = await GetAsync("/x/player/playurl", new()
        {
            ["bvid"] = bvid,
            ["cid"] = cid.ToString(CultureInfo.InvariantCulture),
            ["qn"] = "127",
            ["fnval"] = "4048",
            ["fnver"] = "0",
            ["fourk"] = "1",
        }, signed: false, ct).ConfigureAwait(false);

        var labels = new Dictionary<int, string>();
        if (d.TryGetProperty("accept_quality", out var qs) && d.TryGetProperty("accept_description", out var ds))
        {
            var q = qs.EnumerateArray().Select(x => x.GetInt32()).ToArray();
            var l = ds.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
            for (var i = 0; i < Math.Min(q.Length, l.Length); i++) labels[q[i]] = l[i];
        }

        // B站's own history: resume where this account stopped (only meaningful for the same part).
        double? resume = Long(d, "last_play_cid") == cid && Long(d, "last_play_time") > 0 ? Long(d, "last_play_time") / 1000.0 : null;

        if (d.TryGetProperty("dash", out var dash) && dash.ValueKind == JsonValueKind.Object)
        {
            var all = dash.GetProperty("video").EnumerateArray().ToArray();
            if (all.Length == 0) throw new BiliException(-404, "没有可播放的视频流");
            var playable = all.Select(v => (int)Long(v, "id")).ToHashSet();
            var qualities = labels.Select(p => new BiliQuality(p.Key, p.Value, playable.Contains(p.Key))).OrderByDescending(q => q.Id).ToArray();
            var videos = maxQuality is { } cap && all.Any(v => Long(v, "id") <= cap) ? all.Where(v => Long(v, "id") <= cap).ToArray() : all;
            var best = videos
                .OrderByDescending(v => Long(v, "id"))
                .ThenBy(v => Rank(codecPreference, (int)Long(v, "codecid")))
                .ThenByDescending(v => Long(v, "bandwidth"))
                .First();

            string? audio = null;
            if (dash.TryGetProperty("flac", out var flac) && flac.ValueKind == JsonValueKind.Object
                && flac.TryGetProperty("audio", out var flacAudio) && flacAudio.ValueKind == JsonValueKind.Object)
            {
                audio = PickUrl(flacAudio);
            }
            else if (dash.TryGetProperty("audio", out var audios) && audios.ValueKind == JsonValueKind.Array && audios.GetArrayLength() > 0)
            {
                audio = PickUrl(audios.EnumerateArray().MaxBy(a => Long(a, "bandwidth")));
            }

            var quality = (int)Long(best, "id");
            return new BiliStream(PickUrl(best), audio, quality, labels.GetValueOrDefault(quality, $"{Long(best, "height")}P"),
                (int)Long(best, "codecid") switch { 7 => "AVC", 12 => "HEVC", 13 => "AV1", var c => $"codec {c}" },
                (int)Long(best, "width"), (int)Long(best, "height"), qualities, resume);
        }

        // Very old uploads: one muxed FLV/MP4.
        if (d.TryGetProperty("durl", out var durl) && durl.GetArrayLength() > 0)
        {
            var quality = (int)Long(d, "quality");
            return new BiliStream(Str(durl[0], "url")!, null, quality, labels.GetValueOrDefault(quality, ""), "", 0, 0, [], resume);
        }

        throw new BiliException(-404, "没有可播放的视频流");
    }

    /// <summary>The danmaku of one part (the full XML pool, which B站 still serves without login).</summary>
    public async Task<IReadOnlyList<DanmakuComment>> DanmakuAsync(long cid, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://comment.bilibili.com/{cid}.xml");
        Decorate(request);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        var list = new List<DanmakuComment>();
        foreach (Match m in DanmakuLine().Matches(xml))
        {
            var p = m.Groups["p"].Value.Split(',');
            if (p.Length < 4 || !double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var time)) continue;
            var mode = p[1] switch
            {
                "1" or "2" or "3" => DanmakuMode.Scroll,
                "4" => DanmakuMode.Bottom,
                "5" => DanmakuMode.Top,
                _ => (DanmakuMode?)null, // 7 (advanced) and 8 (code) are scripted effects we do not render
            };
            if (mode is null || !uint.TryParse(p[3], out var color)) continue;
            var text = WebUtility.HtmlDecode(m.Groups["t"].Value);
            if (text.Length > 0) list.Add(new DanmakuComment(time, mode.Value, color & 0xFFFFFF, text));
        }

        list.Sort((a, b) => a.Time.CompareTo(b.Time));
        return list;
    }

    /// <summary>Hottest comments first, 20 a page (a guest sees only the top few).</summary>
    public async Task<BiliComments> CommentsAsync(long aid, int page = 1, CancellationToken ct = default)
    {
        var d = await GetAsync("/x/v2/reply", new()
        {
            ["type"] = "1",
            ["oid"] = aid.ToString(CultureInfo.InvariantCulture),
            ["sort"] = "1",
            ["pn"] = page.ToString(CultureInfo.InvariantCulture),
            ["ps"] = "20",
        }, signed: false, ct).ConfigureAwait(false);

        var items = new List<BiliComment>();
        if (page == 1 && d.TryGetProperty("upper", out var upper) && upper.ValueKind == JsonValueKind.Object
            && upper.TryGetProperty("top", out var top) && top.ValueKind == JsonValueKind.Object)
        {
            items.Add(Comment(top, pinned: true));
        }

        if (d.TryGetProperty("replies", out var replies) && replies.ValueKind == JsonValueKind.Array)
        {
            items.AddRange(replies.EnumerateArray().Select(r => Comment(r, pinned: false)).Where(c => items.All(i => i.Id != c.Id)));
        }

        var total = d.TryGetProperty("page", out var paging) ? Long(paging, "count") : items.Count;
        var hasMore = Credentials is not null && items.Count > 0 && page * 20 < total;
        return new BiliComments(items, total, LimitedForGuests: Credentials is null && total > items.Count, hasMore);
    }

    // ----- Account ----------------------------------------------------------------------------------------

    /// <summary>Starts a QR login: the URL to encode and the key to poll with (valid about three minutes).</summary>
    public async Task<(string Url, string Key)> CreateLoginQrAsync(CancellationToken ct = default)
    {
        var d = await GetAsync("https://passport.bilibili.com/x/passport-login/web/qrcode/generate", [], signed: false, ct).ConfigureAwait(false);
        return (Str(d, "url") ?? throw new BiliException(-1, "二维码生成失败"), Str(d, "qrcode_key") ?? "");
    }

    /// <summary>Waiting / scanned / expired, or done with the account's cookies (read from the redirect URL).</summary>
    public async Task<BiliLoginPoll> PollLoginAsync(string key, CancellationToken ct = default)
    {
        var d = await GetAsync("https://passport.bilibili.com/x/passport-login/web/qrcode/poll", new() { ["qrcode_key"] = key },
            signed: false, ct).ConfigureAwait(false);
        switch (Long(d, "code"))
        {
            case 86101: return new BiliLoginPoll(BiliLoginState.Waiting, null);
            case 86090: return new BiliLoginPoll(BiliLoginState.Scanned, null);
            case 86038: return new BiliLoginPoll(BiliLoginState.Expired, null);
            case 0:
                var query = new Uri(Str(d, "url") ?? throw new BiliException(-1, "登录结果不完整")).Query.TrimStart('?')
                    .Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Split('=', 2))
                    .Where(p => p.Length == 2)
                    .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
                if (!query.TryGetValue("SESSDATA", out var sess) || !query.TryGetValue("bili_jct", out var jct)) throw new BiliException(-1, "登录结果不完整");
                return new BiliLoginPoll(BiliLoginState.Done, new BiliCredentials(sess, jct,
                    query.GetValueOrDefault("DedeUserID", ""), query.GetValueOrDefault("DedeUserID__ckMd5", ""), Str(d, "refresh_token")));
            default:
                throw new BiliException((int)Long(d, "code"), Str(d, "message") ?? "登录失败");
        }
    }

    /// <summary>Who is signed in (null when the cookies expired or nobody is).</summary>
    public async Task<BiliAccount?> AccountAsync(CancellationToken ct = default)
    {
        if (Credentials is null) return null;
        JsonElement d;
        try
        {
            d = await GetAsync("/x/web-interface/nav", [], signed: false, ct).ConfigureAwait(false);
        }
        catch (BiliException ex) when (ex.Code == -101)
        {
            return null; // not logged in any more
        }

        if (!d.TryGetProperty("isLogin", out var login) || !login.GetBoolean()) return null;
        string? vipLabel = null;
        if (d.TryGetProperty("vip_label", out var label)) vipLabel = Str(label, "text");
        if (string.IsNullOrEmpty(vipLabel) && d.TryGetProperty("vip", out var vip) && vip.TryGetProperty("label", out var vl)) vipLabel = Str(vl, "text");
        var level = d.TryGetProperty("level_info", out var lv) ? (int)Long(lv, "current_level") : 0;
        return new BiliAccount(Long(d, "mid"), Str(d, "uname") ?? "", Absolute(Str(d, "face")), Long(d, "vipStatus") == 1,
            string.IsNullOrEmpty(vipLabel) ? null : vipLabel, level);
    }

    /// <summary>Ends the session on B站's side too (best effort), then forgets the cookies.</summary>
    public async Task LogoutAsync(CancellationToken ct = default)
    {
        if (Credentials is { } c)
        {
            try
            {
                await PostAsync("https://passport.bilibili.com/login/exit/v2", new() { ["biliCSRF"] = c.BiliJct }, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or BiliException or JsonException)
            {
            }
        }

        Credentials = null;
    }

    /// <summary>Writes the position into the account's B站 history (so the phone app continues from here).</summary>
    public async Task ReportProgressAsync(long aid, long cid, int seconds, CancellationToken ct = default)
    {
        if (Credentials is not { } c) return;
        await PostAsync("/x/v2/history/report", new()
        {
            ["aid"] = aid.ToString(CultureInfo.InvariantCulture),
            ["cid"] = cid.ToString(CultureInfo.InvariantCulture),
            ["progress"] = seconds.ToString(CultureInfo.InvariantCulture),
            ["platform"] = "web",
            ["csrf"] = c.BiliJct,
        }, ct).ConfigureAwait(false);
    }

    /// <summary>A sized JPEG of a B站 image (the image CDN resizes on request).</summary>
    public static string? Thumb(string? url, int width, int height) =>
        string.IsNullOrEmpty(url) ? null : $"{url}@{width}w_{height}h_1c.jpg";

    // ----- Requests -------------------------------------------------------------------------------------

    private async Task<JsonElement> GetAsync(string path, Dictionary<string, string> query, bool signed, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            await EnsureIdentityAsync(refresh: attempt > 0, ct).ConfigureAwait(false);
            var url = (path.StartsWith("https://", StringComparison.Ordinal) ? path : Api + path) + "?" + (signed ? Sign(query) : Encode(query));

            await _requestGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                Decorate(request);
                request.Headers.Add("Cookie", CookieHeader());
                using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                if ((int)response.StatusCode == 412 && attempt == 0) continue; // risk control: new identity, once
                response.EnsureSuccessStatusCode();

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
                var root = doc.RootElement;
                var code = root.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
                if (code is -352 or -412 && attempt == 0) continue;
                if (code != 0) throw new BiliException(code, root.TryGetProperty("message", out var msg) ? msg.GetString() ?? "" : $"错误 {code}");
                var payload = root.TryGetProperty("data", out var data) ? data : root.TryGetProperty("result", out var result) ? result : default;

                // A risk challenge arrives as code 0 with only a voucher: retry signed (WBI) with a fresh identity.
                if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("v_voucher", out _) && !payload.TryGetProperty("result", out _))
                {
                    if (attempt == 0)
                    {
                        signed = true;
                        path = path.Replace("/x/web-interface/search", "/x/web-interface/wbi/search").Replace("/x/player/playurl", "/x/player/wbi/playurl");
                        continue;
                    }

                    throw new BiliException(-352, "B站暂时拒绝了请求，稍后再试");
                }

                return payload.Clone();
            }
            finally
            {
                _requestGate.Release();
            }
        }
    }

    /// <summary>A form POST for actions that need the account (history report, logout); CSRF is in the form.</summary>
    private async Task PostAsync(string path, Dictionary<string, string> form, CancellationToken ct)
    {
        await EnsureIdentityAsync(refresh: false, ct).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, path.StartsWith("https://", StringComparison.Ordinal) ? path : Api + path)
        {
            Content = new FormUrlEncodedContent(form),
        };
        Decorate(request);
        request.Headers.Add("Cookie", CookieHeader());
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var code = doc.RootElement.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
        if (code != 0) throw new BiliException(code, Str(doc.RootElement, "message") ?? $"错误 {code}");
    }

    private string CookieHeader()
    {
        var cookie = $"buvid3={_state.Buvid3}; buvid4={_state.Buvid4}";
        return Credentials is { } c
            ? $"{cookie}; SESSDATA={Uri.EscapeDataString(c.SessData)}; bili_jct={c.BiliJct}; DedeUserID={c.UserId}; DedeUserID__ckMd5={c.UserIdMd5}"
            : cookie;
    }

    private static void Decorate(HttpRequestMessage request)
    {
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Referrer = new Uri(Referer);
    }

    /// <summary>Guest cookie and WBI mixin key, refreshed once a day (or after risk control).</summary>
    private async Task EnsureIdentityAsync(bool refresh, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (!refresh && _state.Buvid3.Length > 0 && _state.Mixin.Length > 0 && _state.Day == today) return;

        await _identityGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!refresh && _state.Buvid3.Length > 0 && _state.Mixin.Length > 0 && _state.Day == today) return;

            var state = _state with { };
            if (refresh || state.Buvid3.Length == 0)
            {
                using var spi = await Raw($"{Api}/x/frontend/finger/spi", ct).ConfigureAwait(false);
                var data = spi.RootElement.GetProperty("data");
                state = state with { Buvid3 = Str(data, "b_3") ?? "", Buvid4 = Str(data, "b_4") ?? "" };
            }

            using (var nav = await Raw($"{Api}/x/web-interface/nav", ct).ConfigureAwait(false))
            {
                var img = nav.RootElement.GetProperty("data").GetProperty("wbi_img");
                var raw = KeyOf(Str(img, "img_url")) + KeyOf(Str(img, "sub_url"));
                state = state with { Mixin = new string(MixinTable.Where(i => i < raw.Length).Select(i => raw[i]).Take(32).ToArray()), Day = today };
            }

            _state = state;
            SaveState();
        }
        finally
        {
            _identityGate.Release();
        }

        static string KeyOf(string? url) => url is null ? "" : Path.GetFileNameWithoutExtension(new Uri(url).AbsolutePath);
    }

    private async Task<JsonDocument> Raw(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        Decorate(request);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>WBI: sorted parameters plus a timestamp, MD5'd with the day's mixin key into w_rid.</summary>
    private string Sign(Dictionary<string, string> query)
    {
        var all = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in query) all[k] = WbiUnsafe().Replace(v, "");
        all["wts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var encoded = Encode(all);
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(encoded + _state.Mixin))).ToLowerInvariant();
        return encoded + "&w_rid=" + hash;
    }

    private static string Encode(IEnumerable<KeyValuePair<string, string>> query) =>
        string.Join("&", query.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

    // ----- Parsing helpers ------------------------------------------------------------------------------

    private static BiliComment Comment(JsonElement r, bool pinned)
    {
        var member = r.GetProperty("member");
        string? location = null;
        if (r.TryGetProperty("reply_control", out var control) && Str(control, "location") is { } loc)
        {
            location = loc.Replace("IP属地：", "");
        }

        return new BiliComment(
            Long(r, "rpid"),
            Str(member, "uname") ?? "",
            Absolute(Str(member, "avatar")),
            Str(r.GetProperty("content"), "message") ?? "",
            Long(r, "like"),
            DateTimeOffset.FromUnixTimeSeconds(Long(r, "ctime")),
            (int)Long(r, "rcount"),
            pinned,
            location);
    }

    /// <summary>A regular CDN URL beats a peer-to-peer edge (mcdn / PCDN hosts), which is often slow or blocked.</summary>
    private static string PickUrl(JsonElement stream)
    {
        var urls = new List<string>();
        foreach (var name in new[] { "baseUrl", "base_url" })
        {
            if (Str(stream, name) is { } u) urls.Add(u);
        }

        foreach (var name in new[] { "backupUrl", "backup_url" })
        {
            if (stream.TryGetProperty(name, out var backups) && backups.ValueKind == JsonValueKind.Array)
            {
                urls.AddRange(backups.EnumerateArray().Select(b => b.GetString()).Where(b => b is not null)!);
            }
        }

        return urls.OrderBy(u => u.Contains("mcdn", StringComparison.Ordinal) || u.Contains("szbdyd", StringComparison.Ordinal) ? 1 : 0)
                   .ThenBy(u => u.Contains("upos-", StringComparison.Ordinal) ? 0 : 1)
                   .FirstOrDefault() ?? throw new BiliException(-404, "没有可播放的地址");
    }

    private static int Rank(IReadOnlyList<int> preference, int codec)
    {
        for (var i = 0; i < preference.Count; i++)
        {
            if (preference[i] == codec) return i;
        }

        return preference.Count;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Long(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(v.GetString(), out var n) => n,
            _ => 0,
        };
    }

    /// <summary>Search titles carry &lt;em class="keyword"&gt; highlights and HTML entities.</summary>
    private static string Plain(string? html) => WebUtility.HtmlDecode(Tags().Replace(html ?? "", ""));

    private static string Absolute(string? url) =>
        string.IsNullOrEmpty(url) ? "" : url.StartsWith("//", StringComparison.Ordinal) ? "https:" + url : url.Replace("http://", "https://");

    /// <summary>"12:34" or "1:02:03".</summary>
    private static int ParseDuration(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var total = 0;
        foreach (var part in text.Split(':')) total = total * 60 + (int.TryParse(part, out var n) ? n : 0);
        return total;
    }

    // ----- State ------------------------------------------------------------------------------------------

    private sealed record BiliState(string Buvid3, string Buvid4, string Mixin, DateOnly Day);

    private BiliState LoadState()
    {
        try
        {
            if (File.Exists(_statePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(_statePath));
                var r = doc.RootElement;
                return new BiliState(Str(r, "buvid3") ?? "", Str(r, "buvid4") ?? "", Str(r, "mixin") ?? "",
                    DateOnly.TryParse(Str(r, "day"), CultureInfo.InvariantCulture, out var day) ? day : default);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
        }

        return new BiliState("", "", "", default);
    }

    private void SaveState()
    {
        try
        {
            File.WriteAllText(_statePath, JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["buvid3"] = _state.Buvid3,
                ["buvid4"] = _state.Buvid4,
                ["mixin"] = _state.Mixin,
                ["day"] = _state.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            }, BiliJsonContext.Default.DictionaryStringString));
        }
        catch (IOException)
        {
        }
    }

    [GeneratedRegex(@"<d p=""(?<p>[^""]+)"">(?<t>[^<]*)</d>")]
    private static partial Regex DanmakuLine();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex("[!'()*]")]
    private static partial Regex WbiUnsafe();
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class BiliJsonContext : System.Text.Json.Serialization.JsonSerializerContext;

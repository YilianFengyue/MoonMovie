using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MoonMovie.Core.Bilibili;

/// <summary>
/// Keeping a linked account signed in: B站 web cookies last about half a year and are renewed the way the web
/// client does it — B站 says when (<c>cookie/info</c>), a correspond path (RSA-OAEP of "refresh_{ms}") yields a
/// one-time refresh_csrf, the refresh_token buys new cookies, and confirming retires the old token.
/// </summary>
public sealed partial class BiliClient
{
    private const string Passport = "https://passport.bilibili.com/x/passport-login/web";

    private const string CorrespondKey = """
        -----BEGIN PUBLIC KEY-----
        MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDLgd2OAkcGVtoE3ThUREbio0Eg
        Uc/prcajMKXvkCKFCWhJYJcLkcM2DKKcSeFpD/j6Boy538YXnR6VhcuUJOhH2x71
        nzPjfdTcqMz7djHum0qSZA0AyCBDABUqCrfNgCiJ00Ra7GmRj+YCK1NJEuewlb40
        JNrRuoEUXpabUzGB8QIDAQAB
        -----END PUBLIC KEY-----
        """;

    /// <summary>
    /// Renews the cookies when B站 asks for it (or <paramref name="force"/>: they were just rejected). Returns the
    /// new credentials, now in use, or null when nothing changed. Needs the refresh_token kept from the QR login.
    /// </summary>
    public async Task<BiliCredentials?> RenewAsync(bool force = false, CancellationToken ct = default)
    {
        if (Credentials is not { RefreshToken: { Length: > 0 } oldToken } old) return null;

        long timestamp;
        try
        {
            var info = await GetAsync($"{Passport}/cookie/info", new() { ["csrf"] = old.BiliJct }, signed: false, ct).ConfigureAwait(false);
            if (!force && !(info.TryGetProperty("refresh", out var r) && r.ValueKind == JsonValueKind.True)) return null;
            timestamp = Long(info, "timestamp");
        }
        catch (BiliException ex) when (ex.Code == -101 && force)
        {
            timestamp = 0; // the session is gone: the refresh token may still renew it
        }

        if (timestamp <= 0) timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // The one-time refresh_csrf is rendered into the correspond page.
        using var rsa = RSA.Create();
        rsa.ImportFromPem(CorrespondKey);
        var path = Convert.ToHexString(rsa.Encrypt(Encoding.UTF8.GetBytes($"refresh_{timestamp}"), RSAEncryptionPadding.OaepSHA256)).ToLowerInvariant();
        string html;
        using (var request = new HttpRequestMessage(HttpMethod.Get, $"https://www.bilibili.com/correspond/1/{path}"))
        {
            Decorate(request);
            request.Headers.Add("Cookie", CookieHeader());
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }

        if (RefreshCsrf().Match(html) is not { Success: true } m) throw new BiliException(-1, "B站没有给出续期凭据");

        // New cookies arrive as Set-Cookie, the new refresh_token in the body.
        BiliCredentials renewed;
        using (var request = new HttpRequestMessage(HttpMethod.Post, $"{Passport}/cookie/refresh")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["csrf"] = old.BiliJct,
                ["refresh_csrf"] = m.Groups[1].Value.Trim(),
                ["source"] = "main_web",
                ["refresh_token"] = oldToken,
            }),
        })
        {
            Decorate(request);
            request.Headers.Add("Cookie", CookieHeader());
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var code = Long(doc.RootElement, "code");
            if (code != 0) throw new BiliException((int)code, Str(doc.RootElement, "message") ?? $"续期失败（{code}）");

            var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
            if (response.Headers.TryGetValues("Set-Cookie", out var lines))
            {
                foreach (var line in lines)
                {
                    var pair = line.Split(';', 2)[0].Split('=', 2);
                    if (pair.Length == 2 && pair[1].Length > 0) cookies[pair[0].Trim()] = Uri.UnescapeDataString(pair[1].Trim());
                }
            }

            if (!cookies.TryGetValue("SESSDATA", out var sess) || !cookies.TryGetValue("bili_jct", out var jct))
            {
                throw new BiliException(-1, "B站续期结果缺少 Cookie");
            }

            var data = doc.RootElement.TryGetProperty("data", out var d) ? d : default;
            renewed = new BiliCredentials(sess, jct, cookies.GetValueOrDefault("DedeUserID", old.UserId),
                cookies.GetValueOrDefault("DedeUserID__ckMd5", old.UserIdMd5), Str(data, "refresh_token") ?? oldToken);
        }

        Credentials = renewed;

        // Retire the old token with the new session (best effort: the new cookies already work).
        try
        {
            await PostAsync($"{Passport}/confirm/refresh", new()
            {
                ["csrf"] = renewed.BiliJct,
                ["refresh_token"] = oldToken,
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or BiliException or JsonException)
        {
        }

        return renewed;
    }

    [GeneratedRegex("""<div\s+id="1-name"\s*>([^<]+)</div>""")]
    private static partial Regex RefreshCsrf();
}

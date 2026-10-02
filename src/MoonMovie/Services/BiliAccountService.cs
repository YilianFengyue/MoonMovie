using System.Text.Json;
using System.Text.Json.Serialization;
using MoonMovie.Core.Bilibili;
using Windows.Security.Credentials;

namespace MoonMovie.Services;

/// <summary>
/// The linked B站 account. Its cookies live in Windows Credential Manager (per Windows user, encrypted by the
/// system), never in MoonMovie's data folder; they are handed to <see cref="BiliClient"/> at startup.
/// </summary>
public sealed class BiliAccountService(BiliClient client)
{
    private const string Resource = "MoonMovie · 哔哩哔哩";

    /// <summary>Who is signed in, once confirmed with B站 (null: nobody, or not checked yet).</summary>
    public BiliAccount? Account { get; private set; }

    public bool IsLinked => client.Credentials is not null;

    /// <summary>Raised on the calling thread after sign-in, sign-out or a refresh.</summary>
    public event Action? Changed;

    /// <summary>Startup: load saved cookies and confirm they still work (in the background).</summary>
    public void Restore()
    {
        try
        {
            var vault = new PasswordVault();
            var saved = vault.FindAllByResource(Resource).FirstOrDefault();
            if (saved is null) return;
            saved.RetrievePassword();
            client.Credentials = JsonSerializer.Deserialize(saved.Password, BiliAccountJsonContext.Default.BiliCredentials);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or JsonException or UnauthorizedAccessException)
        {
            return; // nothing saved (the vault throws when a resource has no entries)
        }

        _ = RefreshAsync();
    }

    /// <summary>Re-reads the account; cookies B站 no longer accepts are forgotten.</summary>
    public async Task RefreshAsync()
    {
        try
        {
            Account = await client.AccountAsync();
            if (Account is null && client.Credentials is not null)
            {
                Log("account check: B站 says not signed in, forgetting the cookies");
                client.Credentials = null;
                Forget();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or BiliException or JsonException)
        {
            // Offline: keep the cookies and try again later.
            Log($"account check failed: {Describe(ex)}");
        }

        Changed?.Invoke();
    }

    /// <summary>Keeps the cookies from a confirmed QR login; false when B站 then does not recognise them.</summary>
    public async Task<bool> CompleteLoginAsync(BiliCredentials credentials)
    {
        Log($"login confirmed (uid {(credentials.UserId.Length > 0 ? "present" : "missing")}, SESSDATA {credentials.SessData.Length} chars)");
        client.Credentials = credentials;
        try
        {
            Forget();
            new PasswordVault().Add(new PasswordCredential(Resource, credentials.UserId.Length > 0 ? credentials.UserId : "account",
                JsonSerializer.Serialize(credentials, BiliAccountJsonContext.Default.BiliCredentials)));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or ArgumentException)
        {
            // Could not persist: the session still works until MoonMovie closes.
            Log($"credential vault: {Describe(ex)}");
        }

        await RefreshAsync();
        if (Account is { } account) Log($"signed in as mid {account.Mid}, vip {account.IsVip}, Lv{account.Level}");
        return client.Credentials is not null;
    }

    public async Task LogoutAsync()
    {
        await client.LogoutAsync();
        Forget();
        Account = null;
        Changed?.Invoke();
    }

    /// <summary>B站 account diagnostics (never the cookies themselves) in %LOCALAPPDATA%\MoonMovieili.log.</summary>
    public static void Log(string line)
    {
        try
        {
            File.AppendAllText(Path.Combine(Core.Configuration.AppPaths.Root, "bili.log"), $"[{DateTime.Now:MM-dd HH:mm:ss.fff}] {line}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }

    public static string Describe(Exception ex) => ex is BiliException b ? $"B站 {b.Code}: {b.Message}" : $"{ex.GetType().Name}: {ex.Message}";

    private static void Forget()
    {
        try
        {
            var vault = new PasswordVault();
            foreach (var credential in vault.FindAllByResource(Resource)) vault.Remove(credential);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
        }
    }
}

[JsonSerializable(typeof(BiliCredentials))]
internal sealed partial class BiliAccountJsonContext : JsonSerializerContext;

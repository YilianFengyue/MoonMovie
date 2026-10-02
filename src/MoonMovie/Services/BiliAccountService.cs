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
                client.Credentials = null;
                Forget();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or BiliException or JsonException)
        {
            // Offline: keep the cookies and try again later.
        }

        Changed?.Invoke();
    }

    public async Task CompleteLoginAsync(BiliCredentials credentials)
    {
        client.Credentials = credentials;
        try
        {
            Forget();
            new PasswordVault().Add(new PasswordCredential(Resource, credentials.UserId.Length > 0 ? credentials.UserId : "account",
                JsonSerializer.Serialize(credentials, BiliAccountJsonContext.Default.BiliCredentials)));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            // Could not persist: the session still works until MoonMovie closes.
        }

        await RefreshAsync();
    }

    public async Task LogoutAsync()
    {
        await client.LogoutAsync();
        Forget();
        Account = null;
        Changed?.Invoke();
    }

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

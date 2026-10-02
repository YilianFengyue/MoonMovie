using System.Text.Json;
using System.Text.Json.Serialization;
using MoonMovie.Core.Configuration;

namespace MoonMovie.Core.Library;

/// <summary>The local profile: no account, just how MoonMovie greets you.</summary>
public sealed class Profile
{
    public string? Nickname { get; set; }

    /// <summary>A picture chosen from disk, copied into the data folder.</summary>
    public string? AvatarFile { get; set; }

    /// <summary>Show the linked B站 account's avatar instead.</summary>
    public bool UseBiliAvatar { get; set; } = true;

    public DateTimeOffset Since { get; set; } = DateTimeOffset.Now;
}

public sealed class ProfileStore
{
    private readonly string _path = Path.Combine(AppPaths.Data, "profile.json");

    public ProfileStore()
    {
        Current = Load();
        if (!File.Exists(_path)) Save(); // remembers "since"
    }

    public Profile Current { get; }

    public event EventHandler? Changed;

    /// <summary>The nickname, or the Windows user name until one is set.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Current.Nickname) ? Environment.UserName : Current.Nickname!;

    public void Save()
    {
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, ProfileJsonContext.Default.Profile));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (IOException)
        {
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Copies a picture into the data folder (the original may move or disappear).</summary>
    public void SetAvatar(string sourceFile)
    {
        // A new name each time: images are cached by path.
        var target = Path.Combine(AppPaths.Data, $"avatar-{DateTime.Now.Ticks}{Path.GetExtension(sourceFile).ToLowerInvariant()}");
        foreach (var old in Directory.EnumerateFiles(AppPaths.Data, "avatar-*")) File.Delete(old);
        File.Copy(sourceFile, target, overwrite: true);
        Current.AvatarFile = target;
        Current.UseBiliAvatar = false;
        Save();
    }

    private Profile Load()
    {
        try
        {
            return File.Exists(_path) ? JsonSerializer.Deserialize(File.ReadAllText(_path), ProfileJsonContext.Default.Profile) ?? new() : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new();
        }
    }
}

[JsonSerializable(typeof(Profile))]
internal sealed partial class ProfileJsonContext : JsonSerializerContext;

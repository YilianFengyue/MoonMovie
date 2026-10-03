namespace MoonMovie.Core.Configuration;

public static class AppPaths
{
    /// <summary>Entries MoonMovie keeps in a cache folder; only these are ever deleted from it.</summary>
    private const string CacheFolderName = "MoonMovieCache";

    private static readonly string[] CacheEntries =["api", "img", "segments", "danmaku", "sources", "shaders", "thumbs", "douban.json"];

    public static string Root { get; } = Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MoonMovie"));

    /// <summary>The caches' default home, next to the data.</summary>
    public static string DefaultCache => Path.Combine(Root, "cache");

    /// <summary>
    /// Where the caches live this session: the folder chosen in Settings (read at start; a change applies on the next
    /// start), or <see cref="DefaultCache"/> when none is chosen or the chosen drive is not there.
    /// </summary>
    public static string Cache { get; } = Ensure(ChosenCache() ?? DefaultCache);

    public static string ApiCache { get; } = Ensure(Path.Combine(Cache, "api"));

    public static string ImageCache { get; } = Ensure(Path.Combine(Cache, "img"));

    public static string Data { get; } = Ensure(Path.Combine(Root, "data"));

    private static string CacheSettingFile => Path.Combine(Root, "data", "cache-location.txt");

    private static string CacheCleanupFile => Path.Combine(Root, "data", "cache-cleanup.txt");

    /// <summary>The cache folder chosen in Settings (null: the default), whether or not it is in use yet.</summary>
    public static string? CacheLocationSetting
    {
        get
        {
            try
            {
                var text = File.Exists(CacheSettingFile) ? File.ReadAllText(CacheSettingFile).Trim() : null;
                return string.IsNullOrEmpty(text) ? null : text;
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    /// <summary>The cache folder that will be used from the next start on.</summary>
    public static string NextCache => CacheLocationSetting ?? DefaultCache;

    /// <summary>True when Settings chose a folder other than the one in use (it applies on the next start).</summary>
    public static bool CacheChangePending => !Same(NextCache, Cache);

    /// <summary>
    /// Chooses the cache folder for the next start (null: back to the default). The caches in use now are deleted
    /// then: they are only caches, and moving gigabytes of video segments would hold the start up.
    /// </summary>
    public static void SetCacheLocation(string? folder)
    {
        Directory.CreateDirectory(Data);
        // Always a folder of our own inside the chosen one: cleaning up later must never touch the user's files
        // (an "img" folder of their own when D:\ was chosen, say).
        if (folder is not null && !Path.GetFileName(folder.TrimEnd('\\', '/')).Equals(CacheFolderName, StringComparison.OrdinalIgnoreCase))
        {
            folder = Path.Combine(folder, CacheFolderName);
        }

        if (folder is null || Same(folder, DefaultCache))
        {
            File.Delete(CacheSettingFile);
        }
        else
        {
            File.WriteAllText(CacheSettingFile, folder);
        }

        var next = folder ?? DefaultCache;
        if (Same(next, Cache))
        {
            File.Delete(CacheCleanupFile);
        }
        else
        {
            File.WriteAllText(CacheCleanupFile, Cache);
        }
    }

    /// <summary>After a move: deletes MoonMovie's entries from the previous cache folder (nothing else in it).</summary>
    public static void CleanUpPreviousCache()
    {
        try
        {
            if (!File.Exists(CacheCleanupFile)) return;
            var previous = File.ReadAllText(CacheCleanupFile).Trim();
            File.Delete(CacheCleanupFile);
            if (previous.Length == 0 || Same(previous, Cache) || !Directory.Exists(previous)) return;

            foreach (var entry in CacheEntries)
            {
                var path = Path.Combine(previous, entry);
                try
                {
                    if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                    else if (File.Exists(path)) File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Something still holds it: it is only cache, leave it.
                }
            }

            if (!Directory.EnumerateFileSystemEntries(previous).Any()) Directory.Delete(previous);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string? ChosenCache()
    {
        var chosen = CacheLocationSetting;
        if (chosen is null) return null;
        try
        {
            Directory.CreateDirectory(chosen);
            return chosen;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null; // the drive is gone (an unplugged disk): fall back for this session
        }
    }

    private static bool Same(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}

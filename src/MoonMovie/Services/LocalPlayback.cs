using Microsoft.Extensions.DependencyInjection;
using MoonMovie.Core.Local;
using MoonMovie.Core.Models;
using MoonMovie.Core.Playback;
using MoonMovie.Core.Sources;
using MoonMovie.Core.Tmdb;
using MoonMovie.ViewModels;

namespace MoonMovie.Services;

/// <summary>
/// Local files as a playback source: they become a "本地文件" source candidate whose episode list lines up with
/// episode numbers, so the player, progress, danmaku and the detail page treat them like any resource site.
/// </summary>
public static class LocalPlayback
{
    public static readonly SourceSite Site = new("local", "本地文件", "");

    private static LocalLibrary Library => App.Services.GetRequiredService<LocalLibrary>();

    public static bool IsLocal(SourceCandidate candidate) => candidate.Site.Key == Site.Key;

    /// <summary>An absolute file or folder path (as opposed to a URL).</summary>
    public static bool IsLocalPath(string url) => url.Length > 3 && Path.IsPathFullyQualified(url);

    /// <summary>
    /// The title's files for a season as a source: index <c>n</c> is episode <c>n + 1</c>; missing episodes are
    /// empty entries (the player then falls over to an online source), unnumbered files follow at the end.
    /// </summary>
    public static SourceCandidate? Candidate(LocalTitle title, int? season)
    {
        var files = title.Episodes(season);
        if (files.Count == 0) return null;

        var episodes = new List<PlayEpisode>();
        if (title.IsSeries)
        {
            var numbered = files.Where(f => f.Episode is > 0).GroupBy(f => f.Episode!.Value).ToDictionary(g => g.Key, g => g.First());
            var last = numbered.Count > 0 ? numbered.Keys.Max() : 0;
            for (var n = 1; n <= last; n++)
            {
                episodes.Add(new PlayEpisode(n - 1, $"第 {n} 集", numbered.TryGetValue(n, out var f) ? f.Path : string.Empty));
            }

            foreach (var f in files.Where(f => f.Episode is null or 0))
            {
                episodes.Add(new PlayEpisode(episodes.Count, Path.GetFileNameWithoutExtension(f.FileName), f.Path));
            }
        }
        else
        {
            for (var i = 0; i < files.Count; i++)
            {
                var name = files.Count > 1 ? Path.GetFileNameWithoutExtension(files[i].FileName) : "正片";
                episodes.Add(new PlayEpisode(i, name, files[i].Path));
            }
        }

        var have = episodes.Count(e => e.Url.Length > 0);
        return new SourceCandidate(Site, $"{title.Key}|{season}", title.Title, title.Year, null,
            title.IsSeries ? $"本地 {have} 集" : null, [new PlayLine("本地", episodes)], 1000);
    }

    /// <summary>Plays a title from disk right away (no detail page), resuming where it was left.</summary>
    public static async Task PlayAsync(LocalTitle title, int? season = null, string? path = null)
    {
        var item = title.ToMediaItem();
        var progress = App.Services.GetRequiredService<WatchProgressStore>();
        var latest = progress.Latest(item.MediaKey);

        if (title.IsSeries)
        {
            season ??= path is not null ? title.Files.FirstOrDefault(f => Same(f.Path, path))?.Season ?? 1
                : latest?.Season ?? title.Seasons.FirstOrDefault(1);
        }
        else
        {
            season = null;
        }

        if (Candidate(title, season) is not { } candidate) return;
        var episodes = candidate.PrimaryLine.Episodes;

        int index;
        if (path is not null)
        {
            index = Math.Max(0, episodes.ToList().FindIndex(e => Same(e.Url, path)));
        }
        else if (latest is not null && latest.Season == season)
        {
            index = latest.HasNextEpisode ? latest.EpisodeIndex + 1 : latest.EpisodeIndex;
        }
        else
        {
            index = Math.Max(0, episodes.ToList().FindIndex(e => e.Url.Length > 0));
        }

        // Episode names and stills when TMDB knows the series.
        IReadOnlyList<EpisodeInfo> info = [];
        if (title.Match is { Kind: MediaKind.Tv } match && season is { } s)
        {
            try
            {
                info = await App.Services.GetRequiredService<TmdbClient>().SeasonAsync(match.TmdbId, s);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
            }
        }

        var panel = new SourcePanelViewModel(App.Services.GetRequiredService<SourceSearchService>());
        var source = panel.UseLocal(candidate);
        Navigator.OpenPlayback(new PlaybackRequest(item, panel, source, Math.Clamp(index, 0, episodes.Count - 1),
            season, info, title.IsAnimation));
    }

    /// <summary>"继续观看" for a title only known locally.</summary>
    public static void Resume(MediaItem item, int? season)
    {
        if (Library.Find(item.MediaKey) is { } title)
        {
            _ = PlayAsync(title, season);
            return;
        }

        // Opened straight from Explorer: the file path was remembered with the progress.
        var latest = App.Services.GetRequiredService<WatchProgressStore>().Latest(item.MediaKey);
        if (latest?.LocalPath is { } path && (File.Exists(path) || Directory.Exists(path)))
        {
            _ = PlayAsync(LocalLibrary.AdHoc(path), season, latest.HasNextEpisode ? null : path);
        }
    }

    /// <summary>
    /// Files and folders from the open dialog, drag and drop or the command line: videos and disc folders play
    /// (siblings become the episode list); other folders are added to the library.
    /// </summary>
    public static async Task OpenPathsAsync(IReadOnlyList<string> paths)
    {
        var video = paths.FirstOrDefault(p => (File.Exists(p) && LocalNameParser.IsVideo(p)) || IsDisc(p));
        if (video is not null)
        {
            var title = Library.FindByPath(video) ?? LocalLibrary.AdHoc(video);
            await PlayAsync(title, path: video);
            return;
        }

        var folders = paths.Where(p => Directory.Exists(p) && !IsDisc(p)).ToArray();
        if (folders.Length == 0) return;
        Navigator.OpenLibrary(LibraryTab.Local);
        foreach (var folder in folders) await Library.AddFolderAsync(folder);
    }

    public static bool IsDisc(string path) =>
        Directory.Exists(path)
        && (File.Exists(Path.Combine(path, "BDMV", "index.bdmv")) || Directory.Exists(Path.Combine(path, "VIDEO_TS")));

    /// <summary>
    /// What mpv should open for a local path: files as they are; a Blu-ray folder through its main stream (the
    /// largest .m2ts); a DVD folder through dvd:// with the device set by the caller.
    /// </summary>
    public static (string Target, string? DvdDevice) Resolve(string path)
    {
        if (!Directory.Exists(path)) return (path, null);
        var stream = Path.Combine(path, "BDMV", "STREAM");
        if (Directory.Exists(stream))
        {
            var main = new DirectoryInfo(stream).EnumerateFiles("*.m2ts").MaxBy(f => f.Length);
            if (main is not null) return (main.FullName, null);
        }

        return ("dvd://", Path.Combine(path, "VIDEO_TS"));
    }

    public static async Task PickAndOpenAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.VideosLibrary,
        };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        foreach (var ext in LocalNameParser.VideoExtensions) picker.FileTypeFilter.Add(ext);
        var file = await picker.PickSingleFileAsync();
        if (file is not null) await OpenPathsAsync([file.Path]);
    }

    public static async Task<string?> PickFolderAsync()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.VideosLibrary,
        };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

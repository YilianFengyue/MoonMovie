using Microsoft.UI.Xaml.Media.Animation;
using MoonMovie.Core.Models;
using MoonMovie.ViewModels;
using MoonMovie.Views;

namespace MoonMovie.Services;

public static class Navigator
{
    public static void OpenMedia(MediaItem item, int? season = null) =>
        App.MainWindow.Navigate(typeof(DetailPage), new DetailArgs(item, season), new DrillInNavigationTransitionInfo());

    /// <summary>Detail page that starts playing (resuming) as soon as a source is found.</summary>
    public static void Resume(MediaItem item, int? season) =>
        App.MainWindow.Navigate(typeof(DetailPage), new DetailArgs(item, season, AutoPlay: true),
            new DrillInNavigationTransitionInfo());

    public static void OpenBrowse(Core.Browse.BrowseSection section) =>
        App.MainWindow.Navigate(typeof(BrowsePage), section, new EntranceNavigationTransitionInfo());

    public static void OpenLibrary(LibraryTab tab = LibraryTab.Continue) =>
        App.MainWindow.Navigate(typeof(LibraryPage), tab, new EntranceNavigationTransitionInfo());

    public static void OpenPlayback(PlaybackRequest request)
    {
#if DEBUG
        // M0 engine lab: MOONMOVIE_DEBUG_ENGINE=mpv plays through libmpv instead of the current player.
        if (Environment.GetEnvironmentVariable("MOONMOVIE_DEBUG_ENGINE") == "mpv")
        {
            App.MainWindow.Navigate(typeof(MpvLabPage), request, new SuppressNavigationTransitionInfo());
            return;
        }
#endif
        App.MainWindow.Navigate(typeof(PlayerPage), request, new SuppressNavigationTransitionInfo());
    }
}

/// <summary>Everything the player needs; the source panel is shared so switching stays in sync with the detail page.</summary>
public sealed record PlaybackRequest(
    MediaItem Item,
    SourcePanelViewModel Sources,
    SourceItemViewModel Source,
    int EpisodeIndex,
    int? Season,
    IReadOnlyList<EpisodeInfo> Episodes);

public sealed record PlaceholderArgs(string Title, string Caption);

/// <param name="Season">Season to open on, e.g. from a search for "庆余年 第二季".</param>
/// <param name="AutoPlay">Start playback once a source is ready ("继续观看").</param>
public sealed record DetailArgs(MediaItem Item, int? Season = null, bool AutoPlay = false);

public enum LibraryTab
{
    Continue,
    History,
    Favorites,
}

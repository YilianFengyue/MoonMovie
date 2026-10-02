using Microsoft.UI.Xaml.Media.Animation;
using MoonMovie.Core.Models;
using MoonMovie.ViewModels;
using MoonMovie.Views;

namespace MoonMovie.Services;

public static class Navigator
{
    /// <summary>Detail page; titles only known from local files (no TMDB match) play straight away instead.</summary>
    public static void OpenMedia(MediaItem item, int? season = null)
    {
        if (item.IsLocalOnly)
        {
            LocalPlayback.Resume(item, season);
            return;
        }

        App.MainWindow.Navigate(typeof(DetailPage), new DetailArgs(item, season), new DrillInNavigationTransitionInfo());
    }

    /// <summary>Detail page that starts playing (resuming) as soon as a source is found.</summary>
    public static void Resume(MediaItem item, int? season)
    {
        if (item.IsLocalOnly)
        {
            LocalPlayback.Resume(item, season);
            return;
        }

        App.MainWindow.Navigate(typeof(DetailPage), new DetailArgs(item, season, AutoPlay: true),
            new DrillInNavigationTransitionInfo());
    }

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
        // Playing something else from the player (a dropped file) replaces it rather than stacking players.
        var replacing = App.MainWindow.CurrentPage is PlayerPage;
        App.MainWindow.Navigate(typeof(PlayerPage), request, new SuppressNavigationTransitionInfo());
        if (replacing) App.MainWindow.DropPreviousEntry();
    }
}

/// <summary>Everything the player needs; the source panel is shared so switching stays in sync with the detail page.</summary>
public sealed record PlaybackRequest(
    MediaItem Item,
    SourcePanelViewModel Sources,
    SourceItemViewModel Source,
    int EpisodeIndex,
    int? Season,
    IReadOnlyList<EpisodeInfo> Episodes,
    bool IsAnimation = false);

public sealed record PlaceholderArgs(string Title, string Caption);

/// <param name="Season">Season to open on, e.g. from a search for "庆余年 第二季".</param>
/// <param name="AutoPlay">Start playback once a source is ready ("继续观看").</param>
public sealed record DetailArgs(MediaItem Item, int? Season = null, bool AutoPlay = false);

public enum LibraryTab
{
    Continue,
    History,
    Favorites,
    Local,
}

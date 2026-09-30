using Microsoft.UI.Xaml.Media.Animation;
using MoonMovie.Core.Models;
using MoonMovie.ViewModels;
using MoonMovie.Views;

namespace MoonMovie.Services;

public static class Navigator
{
    public static void OpenMedia(MediaItem item, int? season = null) =>
        App.MainWindow.Navigate(typeof(DetailPage), new DetailArgs(item, season), new DrillInNavigationTransitionInfo());

    public static void OpenPlayback(PlaybackRequest request) =>
        App.MainWindow.Navigate(typeof(PlayerPage), request, new SuppressNavigationTransitionInfo());
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
public sealed record DetailArgs(MediaItem Item, int? Season = null);

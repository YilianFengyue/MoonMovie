using Microsoft.UI.Xaml.Media.Animation;
using MoonMovie.Core.Models;
using MoonMovie.ViewModels;
using MoonMovie.Views;

namespace MoonMovie.Services;

public static class Navigator
{
    public static void OpenMedia(MediaItem item) =>
        App.MainWindow.Navigate(typeof(DetailPage), item, new DrillInNavigationTransitionInfo());

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

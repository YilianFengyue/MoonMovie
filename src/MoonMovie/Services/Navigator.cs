using Microsoft.UI.Xaml.Media.Animation;
using MoonMovie.Core.Models;
using MoonMovie.Core.Sources;
using MoonMovie.Views;

namespace MoonMovie.Services;

public static class Navigator
{
    public static void OpenMedia(MediaItem item) =>
        App.MainWindow.Navigate(typeof(DetailPage), item, new DrillInNavigationTransitionInfo());

    /// <summary>Player lands in the next milestone; until then show what would be played.</summary>
    public static void OpenPlayback(MediaItem item, SourceCandidate source, PlayLine line, int episodeIndex, string? episodeLabel)
    {
        var episode = line.Episodes[episodeIndex];
        var title = episodeLabel is null ? item.Title : $"{item.Title}  {episodeLabel}";
        App.MainWindow.Navigate(typeof(PlaceholderPage),
            new PlaceholderArgs(title, $"播放器将在下一阶段接入 · {source.Site.Name} · {line.Name} · {episode.Name}"),
            new DrillInNavigationTransitionInfo());
    }
}

public sealed record PlaceholderArgs(string Title, string Caption);

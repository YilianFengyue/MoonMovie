using Microsoft.UI.Xaml.Media.Animation;
using MoonMovie.Core.Models;
using MoonMovie.Views;

namespace MoonMovie.Services;

public static class Navigator
{
    public static void OpenMedia(MediaItem item) =>
        App.MainWindow.Navigate(typeof(PlaceholderPage),
            new PlaceholderArgs(item.Title, "详情页将在下一阶段实现"),
            new DrillInNavigationTransitionInfo());
}

public sealed record PlaceholderArgs(string Title, string Caption);

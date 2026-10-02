using Microsoft.Extensions.DependencyInjection;
using MoonMovie.Core.Playback;
using System.Runtime.InteropServices;
using Windows.UI.StartScreen;

namespace MoonMovie.Services;

/// <summary>
/// 「继续观看」 in the taskbar / Start jump list: the five titles most recently left half-way. Needs package identity,
/// so it only does something in the installed (MSIX) build.
/// </summary>
public static class JumpListUpdater
{
    private static Timer? _debounce;

    public static void Start()
    {
        try
        {
            if (!JumpList.IsSupported()) return;
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or UnauthorizedAccessException)
        {
            return; // unpackaged: no identity, no jump list
        }

        var progress = App.Services.GetRequiredService<WatchProgressStore>();
        progress.Changed += (_, _) =>
        {
            _debounce?.Dispose();
            _debounce = new Timer(_ => App.MainWindow.DispatcherQueue.Enqueue(() => _ = UpdateAsync()), null,
                TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
        };
        _ = UpdateAsync();
    }

    private static async Task UpdateAsync()
    {
        try
        {
            var progress = App.Services.GetRequiredService<WatchProgressStore>();
            var list = await JumpList.LoadCurrentAsync();
            list.SystemGroupKind = JumpListSystemGroupKind.None;
            list.Items.Clear();
            foreach (var p in progress.Recent(40).Where(p => p.IsContinuable).Take(5))
            {
                var item = JumpListItem.CreateWithArguments($"{ActivationRouter.ResumeSwitch} {p.MediaKey}|{p.Season ?? 0}", p.Title);
                item.GroupName = "继续观看";
                item.Description = p.Kind == Core.Models.MediaKind.Tv
                    ? $"第 {(p.HasNextEpisode ? p.EpisodeIndex + 2 : p.EpisodeIndex + 1)} 集"
                    : "继续播放";
                list.Items.Add(item);
            }

            await list.SaveAsync();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
        }
    }
}

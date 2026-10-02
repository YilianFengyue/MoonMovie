using System.Runtime.InteropServices;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace MoonMovie.Views;

/// <summary>
/// Player ↔ Windows: the media flyout (volume / quick settings panel, lock screen) and hardware media keys through
/// the system media transport controls, and keeping the display awake while something is playing.
/// </summary>
public sealed partial class PlayerPage
{
    private bool _keepingAwake;

    private void InitSystemMedia()
    {
        if (_player is null) return;
        var commands = _player.CommandManager;
        commands.IsEnabled = true;
        commands.NextBehavior.EnablingRule = MediaCommandEnablingRule.Always;
        commands.PreviousBehavior.EnablingRule = MediaCommandEnablingRule.Always;
        commands.NextReceived += (_, args) =>
        {
            args.Handled = true;
            DispatcherQueue.TryEnqueue(() => PlayEpisode(_episodeIndex + 1));
        };
        commands.PreviousReceived += (_, args) =>
        {
            args.Handled = true;
            DispatcherQueue.TryEnqueue(() => PlayEpisode(_episodeIndex - 1));
        };
    }

    /// <summary>
    /// Title, episode and artwork for the system media flyout. With a MediaPlaybackItem as the source, the flyout
    /// reads the item's display properties (the DisplayUpdater is ignored).
    /// </summary>
    private void ApplyDisplayProperties(MediaPlaybackItem item)
    {
        try
        {
            var headline = EpisodeHeadline(_episodeIndex);
            var props = item.GetDisplayProperties();
            props.Type = MediaPlaybackType.Video;
            props.VideoProperties.Title = _request.Item.Title;
            props.VideoProperties.Subtitle = headline.Length > 0 ? headline : _request.Item.MetaLine;
            if (_tmdb.ImageUrl(_request.Item.BackdropPath ?? _request.Item.PosterPath, "w780") is { } art)
            {
                props.Thumbnail = RandomAccessStreamReference.CreateFromUri(new Uri(art));
            }

            item.ApplyDisplayProperties(props);
        }
        catch (COMException)
        {
            // The flyout is a nicety; never let it interfere with playback.
        }
    }

    /// <summary>No screen saver, display sleep or system sleep while playing; released on pause and on leave.</summary>
    private void KeepAwake(bool on)
    {
        if (on == _keepingAwake) return;
        _keepingAwake = on;
        SetThreadExecutionState(on ? EsContinuous | EsDisplayRequired | EsSystemRequired : EsContinuous);
    }

    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
}

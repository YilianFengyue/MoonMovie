using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Windows.Media;
using Windows.Storage.Streams;

namespace MoonMovie.Playback;

/// <summary>
/// The Windows media flyout (volume / quick settings, lock screen) and hardware media keys for a desktop
/// window, independent of the playback engine. Obtained per window through ISystemMediaTransportControlsInterop.
/// </summary>
public sealed unsafe class SystemMediaControls : IDisposable
{
    private static readonly Guid IidInterop = new("ddb0472d-c911-4a1f-86d9-dc3d71a95f5a");
    private static readonly Guid IidControls = new("99fa3ff4-1742-42a6-902e-087d41f965ec");

    private readonly SystemMediaTransportControls? _controls;
    private readonly DispatcherQueue _ui;

    public SystemMediaControls(nint hwnd, DispatcherQueue ui)
    {
        _ui = ui;
        _controls = ForWindow(hwnd);
        if (_controls is null) return;

        _controls.IsEnabled = true;
        _controls.IsPlayEnabled = true;
        _controls.IsPauseEnabled = true;
        _controls.IsNextEnabled = true;
        _controls.IsPreviousEnabled = true;
        _controls.ButtonPressed += OnButtonPressed;
    }

    public event Action? PlayPressed;

    public event Action? PausePressed;

    public event Action? NextPressed;

    public event Action? PreviousPressed;

    public void SetInfo(string title, string subtitle, string? artworkUrl)
    {
        if (_controls is null) return;
        try
        {
            var updater = _controls.DisplayUpdater;
            updater.Type = MediaPlaybackType.Video;
            updater.VideoProperties.Title = title;
            updater.VideoProperties.Subtitle = subtitle;
            updater.Thumbnail = artworkUrl is null ? null : RandomAccessStreamReference.CreateFromUri(new Uri(artworkUrl));
            updater.Update();
        }
        catch (COMException)
        {
        }
    }

    public void SetPlaying(bool? playing)
    {
        if (_controls is null) return;
        _controls.PlaybackStatus = playing switch
        {
            true => MediaPlaybackStatus.Playing,
            false => MediaPlaybackStatus.Paused,
            null => MediaPlaybackStatus.Changing,
        };
    }

    public void SetEpisodeButtons(bool previous, bool next)
    {
        if (_controls is null) return;
        _controls.IsPreviousEnabled = previous;
        _controls.IsNextEnabled = next;
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        var handler = args.Button switch
        {
            SystemMediaTransportControlsButton.Play => PlayPressed,
            SystemMediaTransportControlsButton.Pause => PausePressed,
            SystemMediaTransportControlsButton.Next => NextPressed,
            SystemMediaTransportControlsButton.Previous => PreviousPressed,
            _ => null,
        };
        if (handler is not null) _ui.Enqueue(() => handler());
    }

    private static SystemMediaTransportControls? ForWindow(nint hwnd)
    {
        try
        {
            var factory = WinRT.ActivationFactory.Get("Windows.Media.SystemMediaTransportControls");
            var iid = IidInterop;
            if (Marshal.QueryInterface(factory.ThisPtr, in iid, out var interop) < 0) return null;
            try
            {
                // ISystemMediaTransportControlsInterop: IInspectable (0–5), GetForWindow (6).
                var getForWindow = (delegate* unmanaged[MemberFunction]<nint, nint, Guid*, nint*, int>)(*(void***)interop)[6];
                var riid = IidControls;
                nint controls;
                if (getForWindow(interop, hwnd, &riid, &controls) < 0) return null;
                try
                {
                    return SystemMediaTransportControls.FromAbi(controls);
                }
                finally
                {
                    Marshal.Release(controls);
                }
            }
            finally
            {
                Marshal.Release(interop);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_controls is null) return;
        _controls.ButtonPressed -= OnButtonPressed;
        _controls.PlaybackStatus = MediaPlaybackStatus.Closed;
        _controls.DisplayUpdater.ClearAll();
        _controls.DisplayUpdater.Update();
        _controls.IsEnabled = false;
    }
}

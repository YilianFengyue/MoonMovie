using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using MoonMovie.Core.Configuration;

namespace MoonMovie.Playback.Mpv;

/// <summary>
/// One libmpv instance rendering into a D3D11 composition swap chain (shown by <see cref="MpvVideoView"/>).
/// Events and property changes are raised on the UI thread.
/// </summary>
public sealed unsafe class MpvPlayer : IDisposable
{
    private readonly DispatcherQueue _ui;
    private readonly Thread _eventThread;
    private readonly Dictionary<ulong, string> _observed = [];
    private nint _handle;
    private volatile bool _disposing;

    public MpvPlayer(DispatcherQueue ui, int width, int height)
    {
        _ui = ui;
        _handle = LibMpv.mpv_create();
        if (_handle == 0) throw new InvalidOperationException("mpv_create failed");

        // Video: render into a DXGI composition swap chain that XAML composes (no child window, no airspace).
        Option("vo", "gpu-next");
        Option("gpu-context", "d3d11");
        Option("d3d11-output-mode", "composition");
        Option("d3d11-composition-size", $"{Math.Max(1, width)}x{Math.Max(1, height)}");
        Option("force-window", "immediate"); // create the output (and swap chain) before the first file
        Option("hwdec", "auto-safe");

        // We are the UI: no mpv OSD, OSC, key bindings or config files.
        Option("config", "no");
        Option("terminal", "no");
        Option("osc", "no");
        Option("osd-level", "0");
        Option("input-default-bindings", "no");
        Option("input-vo-keyboard", "no");
        Option("cursor-autohide", "no");
        Option("idle", "yes");
        Option("ytdl", "no"); // URLs are resolved by MoonMovie; never spawn youtube-dl
        Option("keep-open", "yes");

        // The page drives the Windows media flyout itself (SystemMediaControls), with artwork and episodes.
        Option("media-controls", "no");
        Option("audio-client-name", "MoonMovie");

        // Network: generous read-ahead, reconnect on drops.
        Option("cache", "yes");
        Option("demuxer-readahead-secs", "30");
        Option("demuxer-max-bytes", "96MiB");
        Option("demuxer-max-back-bytes", "32MiB");
        Option("stream-lavf-o", "reconnect=1,reconnect_streamed=1,reconnect_delay_max=5");

#if DEBUG
        // Experiments: MOONMOVIE_DEBUG_MPVOPTS="name=value;name=value" overrides any option above.
        foreach (var pair in (Environment.GetEnvironmentVariable("MOONMOVIE_DEBUG_MPVOPTS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0) Option(pair[..eq], pair[(eq + 1)..]);
        }
#endif
        Option("log-file", Path.Combine(AppPaths.Root, "mpv.log"));
        Check(LibMpv.mpv_request_log_messages(_handle, "warn"), "request_log_messages");
        Check(LibMpv.mpv_initialize(_handle), "mpv_initialize");

        Observe("display-swapchain", MpvFormat.Int64);
        _eventThread = new Thread(EventLoop) { Name = "mpv events", IsBackground = true };
        _eventThread.Start();
    }

    public static string Version => (LibMpv.mpv_client_api_version() >> 16) + "." + (LibMpv.mpv_client_api_version() & 0xFFFF);

    /// <summary>The composition swap chain, once mpv has created its output (0 before).</summary>
    public event Action<nint>? SwapChainChanged;

    /// <summary>Observed property changed: name and value (double, long, bool, string or null).</summary>
    public event Action<string, object?>? PropertyChanged;

    public event Action? FileLoaded;

    /// <summary>Playback (re)started after a load or seek: the first frame of the new position is up.</summary>
    public event Action? PlaybackRestart;

    /// <summary>End of file: reason (0 eof, 2 stop, 4 error) and mpv error code.</summary>
    public event Action<int, int>? EndFile;

    public event Action<string>? Log;

    // ----- Commands ------------------------------------------------------------------------------------

    /// <summary>Opens a file or URL, optionally starting at a position and with extra HTTP headers.</summary>
    public void Load(string url, double? start = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        var options = new List<string>();
        if (start is { } s && s > 0) options.Add("start=" + s.ToString("0.###", CultureInfo.InvariantCulture));
        if (headers is { Count: > 0 })
        {
            // mpv's list syntax: commas separate entries, so escape them inside values.
            options.Add("http-header-fields=" + string.Join(",", headers.Select(h => $"{h.Key}: {h.Value.Replace(",", "\\,")}")));
        }

        Command("loadfile", url, "replace", "-1", string.Join(",", options));
    }

    public void SetPause(bool paused) => SetProperty("pause", paused ? "yes" : "no");

    public void Seek(double seconds, bool exact = false) =>
        CommandAsync("seek", seconds.ToString("0.###", CultureInfo.InvariantCulture), exact ? "absolute+exact" : "absolute");

    public void SetCompositionSize(int width, int height)
    {
        if (width > 0 && height > 0) SetProperty("d3d11-composition-size", $"{width}x{height}");
    }

    public void Command(params string[] args)
    {
        if (_handle == 0) return;
        var result = LibMpv.Command(_handle, args);
        if (result < 0) RaiseLog($"command {args[0]} failed: {LibMpv.ErrorString(result)}");
    }

    public void CommandAsync(params string[] args)
    {
        if (_handle != 0) LibMpv.CommandAsync(_handle, 0, args);
    }

    public void SetProperty(string name, string value)
    {
        if (_handle == 0) return;
        var result = LibMpv.mpv_set_property_string(_handle, name, value);
        if (result < 0) RaiseLog($"set {name}={value} failed: {LibMpv.ErrorString(result)}");
    }

    public string? GetString(string name)
    {
        if (_handle == 0) return null;
        var p = LibMpv.mpv_get_property_string(_handle, name);
        if (p == 0) return null;
        try { return Marshal.PtrToStringUTF8(p); }
        finally { LibMpv.mpv_free(p); }
    }

    public double? GetDouble(string name)
    {
        double value;
        return _handle != 0 && LibMpv.mpv_get_property(_handle, name, MpvFormat.Double, &value) >= 0 ? value : null;
    }

    public long? GetInt64(string name)
    {
        long value;
        return _handle != 0 && LibMpv.mpv_get_property(_handle, name, MpvFormat.Int64, &value) >= 0 ? value : null;
    }

    /// <summary>Subscribes to a property; changes arrive through <see cref="PropertyChanged"/>.</summary>
    public void Observe(string name, MpvFormat format)
    {
        var id = (ulong)(_observed.Count + 1);
        _observed[id] = name;
        Check(LibMpv.mpv_observe_property(_handle, id, name, format), "observe " + name);
    }

    // ----- Event loop (own thread) ---------------------------------------------------------------------

    private void EventLoop()
    {
        while (!_disposing)
        {
            var ev = LibMpv.mpv_wait_event(_handle, -1);
            if (ev->EventId == MpvEventId.None) continue;
            if (ev->EventId == MpvEventId.Shutdown) break;

            switch (ev->EventId)
            {
                case MpvEventId.PropertyChange:
                    OnPropertyChange((MpvEventProperty*)ev->Data);
                    break;
                case MpvEventId.FileLoaded:
                    _ui.TryEnqueue(() => FileLoaded?.Invoke());
                    break;
                case MpvEventId.PlaybackRestart:
                    _ui.TryEnqueue(() => PlaybackRestart?.Invoke());
                    break;
                case MpvEventId.EndFile:
                    var end = *(MpvEventEndFile*)ev->Data;
                    _ui.TryEnqueue(() => EndFile?.Invoke(end.Reason, end.Error));
                    break;
                case MpvEventId.LogMessage:
                    var log = (MpvEventLogMessage*)ev->Data;
                    RaiseLog($"[{Marshal.PtrToStringUTF8((nint)log->Prefix)}] {Marshal.PtrToStringUTF8((nint)log->Text)?.TrimEnd()}");
                    break;
            }
        }
    }

    private void OnPropertyChange(MpvEventProperty* property)
    {
        var name = Marshal.PtrToStringUTF8((nint)property->Name) ?? string.Empty;
        object? value = property->Format switch
        {
            MpvFormat.Double => *(double*)property->Data,
            MpvFormat.Int64 => *(long*)property->Data,
            MpvFormat.Flag => *(int*)property->Data != 0,
            MpvFormat.String => Marshal.PtrToStringUTF8(*(nint*)property->Data),
            _ => null,
        };

        if (name == "display-swapchain")
        {
            var swapChain = value is long p ? (nint)p : 0;
            _ui.TryEnqueue(() => SwapChainChanged?.Invoke(swapChain));
            return;
        }

        _ui.TryEnqueue(() => PropertyChanged?.Invoke(name, value));
    }

    private void RaiseLog(string line) => _ui.TryEnqueue(() => Log?.Invoke(line));

    private void Option(string name, string value)
    {
        var result = LibMpv.mpv_set_option_string(_handle, name, value);
        if (result < 0) RaiseLog($"option {name}={value}: {LibMpv.ErrorString(result)}");
    }

    private static void Check(int result, string what)
    {
        if (result < 0) throw new InvalidOperationException($"mpv {what}: {LibMpv.ErrorString(result)}");
    }

    /// <summary>Stops the event thread first (mpv_wait_event must not race destruction), then tears mpv down.</summary>
    public void Dispose()
    {
        if (_handle == 0) return;
        _disposing = true;
        LibMpv.mpv_wakeup(_handle);
        _eventThread.Join(TimeSpan.FromSeconds(2));
        var handle = _handle;
        _handle = 0;
        LibMpv.mpv_terminate_destroy(handle);
    }
}

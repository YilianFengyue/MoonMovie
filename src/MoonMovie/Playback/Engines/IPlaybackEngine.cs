using Microsoft.UI.Xaml;

namespace MoonMovie.Playback.Engines;

public enum EngineState
{
    Idle,
    Opening,
    Buffering,
    Playing,
    Paused,
    Ended,
}

/// <summary>
/// What the player page needs from a playback engine. <see cref="MpvEngine"/> is the default;
/// <see cref="SystemEngine"/> (Media Foundation) is the fallback when libmpv is missing or fails to start.
/// Engine-specific extras (tracks, picture, shaders…) live on the concrete type.
/// </summary>
public interface IPlaybackEngine : IDisposable
{
    /// <summary>"mpv" or "系统".</summary>
    string Name { get; }

    /// <summary>The picture; the page puts it at the bottom of its layer stack.</summary>
    FrameworkElement View { get; }

    EngineState State { get; }

    TimeSpan Position { get; }

    TimeSpan Duration { get; }

    /// <summary>Seconds of media buffered ahead of <see cref="Position"/>.</summary>
    double BufferedAhead { get; }

    double Rate { get; set; }

    /// <summary>0–1.5 (above 1 boosts, where supported).</summary>
    double Volume { get; set; }

    bool Muted { get; set; }

    /// <summary>Starts loading; <paramref name="start"/> is applied as part of the open (no visible jump).</summary>
    Task OpenAsync(string url, TimeSpan start, bool isHls);

    void Play();

    void Pause();

    void Seek(TimeSpan position, bool exact = false);

    /// <summary>The media is open and its duration known.</summary>
    event Action? Opened;

    event Action? StateChanged;

    /// <summary>Playback reached the end.</summary>
    event Action? Ended;

    /// <summary>The media could not be played (message for the user / log).</summary>
    event Action<string>? Failed;

    /// <summary>Fine-grained position updates where the engine has them (every frame for mpv).</summary>
    event Action? PositionChanged;
}

using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using MoonMovie.Core.Danmaku;
using MoonMovie.Core.Settings;
using Microsoft.Graphics.DirectX;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace MoonMovie.Controls;

/// <summary>
/// Bullet-comment layer slaved to the media clock: positions are a pure function of media time, so pausing freezes
/// the comments, seeking re-flows them and playback speed carries over. Each comment is rasterised once (outlined
/// text) and then only blitted, which keeps dense moments cheap.
/// </summary>
/// <remarks>
/// Drawn with Win2D into a composition drawing surface (premultiplied alpha, composed by the system with the rest
/// of the window) rather than a CanvasAnimatedControl: that control is its own swap chain stacked over the video's,
/// and until it has presented a frame (or after it is recreated) it shows as an opaque black sheet — picture gone,
/// sound still playing.
/// </remarks>
public sealed partial class DanmakuOverlay : Grid
{
    // A system face: Win2D resolves ms-appx font URIs through package APIs, which an unpackaged app does not have.
    private const string FontFamily = "Microsoft YaHei UI";
    private const double PinnedSeconds = 4.5;
    private const double ScrollSecondsAtNormalSpeed = 8.5;
    private const double LaneGap = 28;
    private const double TopInset = 12;
    private const double StaleAfter = 0.6;
    private const int MaxSpawnsPerFrame = 16;

    private readonly object _gate = new();

    // ---- Shared state (kept behind a lock so the clock can be fed from anywhere) ----
    private IReadOnlyList<DanmakuComment> _comments = [];
    private int _commentsVersion;
    private double _clockPosition;
    private long _clockStamp = Stopwatch.GetTimestamp();
    private double _clockRate = 1;
    private bool _clockPlaying;
    private double _offset;
    private Look _look = Look.From(new DanmakuSettings());
    private int _lookVersion;

    // ---- Renderer state ----
    private readonly List<Item> _active = [];
    private Item?[] _scrollLanes = [];
    private Item?[] _topLanes = [];
    private Item?[] _bottomLanes = [];
    private CanvasTextFormat? _format;
    private CanvasStrokeStyle? _stroke;
    private double _formatSize;
    private int _seenComments = -1;
    private int _seenLook = -1;
    private int _next;
    private double _lastNow = double.NaN;

    // ---- Composition ----
    private CanvasDevice? _device;
    private CompositionGraphicsDevice? _graphics;
    private CompositionDrawingSurface? _surface;
    private SpriteVisual? _sprite;
    private bool _rendering;
    private bool _on = true;
    private bool _faulted;

    public DanmakuOverlay()
    {
        IsHitTestVisible = false;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => Kick();
    }

    /// <summary>How many comments are loaded (after filtering).</summary>
    public int Count
    {
        get
        {
            lock (_gate) return _comments.Count;
        }
    }

    /// <summary>Seconds added to every comment's time; positive shows them later.</summary>
    public double Offset
    {
        get
        {
            lock (_gate) return _offset;
        }
        set
        {
            lock (_gate) _offset = value;
            Kick();
        }
    }

    public bool IsOn
    {
        get => _on;
        set
        {
            _on = value;
            Kick();
        }
    }

    public void SetComments(IReadOnlyList<DanmakuComment> comments)
    {
        lock (_gate)
        {
            _comments = comments;
            _commentsVersion++;
        }

        Kick();
    }

    public void Clear() => SetComments([]);

    public void ApplySettings(DanmakuSettings settings)
    {
        lock (_gate)
        {
            _look = Look.From(settings);
            _lookVersion++;
        }

        Kick();
    }

    /// <summary>Feeds the media clock; small drift is absorbed so motion stays smooth between reports.</summary>
    public void Sync(TimeSpan position, bool playing, double rate)
    {
        var seconds = position.TotalSeconds;
        var now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            var predicted = Predict(now);
            var stateChanged = playing != _clockPlaying || Math.Abs(rate - _clockRate) > 0.001;
            if (stateChanged || Math.Abs(predicted - seconds) > 0.12)
            {
                _clockPosition = seconds;
                _clockStamp = now;
            }

            _clockPlaying = playing;
            _clockRate = rate;
        }

        Kick();
    }

    // ----- Lifecycle ------------------------------------------------------------------------------------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_sprite is not null) return;
#if DEBUG
        if (Environment.GetEnvironmentVariable("MOONMOVIE_DEBUG_NOCANVAS") == "1") return;
#endif
        try
        {
            var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
            _device = CanvasDevice.GetSharedDevice();
            _device.DeviceLost += OnDeviceLost;
            _graphics = CanvasComposition.CreateCompositionGraphicsDevice(compositor, _device);
            _surface = _graphics.CreateDrawingSurface(new Windows.Foundation.Size(1, 1),
                DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);
            _sprite = compositor.CreateSpriteVisual();
            _sprite.Brush = compositor.CreateSurfaceBrush(_surface);
            _sprite.RelativeSizeAdjustment = System.Numerics.Vector2.One;
            ElementCompositionPreview.SetElementChildVisual(this, _sprite);
        }
        catch (Exception ex)
        {
            Fault(ex);
            return;
        }

        Kick();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopRendering();
        ElementCompositionPreview.SetElementChildVisual(this, null);
        foreach (var item in _active) item.Bitmap.Dispose();
        _active.Clear();
        _surface?.Dispose();
        _graphics?.Dispose();
        if (_device is not null) _device.DeviceLost -= OnDeviceLost;
        _sprite = null;
        _surface = null;
        _graphics = null;
        _device = null;
    }

    /// <summary>GPU reset (driver update, sleep, adapter change): continue on a fresh device.</summary>
    private void OnDeviceLost(CanvasDevice sender, object args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_graphics is null) return;
        sender.DeviceLost -= OnDeviceLost;
        _device = CanvasDevice.GetSharedDevice();
        _device.DeviceLost += OnDeviceLost;
        CanvasComposition.SetCanvasDevice(_graphics, _device);
        ResetRenderState();
        Kick();
    });

    /// <summary>
    /// Animate every frame only while there is something moving; otherwise draw a single frame (a seek or a
    /// setting change while paused, or clearing the layer) and go idle.
    /// </summary>
    private void Kick()
    {
        bool animate;
        lock (_gate) animate = _on && _clockPlaying && _comments.Count > 0;

        if (animate && !_faulted)
        {
            if (!_rendering)
            {
                _rendering = true;
                CompositionTarget.Rendering += OnRendering;
            }
        }
        else
        {
            StopRendering();
            RenderFrame();
        }
    }

    private void StopRendering()
    {
        if (!_rendering) return;
        _rendering = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, object e) => RenderFrame();

    private double Predict(long now) =>
        _clockPosition + (_clockPlaying ? (now - _clockStamp) / (double)Stopwatch.Frequency * _clockRate : 0);

    private void RenderFrame()
    {
        if (_surface is null || _faulted) return;

        var width = ActualWidth;
        var height = ActualHeight;
        var scale = XamlRoot?.RasterizationScale ?? 1.0;
        if (width < 1 || height < 1) return;

        try
        {
            var pixels = new Windows.Graphics.SizeInt32((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale));
            if (_surface.SizeInt32.Width != pixels.Width || _surface.SizeInt32.Height != pixels.Height)
            {
                CanvasComposition.Resize(_surface, new Windows.Foundation.Size(pixels.Width, pixels.Height));
            }

            using var ds = CanvasComposition.CreateDrawingSession(_surface,
                new Windows.Foundation.Rect(0, 0, pixels.Width, pixels.Height), (float)(96 * scale));
            ds.Clear(Colors.Transparent);
            if (_on) Draw(ds, width, height);
        }
        catch (Exception ex) when (_device?.IsDeviceLost(ex.HResult) == true)
        {
            _device.RaiseDeviceLost();
        }
        catch (Exception ex)
        {
            Fault(ex);
        }
    }

    /// <summary>Comments are not worth taking the player down: a failure switches the layer off and is logged.</summary>
    private void Fault(Exception ex)
    {
        _faulted = true;
        StopRendering();
        try
        {
            File.AppendAllText(Path.Combine(Core.Configuration.AppPaths.Root, "danmaku.log"),
                $"[{DateTimeOffset.Now:O}] {ex}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }

    private void Draw(CanvasDrawingSession ds, double width, double height)
    {
        IReadOnlyList<DanmakuComment> comments;
        int commentsVersion, lookVersion;
        Look look;
        double now;
        lock (_gate)
        {
            comments = _comments;
            commentsVersion = _commentsVersion;
            look = _look;
            lookVersion = _lookVersion;
            now = Predict(Stopwatch.GetTimestamp()) - _offset;
        }

        if (width < 50 || height < 50) return;

        var fontSize = Math.Round(25 * look.FontScale * Math.Clamp(height / 900, 0.72, 1.45));
        var lineHeight = Math.Ceiling(fontSize * 1.36);

        if (commentsVersion != _seenComments || lookVersion != _seenLook || Math.Abs(fontSize - _formatSize) > 0.1)
        {
            _seenComments = commentsVersion;
            _seenLook = lookVersion;
            EnsureFormat(fontSize);
            Reset(comments, now);
        }

        // A jump in media time is a seek (or a long stall): re-flow from the new position.
        if (double.IsNaN(_lastNow) || now < _lastNow - 0.25 || now > _lastNow + 1.0)
        {
            Reset(comments, now);
        }

        _lastNow = now;
        EnsureLanes(ref _scrollLanes, Math.Max(1, (int)((height * look.Area - 8) / lineHeight)));
        EnsureLanes(ref _topLanes, Math.Max(1, (int)((height * Math.Min(look.Area, 0.5) - 8) / lineHeight)));
        EnsureLanes(ref _bottomLanes, Math.Max(1, (int)(height * 0.25 / lineHeight)));

        var spawned = 0;
        while (_next < comments.Count && comments[_next].Time <= now)
        {
            var comment = comments[_next++];
            if (now - comment.Time > StaleAfter || spawned >= MaxSpawnsPerFrame) continue;
            if (comment.Mode == DanmakuMode.Top && !look.ShowTop) continue;
            if (comment.Mode == DanmakuMode.Bottom && !look.ShowBottom) continue;
            if (TrySpawn(ds, comment, now, width, height, lineHeight, look)) spawned++;
        }

        for (var i = _active.Count - 1; i >= 0; i--)
        {
            var item = _active[i];
            double x, y;
            if (item.Mode == DanmakuMode.Scroll)
            {
                x = width - (now - item.Start) * item.Speed;
                if (x + item.Width < 0)
                {
                    Retire(i);
                    continue;
                }

                y = TopInset + item.Lane * lineHeight;
            }
            else
            {
                if (now - item.Start > PinnedSeconds)
                {
                    Retire(i);
                    continue;
                }

                x = (width - item.Width) / 2;
                y = item.Mode == DanmakuMode.Top
                    ? TopInset + item.Lane * lineHeight
                    : height - 24 - (item.Lane + 1) * lineHeight;
            }

            ds.DrawImage(item.Bitmap, (float)(x - item.Pad), (float)(y - item.Pad), item.Bitmap.Bounds, (float)look.Opacity);
        }
    }

    private bool TrySpawn(CanvasDrawingSession ds, DanmakuComment comment, double now, double width, double height,
        double lineHeight, Look look)
    {
        var text = comment.Text.Length > 48 ? comment.Text[..48] + "…" : comment.Text;

        // Measure first; rasterise only once a lane is found.
        using var layout = new CanvasTextLayout(ds, text, _format!, 4096, (float)lineHeight);
        var textWidth = layout.LayoutBounds.Width;

        int lane;
        double speed = 0;
        if (comment.Mode == DanmakuMode.Scroll)
        {
            speed = (width + textWidth) / (ScrollSecondsAtNormalSpeed / look.Speed);
            lane = FindScrollLane(textWidth, speed, now, width);
        }
        else
        {
            var lanes = comment.Mode == DanmakuMode.Top ? _topLanes : _bottomLanes;
            lane = Array.FindIndex(lanes, l => l is null || now - l.Start > PinnedSeconds);
        }

        if (lane < 0) return false; // the screen is full: dropping beats piling up

        var item = Rasterise(ds, layout, comment, textWidth, lineHeight);
        item.Lane = lane;
        item.Start = now;
        item.Speed = speed;
        _active.Add(item);
        (comment.Mode switch
        {
            DanmakuMode.Top => _topLanes,
            DanmakuMode.Bottom => _bottomLanes,
            _ => _scrollLanes,
        })[lane] = item;
        return true;
    }

    /// <summary>
    /// A lane is free once its last comment has fully entered (plus a gap) and the newcomer, if faster, cannot
    /// catch it before it leaves.
    /// </summary>
    private int FindScrollLane(double textWidth, double speed, double now, double width)
    {
        for (var i = 0; i < _scrollLanes.Length; i++)
        {
            // A comment pinned to the top owns its row while it is up.
            if (i < _topLanes.Length && _topLanes[i] is { } pinned && now - pinned.Start <= PinnedSeconds) continue;

            var last = _scrollLanes[i];
            if (last is null || !_active.Contains(last)) return i;

            var travelled = (now - last.Start) * last.Speed;
            if (travelled < last.Width + LaneGap) continue;

            var lastGone = last.Start + (width + last.Width) / last.Speed;
            var headArrives = now + width / speed;
            if (speed <= last.Speed || headArrives >= lastGone) return i;
        }

        return -1;
    }

    private Item Rasterise(CanvasDrawingSession ds, CanvasTextLayout layout, DanmakuComment comment, double textWidth,
        double lineHeight)
    {
        var pad = Math.Ceiling(_formatSize * 0.12) + 1;
        var bitmap = new CanvasRenderTarget(ds, (float)(textWidth + pad * 2), (float)(lineHeight + pad * 2));
        using (var geometry = CanvasGeometry.CreateText(layout))
        using (var rds = bitmap.CreateDrawingSession())
        {
            rds.Clear(Colors.Transparent);
            var r = (byte)(comment.Color >> 16);
            var g = (byte)(comment.Color >> 8);
            var b = (byte)comment.Color;

            // Dark text gets a light outline, everything else a dark one.
            var luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            var outline = luminance < 60 ? Color.FromArgb(200, 255, 255, 255) : Color.FromArgb(190, 0, 0, 0);
            rds.Transform = System.Numerics.Matrix3x2.CreateTranslation((float)pad, (float)pad);
            rds.DrawGeometry(geometry, outline, (float)(_formatSize * 0.11), _stroke);
            rds.FillGeometry(geometry, Color.FromArgb(255, r, g, b));
        }

        return new Item(bitmap, comment.Mode, textWidth, pad);
    }

    private void EnsureFormat(double fontSize)
    {
        if (_format is not null && Math.Abs(fontSize - _formatSize) < 0.1) return;
        _format?.Dispose();
        _format = new CanvasTextFormat
        {
            FontFamily = FontFamily,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            FontSize = (float)fontSize,
            WordWrapping = CanvasWordWrapping.NoWrap,
            VerticalAlignment = CanvasVerticalAlignment.Center,
        };
        _stroke ??= new CanvasStrokeStyle { LineJoin = CanvasLineJoin.Round };
        _formatSize = fontSize;
    }

    private static void EnsureLanes(ref Item?[] lanes, int count)
    {
        if (lanes.Length != count) Array.Resize(ref lanes, count);
    }

    private void Reset(IReadOnlyList<DanmakuComment> comments, double now)
    {
        foreach (var item in _active) item.Bitmap.Dispose();
        _active.Clear();
        Array.Clear(_scrollLanes);
        Array.Clear(_topLanes);
        Array.Clear(_bottomLanes);

        // First comment at or just before "now" (binary search on the sorted list).
        int lo = 0, hi = comments.Count;
        var from = now - 0.1;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (comments[mid].Time < from) lo = mid + 1;
            else hi = mid;
        }

        _next = lo;
        _lastNow = now;
    }

    private void ResetRenderState()
    {
        _format = null;
        _formatSize = 0;
        _seenComments = -1;
        _active.Clear();
        Array.Clear(_scrollLanes);
        Array.Clear(_topLanes);
        Array.Clear(_bottomLanes);
    }

    private void Retire(int index)
    {
        _active[index].Bitmap.Dispose();
        _active.RemoveAt(index);
    }

    private sealed class Item(CanvasRenderTarget bitmap, DanmakuMode mode, double width, double pad)
    {
        public CanvasRenderTarget Bitmap { get; } = bitmap;

        public DanmakuMode Mode { get; } = mode;

        public double Width { get; } = width;

        public double Pad { get; } = pad;

        public int Lane { get; set; }

        /// <summary>Media time it appeared.</summary>
        public double Start { get; set; }

        /// <summary>Pixels per media second (scrolling only).</summary>
        public double Speed { get; set; }
    }

    private sealed record Look(double Opacity, double FontScale, double Speed, double Area, bool ShowTop, bool ShowBottom)
    {
        public static Look From(DanmakuSettings s) => new(
            Math.Clamp(s.Opacity, 0.1, 1),
            Math.Clamp(s.FontScale, 0.5, 2),
            Math.Clamp(s.Speed, 0.3, 3),
            Math.Clamp(s.Area, 0.1, 1),
            s.ShowTop,
            s.ShowBottom);
    }
}

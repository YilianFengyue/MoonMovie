using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using MoonMovie.Animations;
using MoonMovie.Playback;

namespace MoonMovie.Controls;

/// <summary>
/// Cinema-style scrubber: thin neutral track with buffered range, thickens on hover, shows a thumb and a time
/// preview that follows the pointer, and only seeks on release (no thrashing the decoder while dragging).
/// </summary>
public sealed partial class SeekBar : UserControl
{
    private IReadOnlyList<TimeSpan> _marks = [];
    private TimeSpan _duration;
    private TimeSpan _position;
    private double _buffered;
    private bool _hover;
    private bool _dragging;
    private bool _buffering;
    private Storyboard? _sweep;

    public SeekBar()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Render();
    }

    /// <summary>Raised on release with the chosen time.</summary>
    public event EventHandler<TimeSpan>? SeekRequested;

    /// <summary>Raised while dragging (null when the drag ends).</summary>
    public event EventHandler<TimeSpan?>? Scrubbing;

    public bool IsDragging => _dragging;

    /// <summary>While the player waits for data a soft light sweeps the track.</summary>
    public bool IsBuffering
    {
        get => _buffering;
        set
        {
            if (value == _buffering) return;
            _buffering = value;
            if (value)
            {
                StartSweep();
            }
            else
            {
                Motion.FadeTo(Sweep, 0, TimeSpan.FromMilliseconds(260));
            }
        }
    }

    private void StartSweep()
    {
        var width = Math.Max(1, Track.ActualWidth);
        Sweep.Width = Math.Clamp(width * 0.16, 80, 240);
        _sweep?.Stop();
        var travel = new DoubleAnimation
        {
            From = -Sweep.Width,
            To = width,
            Duration = TimeSpan.FromMilliseconds(Math.Clamp(width * 1.6, 1100, 2200)),
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(travel, SweepOffset);
        Storyboard.SetTargetProperty(travel, "X");
        _sweep = new Storyboard();
        _sweep.Children.Add(travel);
        _sweep.Begin();
        Motion.FadeTo(Sweep, 1, TimeSpan.FromMilliseconds(200));
    }

    public void Update(TimeSpan position, TimeSpan duration, double bufferedFraction)
    {
        _duration = duration;
        _buffered = Math.Clamp(bufferedFraction, 0, 1);
        if (!_dragging) _position = position;
        Render();
    }

    /// <summary>Times to mark on the track (chapters, end of the opening, start of the credits).</summary>
    public void SetMarks(IReadOnlyList<TimeSpan> marks)
    {
        _marks = marks;
        _marksDrawn = default;
        Render();
    }

    private double Fraction => _duration > TimeSpan.Zero ? Math.Clamp(_position / _duration, 0, 1) : 0;

    private void Render()
    {
        var width = Track.ActualWidth;
        if (width <= 0) return;
        // The sweep travels beyond the ends: keep it inside the rounded track.
        if (Track.Clip?.Rect.Width != width) Track.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, width, Track.ActualHeight) };
        PlayedFill.Width = width * Fraction;
        BufferedFill.Width = width * Math.Max(_buffered, Fraction);
        ThumbOffset.X = width * Fraction - Thumb.Width / 2;
        RenderMarks(width);
    }

    private (double Width, TimeSpan Duration, int Count) _marksDrawn;

    private void RenderMarks(double width)
    {
        // Rendered on every tick: only rebuild when something that moves the marks changed.
        var key = (width, _duration, _marks.Count);
        if (key == _marksDrawn) return;
        _marksDrawn = key;
        Marks.Children.Clear();
        if (_duration <= TimeSpan.Zero) return;
        foreach (var mark in _marks)
        {
            var f = mark / _duration;
            if (f is <= 0.002 or >= 0.998) continue;
            var gap = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = 2,
                Height = 4,
                Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Black) { Opacity = 0.7 },
            };
            Canvas.SetLeft(gap, width * f - 1);
            Marks.Children.Add(gap);
        }
    }

    private TimeSpan TimeAt(double x) =>
        _duration > TimeSpan.Zero ? _duration * Math.Clamp(x / Math.Max(1, Track.ActualWidth), 0, 1) : TimeSpan.Zero;

    private void ShowPreview(double x)
    {
        var time = TimeAt(x);
        PreviewText.Text = TimeText.Format(time);
        Preview.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var half = Preview.DesiredSize.Width / 2;
        Canvas.SetLeft(Preview, Math.Clamp(x - half, 0, Math.Max(0, Track.ActualWidth - Preview.DesiredSize.Width)));
        HoverFill.Width = Math.Clamp(x, 0, Track.ActualWidth);
    }

    private void SetEmphasis(bool on)
    {
        // Fluent motion: the track grows quickly and settles (4 → 8 px), the thumb springs in from small.
        var duration = TimeSpan.FromMilliseconds(on ? 167 : 250);
        var ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 };
        var scale = new DoubleAnimation { To = on ? 2 : 1, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(scale, TrackScale);
        Storyboard.SetTargetProperty(scale, "ScaleY");
        var storyboard = new Storyboard();
        storyboard.Children.Add(scale);
        foreach (var axis in new[] { "ScaleX", "ScaleY" })
        {
            var thumb = new DoubleAnimation
            {
                To = on ? 1 : 0.4,
                Duration = TimeSpan.FromMilliseconds(on ? 260 : 180),
                EasingFunction = on ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 } : ease,
            };
            Storyboard.SetTarget(thumb, ThumbScale);
            Storyboard.SetTargetProperty(thumb, axis);
            storyboard.Children.Add(thumb);
        }
        storyboard.Begin();

        Motion.FadeTo(Thumb, on ? 1 : 0, duration);
        Motion.FadeTo(Preview, on ? 1 : 0, duration);
        Motion.FadeTo(HoverFill, on ? 1 : 0, duration);
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _hover = true;
        ShowPreview(e.GetCurrentPoint(Track).Position.X);
        SetEmphasis(true);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _hover = false;
        if (!_dragging) SetEmphasis(false);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var x = e.GetCurrentPoint(Track).Position.X;
        ShowPreview(x);
        if (_dragging)
        {
            _position = TimeAt(x);
            Render();
            Scrubbing?.Invoke(this, _position);
        }
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = HitArea.CapturePointer(e.Pointer);
        var x = e.GetCurrentPoint(Track).Position.X;
        _position = TimeAt(x);
        Render();
        Scrubbing?.Invoke(this, _position);
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        HitArea.ReleasePointerCapture(e.Pointer);
        EndDrag();
        e.Handled = true;
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndDrag();

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs e) => EndDrag();

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        Scrubbing?.Invoke(this, null);
        SeekRequested?.Invoke(this, _position);
        if (!_hover) SetEmphasis(false);
    }
}

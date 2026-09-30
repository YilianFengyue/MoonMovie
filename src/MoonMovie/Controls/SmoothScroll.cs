using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MoonMovie.Controls;

/// <summary>
/// Animated wheel scrolling that accumulates rapid notches into one target instead of restarting from the
/// current (still animating) offset each time — which would lose distance and feel sticky.
/// </summary>
public static class SmoothScroll
{
    private const double WheelStepFactor = 1.1;
    private static readonly TimeSpan AccumulateWindow = TimeSpan.FromMilliseconds(260);
    private static readonly ConditionalWeakTable<ScrollViewer, Target> Targets = new();

    public static void Vertical(ScrollViewer viewer, int wheelDelta)
    {
        var state = Targets.GetOrCreateValue(viewer);
        var origin = state.IsFresh ? state.Vertical : viewer.VerticalOffset;
        state.Vertical = Math.Clamp(origin - wheelDelta * WheelStepFactor, 0, viewer.ScrollableHeight);
        state.Touch();
        viewer.ChangeView(null, state.Vertical, null, disableAnimation: false);
    }

    public static void Horizontal(ScrollViewer viewer, int wheelDelta)
    {
        var state = Targets.GetOrCreateValue(viewer);
        var origin = state.IsFresh ? state.Horizontal : viewer.HorizontalOffset;
        state.Horizontal = Math.Clamp(origin - wheelDelta * WheelStepFactor * 1.6, 0, viewer.ScrollableWidth);
        state.Touch();
        viewer.ChangeView(state.Horizontal, null, null, disableAnimation: false);
    }

    /// <summary>Nearest ancestor ScrollViewer that can actually scroll vertically.</summary>
    public static ScrollViewer? FindVerticalAncestor(DependencyObject start)
    {
        for (var node = VisualTreeHelper.GetParent(start); node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ScrollViewer { VerticalScrollMode: not ScrollMode.Disabled } viewer)
            {
                return viewer;
            }
        }

        return null;
    }

    private sealed class Target
    {
        private long _stamp;

        public double Vertical { get; set; }

        public double Horizontal { get; set; }

        public bool IsFresh => Environment.TickCount64 - _stamp < AccumulateWindow.TotalMilliseconds;

        public void Touch() => _stamp = Environment.TickCount64;
    }
}

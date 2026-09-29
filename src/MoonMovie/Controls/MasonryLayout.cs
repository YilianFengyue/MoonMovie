using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace MoonMovie.Controls;

/// <summary>
/// Virtualizing masonry: columns always fill the available width exactly, each item drops into the
/// shortest column, and only items near the viewport are realized. Optimised for append-only sources
/// (infinite scrolling); any other collection change triggers a full relayout.
/// </summary>
public sealed partial class MasonryLayout : VirtualizingLayout
{
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth), typeof(double), typeof(MasonryLayout), new PropertyMetadata(220d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty ColumnSpacingProperty = DependencyProperty.Register(
        nameof(ColumnSpacing), typeof(double), typeof(MasonryLayout), new PropertyMetadata(20d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty RowSpacingProperty = DependencyProperty.Register(
        nameof(RowSpacing), typeof(double), typeof(MasonryLayout), new PropertyMetadata(24d, OnLayoutPropertyChanged));

    public double MinColumnWidth
    {
        get => (double)GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    public double ColumnSpacing
    {
        get => (double)GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    public double RowSpacing
    {
        get => (double)GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    protected override void InitializeForContextCore(VirtualizingLayoutContext context) =>
        context.LayoutState = new State();

    protected override void UninitializeForContextCore(VirtualizingLayoutContext context) =>
        context.LayoutState = null;

    protected override void OnItemsChangedCore(VirtualizingLayoutContext context, object source, NotifyCollectionChangedEventArgs args)
    {
        var state = (State)context.LayoutState;
        var appendedAtEnd = args.Action == NotifyCollectionChangedAction.Add && args.NewStartingIndex >= state.Bounds.Count;
        if (!appendedAtEnd)
        {
            RecycleAll(context, state);
            state.Reset(state.Columns);
        }

        InvalidateMeasure();
    }

    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        var state = (State)context.LayoutState;
        var width = double.IsInfinity(availableSize.Width) ? MinColumnWidth : availableSize.Width;
        var spacing = ColumnSpacing;
        var columns = Math.Max(1, (int)Math.Floor((width + spacing) / (MinColumnWidth + spacing)));
        var columnWidth = Math.Floor((width - (columns - 1) * spacing) / columns);

        if (columns != state.Columns || Math.Abs(columnWidth - state.ColumnWidth) > 0.5)
        {
            RecycleAll(context, state);
            state.Reset(columns);
            state.ColumnWidth = columnWidth;
        }

        var count = context.ItemCount;
        var realization = context.RealizationRect;
        var realizeBottom = realization.Height > 0 ? realization.Bottom : 0;

        // Extend the laid-out prefix until every column reaches past the realization window.
        for (var i = state.Bounds.Count; i < count; i++)
        {
            if (state.Bounds.Count >= columns * 2 && state.ShortestColumnHeight > realizeBottom)
            {
                break;
            }

            var element = context.GetOrCreateElementAt(i);
            element.Measure(new Size(columnWidth, double.PositiveInfinity));
            var column = state.ShortestColumn;
            var rect = new Rect(column * (columnWidth + spacing), state.ColumnHeights[column], columnWidth, element.DesiredSize.Height);
            state.Bounds.Add(rect);
            state.ColumnHeights[column] = rect.Bottom + RowSpacing;
            state.Realized.Add(i);
        }

        // Realize what intersects the window; recycle the rest.
        var keep = new HashSet<int>();
        for (var i = 0; i < state.Bounds.Count; i++)
        {
            var b = state.Bounds[i];
            if (realization.Height > 0 && b.Bottom >= realization.Top && b.Top <= realization.Bottom)
            {
                var element = context.GetOrCreateElementAt(i);
                element.Measure(new Size(columnWidth, double.PositiveInfinity));
                keep.Add(i);
            }
        }

        foreach (var index in state.Realized)
        {
            if (!keep.Contains(index))
            {
                context.RecycleElement(context.GetOrCreateElementAt(index));
            }
        }

        state.Realized = keep;

        var laidOut = state.Bounds.Count;
        var height = laidOut == 0 ? 0 : state.TallestColumnHeight - RowSpacing;
        if (laidOut < count && laidOut > 0)
        {
            // Estimate the unmeasured tail so the scrollbar stays honest.
            height += (count - laidOut) * (height / laidOut);
        }

        return new Size(width, Math.Max(0, height));
    }

    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        var state = (State)context.LayoutState;
        foreach (var index in state.Realized)
        {
            context.GetOrCreateElementAt(index).Arrange(state.Bounds[index]);
        }

        return finalSize;
    }

    private static void RecycleAll(VirtualizingLayoutContext context, State state)
    {
        foreach (var index in state.Realized)
        {
            if (index < context.ItemCount)
            {
                context.RecycleElement(context.GetOrCreateElementAt(index));
            }
        }

        state.Realized.Clear();
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((MasonryLayout)d).InvalidateMeasure();

    private sealed class State
    {
        public List<Rect> Bounds { get; } = [];

        public HashSet<int> Realized { get; set; } = [];

        public double[] ColumnHeights { get; private set; } = [0];

        public int Columns { get; private set; }

        public double ColumnWidth { get; set; }

        public int ShortestColumn
        {
            get
            {
                var best = 0;
                for (var c = 1; c < ColumnHeights.Length; c++)
                {
                    if (ColumnHeights[c] < ColumnHeights[best]) best = c;
                }

                return best;
            }
        }

        public double ShortestColumnHeight => ColumnHeights.Min();

        public double TallestColumnHeight => ColumnHeights.Max();

        public void Reset(int columns)
        {
            Columns = Math.Max(1, columns);
            Bounds.Clear();
            Realized.Clear();
            ColumnHeights = new double[Columns];
        }
    }
}

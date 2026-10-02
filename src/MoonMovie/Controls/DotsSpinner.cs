using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;

namespace MoonMovie.Controls;

/// <summary>
/// The classic Windows progress ring: five dots chase each other round a circle — gathering, sweeping, slowing at
/// the top — twice, then vanish and start again. Pure composition visuals (it hosts nothing), so it is safe to
/// animate anywhere.
/// </summary>
public sealed partial class DotsSpinner : Grid
{
    private const int DotCount = 5;
    private static readonly TimeSpan Cycle = TimeSpan.FromMilliseconds(4400);
    private static readonly TimeSpan Stagger = TimeSpan.FromMilliseconds(167);

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(DotsSpinner), new PropertyMetadata(true, (d, _) => ((DotsSpinner)d).Sync()));

    public static readonly DependencyProperty DotColorProperty = DependencyProperty.Register(
        nameof(DotColor), typeof(Color), typeof(DotsSpinner), new PropertyMetadata(Colors.White, (d, _) => ((DotsSpinner)d).Rebuild()));

    private ContainerVisual? _root;

    public DotsSpinner()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => Rebuild();
        Unloaded += (_, _) => Teardown();
        SizeChanged += (_, _) => Rebuild();
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public Color DotColor
    {
        get => (Color)GetValue(DotColorProperty);
        set => SetValue(DotColorProperty, value);
    }

    private void Sync()
    {
        if (_root is not null) _root.IsVisible = IsActive;
        if (IsActive && _root is null) Rebuild();
    }

    private void Teardown()
    {
        ElementCompositionPreview.SetElementChildVisual(this, null);
        _root?.Dispose();
        _root = null;
    }

    private void Rebuild()
    {
        if (!IsLoaded || ActualWidth < 4) return;
        Teardown();

        var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        var size = (float)Math.Min(ActualWidth, ActualHeight);
        var dot = Math.Max(2.5f, size * 0.1f);
        var radius = size / 2 - dot / 2;
        var center = new Vector2((float)ActualWidth / 2, (float)ActualHeight / 2);

        _root = compositor.CreateContainerVisual();
        _root.Size = new Vector2((float)ActualWidth, (float)ActualHeight);
        _root.IsVisible = IsActive;

        var brush = compositor.CreateColorBrush(DotColor);
        for (var i = 0; i < DotCount; i++)
        {
            // Each dot is a small circle on an arm pivoting at the centre; the arm's angle is what animates.
            var arm = compositor.CreateContainerVisual();
            arm.Size = new Vector2(dot, radius + dot / 2);
            arm.Offset = new Vector3(center.X - dot / 2, center.Y - radius - dot / 2, 0);
            arm.CenterPoint = new Vector3(dot / 2, radius + dot / 2, 0);

            var circle = compositor.CreateEllipseGeometry();
            circle.Center = new Vector2(dot / 2, dot / 2);
            circle.Radius = new Vector2(dot / 2, dot / 2);
            var shape = compositor.CreateSpriteShape(circle);
            shape.FillBrush = brush;
            var dotVisual = compositor.CreateShapeVisual();
            dotVisual.Size = new Vector2(dot, dot);
            dotVisual.Shapes.Add(shape);
            arm.Children.InsertAtTop(dotVisual);

            arm.Opacity = 0; // each dot appears only when its turn starts
            arm.StartAnimation(nameof(Visual.RotationAngleInDegrees), Spin(compositor, i));
            arm.StartAnimation(nameof(Visual.Opacity), Presence(compositor, i));
            _root.Children.InsertAtTop(arm);
        }

        ElementCompositionPreview.SetElementChildVisual(this, _root);
    }

    /// <summary>Two laps: in fast, easing through the top, out fast — the familiar Windows rhythm.</summary>
    private static ScalarKeyFrameAnimation Spin(Compositor c, int index)
    {
        var a = c.CreateScalarKeyFrameAnimation();
        float At(double ms) => (float)(ms / Cycle.TotalMilliseconds);
        CubicBezierEasingFunction Ease(float x1, float y1, float x2, float y2) => c.CreateCubicBezierEasingFunction(new(x1, y1), new(x2, y2));

        a.InsertKeyFrame(0f, -110);
        a.InsertKeyFrame(At(433), 10, Ease(0.13f, 0.21f, 0.1f, 0.7f));
        a.InsertKeyFrame(At(1200), 93, c.CreateLinearEasingFunction());
        a.InsertKeyFrame(At(1617), 205, Ease(0.02f, 0.33f, 0.38f, 0.77f));
        a.InsertKeyFrame(At(2017), 357, Ease(0.57f, 0.17f, 0.95f, 0.75f));
        a.InsertKeyFrame(At(2783), 439, Ease(0f, 0.19f, 0.07f, 0.72f));
        a.InsertKeyFrame(At(3217), 532, c.CreateLinearEasingFunction());
        a.InsertKeyFrame(At(3617), 643, Ease(0f, 0f, 0.95f, 0.37f));
        a.InsertKeyFrame(1f, 643);
        a.Duration = Cycle;
        a.DelayTime = Stagger * index;
        a.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        a.IterationBehavior = AnimationIterationBehavior.Forever;
        return a;
    }

    /// <summary>Visible for the two laps, gone for the pause before the next round.</summary>
    private static ScalarKeyFrameAnimation Presence(Compositor c, int index)
    {
        var a = c.CreateScalarKeyFrameAnimation();
        var step = c.CreateStepEasingFunction();
        a.InsertKeyFrame(0f, 1);
        a.InsertKeyFrame(3600f / 4400f, 1);
        a.InsertKeyFrame(3601f / 4400f, 0, step);
        a.InsertKeyFrame(1f, 0, step);
        a.Duration = Cycle;
        a.DelayTime = Stagger * index;
        a.DelayBehavior = AnimationDelayBehavior.SetInitialValueAfterDelay;
        a.IterationBehavior = AnimationIterationBehavior.Forever;
        return a;
    }
}

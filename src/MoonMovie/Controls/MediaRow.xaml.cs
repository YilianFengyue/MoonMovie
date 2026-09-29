using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace MoonMovie.Controls;

/// <summary>A titled, horizontally paged shelf of cards.</summary>
public sealed partial class MediaRow : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(MediaRow), new PropertyMetadata(null));

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(object), typeof(MediaRow), new PropertyMetadata(null));

    public static readonly DependencyProperty IsLandscapeProperty = DependencyProperty.Register(
        nameof(IsLandscape), typeof(bool), typeof(MediaRow), new PropertyMetadata(false, OnIsLandscapeChanged));

    private bool _pointerInside;

    public MediaRow()
    {
        InitializeComponent();
        foreach (var button in new[] { PrevButton, NextButton })
        {
            button.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(160) };
        }

        Repeater.ItemTemplate = (DataTemplate)Application.Current.Resources["PosterCardTemplate"];
    }

    public string? Title
    {
        get => (string?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public object? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public bool IsLandscape
    {
        get => (bool)GetValue(IsLandscapeProperty);
        set => SetValue(IsLandscapeProperty, value);
    }

    private static void OnIsLandscapeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var row = (MediaRow)d;
        var key = (bool)e.NewValue ? "LandscapeCardTemplate" : "PosterCardTemplate";
        row.Repeater.ItemTemplate = (DataTemplate)Application.Current.Resources[key];
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _pointerInside = true;
        UpdatePagers();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerInside = false;
        UpdatePagers();
    }

    private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) => UpdatePagers();

    private void OnPrev(object sender, RoutedEventArgs e) => Page(-1);

    private void OnNext(object sender, RoutedEventArgs e) => Page(1);

    private void Page(int direction)
    {
        var step = Math.Max(200, Scroller.ViewportWidth - 160);
        var target = Math.Clamp(Scroller.HorizontalOffset + direction * step, 0, Scroller.ScrollableWidth);
        Scroller.ChangeView(target, null, null, disableAnimation: false);
    }

    private void UpdatePagers()
    {
        SetPager(PrevButton, _pointerInside && Scroller.HorizontalOffset > 4);
        SetPager(NextButton, _pointerInside && Scroller.HorizontalOffset < Scroller.ScrollableWidth - 4);
    }

    private static void SetPager(Button button, bool show)
    {
        if (show)
        {
            button.Visibility = Visibility.Visible;
        }

        button.Opacity = show ? 1 : 0;
        button.IsHitTestVisible = show;
    }
}

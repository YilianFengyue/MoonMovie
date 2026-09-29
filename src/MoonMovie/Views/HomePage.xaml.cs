using System.ComponentModel;
using System.Numerics;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using MoonMovie.Imaging;
using MoonMovie.Services;
using MoonMovie.ViewModels;

namespace MoonMovie.Views;

public sealed partial class HomePage : Page
{
    private static readonly TimeSpan RotateInterval = TimeSpan.FromSeconds(9);

    private readonly ImageLoader _images = App.Services.GetRequiredService<ImageLoader>();
    private readonly DispatcherQueueTimer _rotateTimer;
    private readonly List<Border> _pagerBars = [];
    private ExpressionAnimation? _dimExpression;
    private ExpressionAnimation? _fadeExpression;
    private int _spotlightIndex = -1;
    private int _spotlightVersion;
    private bool _heroHover;

    public HomePage()
    {
        ViewModel = App.Services.GetRequiredService<HomeViewModel>();
        InitializeComponent();

        _rotateTimer = DispatcherQueue.CreateTimer();
        _rotateTimer.Interval = RotateInterval;
        _rotateTimer.Tick += (_, _) => OnRotateTick();

        ElementCompositionPreview.SetIsTranslationEnabled(HeroCopy, true);
        ElementCompositionPreview.SetIsTranslationEnabled(HeroParallax, true);

        SizeChanged += OnSizeChanged;
        Loaded += (_, _) => SetupScrollExpressions();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        WeakReferenceMessenger.Default.Register<HomePage, AmbientRequest>(this, static (page, msg) => page.OnAmbientRequest(msg));
    }

    public HomeViewModel ViewModel { get; }

    // Hero.Height is known before the first layout pass; ActualHeight is still 0 when cached data arrives.
    private bool HeroInView => Scroller.VerticalOffset < Math.Max(Hero.ActualHeight, Hero.Height) * 0.45;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _rotateTimer.Start();

        if (_spotlightIndex >= 0)
        {
            // Returning from another page: restore the hero's own artwork.
            if (HeroInView) ShowHeroAmbient();
            return;
        }

        await ViewModel.LoadAsync();
        PrefetchSpotlight();
        BuildPager();
        if (ViewModel.Spotlight.Count > 0)
        {
            await ShowSpotlightAsync(0, animate: false);
        }

#if DEBUG
        // Visual QA hook: MOONMOVIE_DEBUG_SCROLL=<offset> opens the page pre-scrolled.
        if (double.TryParse(Environment.GetEnvironmentVariable("MOONMOVIE_DEBUG_SCROLL"), out var debugOffset))
        {
            await Task.Delay(1500);
            Scroller.ChangeView(null, debugOffset, null, disableAnimation: true);
        }
#endif
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _rotateTimer.Stop();
    }

    // ----- Spotlight --------------------------------------------------------------------------------------

    /// <summary>Warm artwork and title logos so every rotation is instant.</summary>
    private void PrefetchSpotlight()
    {
        foreach (var spot in ViewModel.Spotlight)
        {
            _images.Prefetch(spot.PreviewUrl, highPriority: true);
            _images.Prefetch(spot.BackdropUrl);
            _ = spot.EnsureBrandingAsync(_images);
        }
    }

    private void ShowHeroAmbient()
    {
        var spot = ViewModel.Spotlight[_spotlightIndex];
        Ambient.Show(spot.BackdropUrl, spot.PreviewUrl);
    }

    private void OnRotateTick()
    {
        if (_heroHover || !HeroInView || ViewModel.Spotlight.Count < 2)
        {
            return;
        }

        _ = ShowSpotlightAsync((_spotlightIndex + 1) % ViewModel.Spotlight.Count, animate: true);
    }

    private async Task ShowSpotlightAsync(int index, bool animate)
    {
        if (index == _spotlightIndex || index < 0 || index >= ViewModel.Spotlight.Count)
        {
            return;
        }

        var version = ++_spotlightVersion;
        _spotlightIndex = index;
        var spot = ViewModel.Spotlight[index];
        UpdatePager(index);
        _rotateTimer.Stop();
        _rotateTimer.Start();

        if (HeroInView)
        {
            Ambient.Show(spot.BackdropUrl, spot.PreviewUrl);
        }

        var copy = ElementCompositionPreview.GetElementVisual(HeroCopy);
        if (animate)
        {
            await AnimateCopyAsync(copy, toOpacity: 0, fromY: 0, toY: -10, TimeSpan.FromMilliseconds(170), accelerate: true);
        }

        // Wait briefly for the title logo so the title does not flash text and then swap to art.
        var branding = spot.EnsureBrandingAsync(_images);
        await Task.WhenAny(branding, Task.Delay(900));
        if (version != _spotlightVersion)
        {
            return;
        }

        ApplySpotlight(spot);
        _ = AnimateCopyAsync(copy, toOpacity: 1, fromY: 18, toY: 0, TimeSpan.FromMilliseconds(animate ? 520 : 700), accelerate: false);

        if (!branding.IsCompleted)
        {
            await branding;
            if (version == _spotlightVersion)
            {
                ApplyBranding(spot);
            }
        }
    }

    private void ApplySpotlight(SpotlightViewModel spot)
    {
        HeroOverline.Text = spot.Overline;
        HeroMeta.Text = spot.MetaLine;
        HeroOverview.Text = spot.Overview ?? string.Empty;
        HeroRating.Text = spot.RatingText ?? string.Empty;
        HeroRatingPanel.Visibility = spot.RatingText is null ? Visibility.Collapsed : Visibility.Visible;
        ApplyBranding(spot);
    }

    private void ApplyBranding(SpotlightViewModel spot)
    {
        HeroTitle.Text = spot.Title;
        var hasLogo = spot.LogoUrl is not null;
        ImageEx.SetUrl(HeroLogo, spot.LogoUrl);
        HeroLogo.Visibility = hasLogo ? Visibility.Visible : Visibility.Collapsed;
        HeroTitle.Visibility = hasLogo ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(HeroPlayButton, $"播放 {spot.Title}");
    }

    private static Task AnimateCopyAsync(Visual visual, float toOpacity, float fromY, float toY, TimeSpan duration, bool accelerate)
    {
        var compositor = visual.Compositor;
        var easing = accelerate
            ? compositor.CreateCubicBezierEasingFunction(new Vector2(0.7f, 0f), new Vector2(1f, 0.5f))
            : compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(1f, toOpacity, easing);
        opacity.Duration = duration;
        visual.StartAnimation("Opacity", opacity);

        var translation = compositor.CreateVector3KeyFrameAnimation();
        translation.InsertKeyFrame(0f, new Vector3(0, fromY, 0));
        translation.InsertKeyFrame(1f, new Vector3(0, toY, 0), easing);
        translation.Duration = duration;
        visual.StartAnimation("Translation", translation);

        batch.End();
        var tcs = new TaskCompletionSource();
        batch.Completed += (_, _) => tcs.TrySetResult();
        return tcs.Task;
    }

    private void BuildPager()
    {
        HeroPager.Children.Clear();
        _pagerBars.Clear();
        if (ViewModel.Spotlight.Count < 2)
        {
            return;
        }

        for (var i = 0; i < ViewModel.Spotlight.Count; i++)
        {
            var index = i;
            var bar = new Border
            {
                Width = 8,
                Height = 4,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(Microsoft.UI.Colors.White),
                Opacity = 0.35,
                OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(250) },
            };
            var button = new Button
            {
                Style = (Style)Application.Current.Resources["MoonCardButtonStyle"],
                Height = 20,
                HorizontalAlignment = HorizontalAlignment.Left,
                Content = bar,
                IsTabStop = false,
            };
            AutomationProperties.SetName(button, $"第 {i + 1} 部");
            button.Click += (_, _) => _ = ShowSpotlightAsync(index, animate: true);
            HeroPager.Children.Add(button);
            _pagerBars.Add(bar);
        }
    }

    private void UpdatePager(int active)
    {
        for (var i = 0; i < _pagerBars.Count; i++)
        {
            var bar = _pagerBars[i];
            var target = i == active ? 32 : 8;
            bar.Opacity = i == active ? 1 : 0.35;

            var animation = new DoubleAnimation
            {
                To = target,
                Duration = new Duration(TimeSpan.FromMilliseconds(320)),
                EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 },
                EnableDependentAnimation = true,
            };
            Storyboard.SetTarget(animation, bar);
            Storyboard.SetTargetProperty(animation, "Width");
            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }
    }

    private void OnHeroOpen(object sender, RoutedEventArgs e)
    {
        if (_spotlightIndex >= 0)
        {
            Navigator.OpenMedia(ViewModel.Spotlight[_spotlightIndex].Item);
        }
    }

    private void OnHeroPointerEntered(object sender, PointerRoutedEventArgs e) => _heroHover = true;

    private void OnHeroPointerExited(object sender, PointerRoutedEventArgs e) => _heroHover = false;

    // ----- Ambient & scrolling ------------------------------------------------------------------------------

    private void OnAmbientRequest(AmbientRequest request)
    {
        // Near the top the hero owns the background; below it, whatever the user is looking at does.
        if (!HeroInView)
        {
            Ambient.Show(request.Url);
        }
    }

    private void OnScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (HeroInView && _spotlightIndex >= 0)
        {
            ShowHeroAmbient();
        }

        if (Scroller.VerticalOffset + Scroller.ViewportHeight * 2 > Scroller.ExtentHeight)
        {
            _ = ViewModel.LoadMoreDiscoverAsync();
        }
    }

    private void SetupScrollExpressions()
    {
        if (_dimExpression is not null)
        {
            return;
        }

        var scroll = ElementCompositionPreview.GetScrollViewerManipulationPropertySet(Scroller);
        var compositor = scroll.Compositor;
        var height = (float)Math.Max(Hero.ActualHeight, 1);

        // Artwork dims as the shelves take over, but never fully disappears.
        _dimExpression = compositor.CreateExpressionAnimation("Clamp(1 + scroll.Translation.Y / (h * 1.1), 0.4, 1)");
        _dimExpression.SetReferenceParameter("scroll", scroll);
        _dimExpression.SetScalarParameter("h", height);
        ElementCompositionPreview.GetElementVisual(Ambient.DimTarget).StartAnimation("Opacity", _dimExpression);

        // Hero copy drifts slower than the page and fades out.
        var parallax = compositor.CreateExpressionAnimation("Vector3(0, -scroll.Translation.Y * 0.3, 0)");
        parallax.SetReferenceParameter("scroll", scroll);
        var heroVisual = ElementCompositionPreview.GetElementVisual(HeroParallax);
        heroVisual.StartAnimation("Translation", parallax);

        _fadeExpression = compositor.CreateExpressionAnimation("Clamp(1 + scroll.Translation.Y / (h * 0.5), 0, 1)");
        _fadeExpression.SetReferenceParameter("scroll", scroll);
        _fadeExpression.SetScalarParameter("h", height);
        heroVisual.StartAnimation("Opacity", _fadeExpression);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        Hero.Height = Math.Clamp(e.NewSize.Height * 0.86, 560, 1100);

        if (_dimExpression is not null && _fadeExpression is not null)
        {
            var height = (float)Hero.Height;
            _dimExpression.SetScalarParameter("h", height);
            _fadeExpression.SetScalarParameter("h", height);
            ElementCompositionPreview.GetElementVisual(Ambient.DimTarget).StartAnimation("Opacity", _dimExpression);
            ElementCompositionPreview.GetElementVisual(HeroParallax).StartAnimation("Opacity", _fadeExpression);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HomeViewModel.ErrorMessage))
        {
            ErrorText.Visibility = ViewModel.ErrorMessage is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}

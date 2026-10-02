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
using MoonMovie.Animations;
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
    private readonly Microsoft.UI.Xaml.Media.TranslateTransform _heroParallax = new();
    private int _spotlightIndex = -1;
    private int _spotlightVersion;
    private bool _heroHover;
    private bool _isActive;

    public HomePage()
    {
        ViewModel = App.Services.GetRequiredService<HomeViewModel>();
        InitializeComponent();

        _rotateTimer = DispatcherQueue.CreateTimer();
        _rotateTimer.Interval = RotateInterval;
        _rotateTimer.Tick += (_, _) => OnRotateTick();

        HeroParallax.RenderTransform = _heroParallax;

        SizeChanged += OnSizeChanged;
        Loaded += (_, _) => SetupScrollExpressions();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.Continue.CollectionChanged += (_, _) => UpdateContinueRow();

        WeakReferenceMessenger.Default.Register<HomePage, AmbientRequest>(this, static (page, msg) => page.OnAmbientRequest(msg));
    }

    public HomeViewModel ViewModel { get; }

    // Hero.Height is known before the first layout pass; ActualHeight is still 0 when cached data arrives.
    private bool HeroInView => Scroller.VerticalOffset < Math.Max(Hero.ActualHeight, Hero.Height) * 0.45;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _isActive = true;
        _rotateTimer.Start();
        RefreshContinue();

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

        // M0 engine lab: MOONMOVIE_DEBUG_MPV=<url or file> plays it straight through libmpv.
        if (Environment.GetEnvironmentVariable("MOONMOVIE_DEBUG_MPV") is { Length: > 0 } mpvUrl)
        {
            App.MainWindow.Navigate(typeof(MpvLabPage), mpvUrl);
        }

        // QA hook: MOONMOVIE_DEBUG_BILI=<BV id> plays that B站 video.
        if (Environment.GetEnvironmentVariable("MOONMOVIE_DEBUG_BILI") is { Length: > 0 } bvid)
        {
            _ = Services.BiliPlayback.PlayAsync(new Core.Bilibili.BiliVideo(bvid, 0, "", "", 0, "", 0, 0, 0, DateTimeOffset.Now, null));
            return;
        }

        // Visual QA hook: MOONMOVIE_DEBUG_NAV=movie|tv|anime|library|history|favorites opens that page.
        switch (Environment.GetEnvironmentVariable("MOONMOVIE_DEBUG_NAV"))
        {
            case "profile":
                App.MainWindow.Navigate(typeof(ProfilePage), null);
                return;
            case "movie": Navigator.OpenBrowse(Core.Browse.BrowseSection.Movie); break;
            case "tv": Navigator.OpenBrowse(Core.Browse.BrowseSection.Tv); break;
            case "anime": Navigator.OpenBrowse(Core.Browse.BrowseSection.Anime); break;
            case "library": Navigator.OpenLibrary(); break;
            case "history": Navigator.OpenLibrary(LibraryTab.History); break;
            case "favorites": Navigator.OpenLibrary(LibraryTab.Favorites); break;
            case "settings": App.MainWindow.Navigate(typeof(SettingsPage), null); break;
        }

        // Visual QA hook: MOONMOVIE_DEBUG_SEARCH=<query> opens the search page.
        if (Environment.GetEnvironmentVariable("MOONMOVIE_DEBUG_SEARCH") is { Length: > 0 } debugQuery)
        {
            App.MainWindow.Navigate(typeof(SearchPage), debugQuery);
        }

        // Visual QA hook: MOONMOVIE_DEBUG_OPEN=movie:157336 | tv:1396 opens that title's detail page.
        if (Environment.GetEnvironmentVariable("MOONMOVIE_DEBUG_OPEN")?.Split(':') is [var kind, var id]
            && int.TryParse(id, out var tmdbId))
        {
            var tmdb = App.Services.GetRequiredService<Core.Tmdb.TmdbClient>();
            var detail = await tmdb.DetailAsync(kind == "tv" ? Core.Models.MediaKind.Tv : Core.Models.MediaKind.Movie, tmdbId);
            if (detail is not null) Navigator.OpenMedia(detail.Item);
        }
#endif
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _isActive = false;
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

        // Storyboards (not composition): the hero copy hosts the action buttons — see Animations/Motion.cs.
        if (animate)
        {
            await Motion.SlideFadeAsync(HeroCopy, 0, 0, 0, -10, null, 0, TimeSpan.FromMilliseconds(170), decelerate: false);
        }

        // Wait briefly for the title logo so the title does not flash text and then swap to art.
        var branding = spot.EnsureBrandingAsync(_images);
        await Task.WhenAny(branding, Task.Delay(900));
        if (version != _spotlightVersion)
        {
            return;
        }

        ApplySpotlight(spot);
        _ = Motion.SlideFadeAsync(HeroCopy, 0, 0, 18, 0, null, 1, TimeSpan.FromMilliseconds(animate ? 520 : 700));

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
        UpdateHeroFavorite();
    }

    private void UpdateHeroFavorite()
    {
        if (_spotlightIndex < 0) return;
        var on = App.Services.GetRequiredService<Core.Library.FavoritesStore>()
            .IsFavorite(ViewModel.Spotlight[_spotlightIndex].Item.MediaKey);
        HeroFavoriteIcon.Glyph = on ? "" : "";
        var label = on ? "已收藏" : "收藏";
        AutomationProperties.SetName(HeroFavoriteButton, label);
        ToolTipService.SetToolTip(HeroFavoriteButton, label);
    }

    private void OnHeroFavorite(object sender, RoutedEventArgs e)
    {
        if (_spotlightIndex < 0) return;
        App.Services.GetRequiredService<Core.Library.FavoritesStore>().Toggle(ViewModel.Spotlight[_spotlightIndex].Item);
        UpdateHeroFavorite();
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
            Animations.Motion.FadeTo(bar, i == active ? 1 : 0.35, TimeSpan.FromMilliseconds(250));

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

    private void RefreshContinue()
    {
        ViewModel.RefreshContinue();
        UpdateContinueRow();
    }

    private void UpdateContinueRow()
    {
        var has = ViewModel.Continue.Count > 0;
        ContinueRow.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        // The first shelf tucks under the hero; whichever shelf is first takes that overlap.
        RowsRepeater.Margin = new Thickness(0, has ? 0 : -56, 0, 0);
    }

    private void OnOpenLibrary(object sender, RoutedEventArgs e) => Navigator.OpenLibrary();

    private void OnHeroPointerEntered(object sender, PointerRoutedEventArgs e) => _heroHover = true;

    private void OnHeroPointerExited(object sender, PointerRoutedEventArgs e) => _heroHover = false;

    // ----- Ambient & scrolling ------------------------------------------------------------------------------

    private void OnAmbientRequest(AmbientRequest request)
    {
        // Near the top the hero owns the background; below it, whatever the user is looking at does.
        if (_isActive && !HeroInView)
        {
            Ambient.Show(request.Url);
        }
    }

    private void OnScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        // Hero copy drifts slower than the page and fades out (plain properties: it hosts controls).
        var offset = Scroller.VerticalOffset;
        _heroParallax.Y = offset * 0.3;
        HeroParallax.Opacity = Math.Clamp(1 - offset / (Hero.Height * 0.5), 0, 1);

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
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        Hero.Height = Math.Clamp(e.NewSize.Height * 0.86, 560, 1100);

        if (_dimExpression is not null)
        {
            _dimExpression.SetScalarParameter("h", (float)Hero.Height);
            ElementCompositionPreview.GetElementVisual(Ambient.DimTarget).StartAnimation("Opacity", _dimExpression);
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

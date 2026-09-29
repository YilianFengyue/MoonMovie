using System.ComponentModel;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using MoonMovie.Core.Models;
using MoonMovie.Imaging;
using MoonMovie.ViewModels;

namespace MoonMovie.Views;

public sealed partial class DetailPage : Page
{
    private static readonly TimeSpan PanelDuration = TimeSpan.FromMilliseconds(320);

    private readonly HashSet<string> _revealedSources = [];
    private ExpressionAnimation? _dimExpression;
    private ExpressionAnimation? _fadeExpression;
    private bool _panelOpen;
    private bool _syncingSeasons;
    private string? _lastSelectedIdentity;

    public DetailPage()
    {
        ViewModel = App.Services.GetRequiredService<DetailViewModel>();
        InitializeComponent();

        ElementCompositionPreview.SetIsTranslationEnabled(SourcePanel, true);
        SizeChanged += OnSizeChanged;
        Loaded += (_, _) => SetupScrollDimming();

        ViewModel.PropertyChanged += OnViewModelChanged;
        ViewModel.Sources.PropertyChanged += (_, _) => UpdatePlayState();
        ViewModel.Sources.Items.CollectionChanged += (_, _) => UpdatePlayState();
        ViewModel.Sources.Chosen += (_, _) => ClosePanel();
        ViewModel.Seasons.CollectionChanged += (_, _) => BuildSeasonBar();
        ViewModel.Cast.CollectionChanged += (_, _) => CastRow.Visibility = Visible(ViewModel.Cast.Count > 0);
        ViewModel.Recommendations.CollectionChanged += (_, _) =>
            RecommendationsRow.Visibility = Visible(ViewModel.Recommendations.Count > 0);
    }

    public DetailViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not MediaItem item)
        {
            return;
        }

        var load = ViewModel.LoadAsync(item);
        ApplyHeader();
        Ambient.Show(ViewModel.BackdropUrl, ViewModel.PreviewUrl);
        UpdatePlayState();
        await load;

#if DEBUG
        // Visual QA hooks: MOONMOVIE_DEBUG_PANEL=<ms> opens the source panel; MOONMOVIE_DEBUG_SCROLL scrolls.
        if (int.TryParse(Environment.GetEnvironmentVariable("MOONMOVIE_DEBUG_PANEL"), out var panelDelay))
        {
            await Task.Delay(panelDelay);
            OpenPanel();
        }
        else if (double.TryParse(Environment.GetEnvironmentVariable("MOONMOVIE_DEBUG_SCROLL"), out var offset))
        {
            await Task.Delay(1500);
            Scroller.ChangeView(null, offset, null, disableAnimation: true);
        }
#endif
    }

    // ----- Header ------------------------------------------------------------------------------------------

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DetailViewModel.IsFavorite):
                UpdateFavorite();
                break;
            case nameof(DetailViewModel.LogoUrl):
                ApplyLogo();
                break;
            case nameof(DetailViewModel.SelectedSeason):
                SyncSeasonBar();
                break;
            case "" or null:
                ApplyHeader();
                break;
        }
    }

    private void ApplyHeader()
    {
        HeroTitle.Text = ViewModel.Title;
        MetaText.Text = ViewModel.MetaLine;
        RatingText.Text = ViewModel.RatingText ?? string.Empty;
        RatingPanel.Visibility = Visible(ViewModel.RatingText is not null);

        TaglineText.Text = ViewModel.Tagline ?? string.Empty;
        TaglineText.Visibility = Visible(ViewModel.Tagline is not null);
        OverviewText.Text = ViewModel.Overview ?? string.Empty;

        DirectorLabel.Text = ViewModel.DirectorLabel;
        DirectorText.Text = ViewModel.Directors ?? "—";
        StarringText.Text = ViewModel.Starring ?? "—";
        CreditsGrid.Visibility = Visible(ViewModel.Directors is not null || ViewModel.Starring is not null);

        EpisodesRow.Visibility = Visible(ViewModel.IsSeries);
        ApplyLogo();
        UpdateFavorite();
        UpdatePlayState();
    }

    private void ApplyLogo()
    {
        var hasLogo = ViewModel.LogoUrl is not null;
        ImageEx.SetUrl(HeroLogo, ViewModel.LogoUrl);
        HeroLogo.Visibility = Visible(hasLogo);
        HeroTitle.Visibility = Visible(!hasLogo);
    }

    private void UpdateFavorite()
    {
        FavoriteIcon.Glyph = ViewModel.IsFavorite ? "" : "";
        var label = ViewModel.IsFavorite ? "已收藏" : "收藏";
        ToolTipService.SetToolTip(FavoriteButton, label);
        AutomationProperties.SetName(FavoriteButton, label);
    }

    private void OnFavoriteClick(object sender, RoutedEventArgs e) => ViewModel.ToggleFavorite();

    // ----- Play button & status line --------------------------------------------------------------------

    /// <summary>
    /// The play button always says what will happen: still searching, ready (and from where), or nothing found.
    /// </summary>
    private void UpdatePlayState()
    {
        var sources = ViewModel.Sources;
        var playable = sources.PlayableCount;

        SourceCountBadge.Visibility = Visible(sources.Items.Count > 0);
        SourceCountText.Text = sources.Items.Count.ToString();
        PanelProgress.Value = sources.Progress;
        PanelProgress.Visibility = Visible(sources.Phase == SourcePhase.Searching);
        PanelEmpty.Visibility = Visible(sources.Phase == SourcePhase.Empty && sources.Items.Count == 0);

        switch (sources.Phase)
        {
            case SourcePhase.Ready when sources.Selected is { } selected:
                PlayRing.Visibility = Visibility.Collapsed;
                PlayIcon.Visibility = Visibility.Visible;
                PlayLabel.Text = ViewModel.IsSeries && ViewModel.Episodes.Count > 0 ? "播放 第 1 集" : "播放";
                PlayButton.IsEnabled = true;

                StatusDot.Fill = selected.StateBrush;
                var others = playable - 1;
                StatusText.Text = others > 0
                    ? $"{selected.SiteName} · {selected.LatencyText} · 另有 {others} 个片源"
                    : $"{selected.SiteName} · {selected.LatencyText}";
                StatusLink.Content = "更换";
                StatusLink.Visibility = Visibility.Visible;
                PanelSubtitle.Text = sources.Phase == SourcePhase.Ready && sources.Progress < 1
                    ? $"正在检索 {sources.SitesDone}/{sources.SitesTotal} 个资源站 · {playable} 个可用"
                    : $"{sources.SitesTotal} 个资源站 · {playable} 个可用";

                if (selected.Candidate.Identity != _lastSelectedIdentity)
                {
                    _lastSelectedIdentity = selected.Candidate.Identity;
                    Pulse(StatusLine);
                }

                break;

            case SourcePhase.Empty:
                PlayRing.Visibility = Visibility.Collapsed;
                PlayIcon.Visibility = Visibility.Visible;
                PlayLabel.Text = "暂无片源";
                PlayButton.IsEnabled = false;
                StatusDot.Fill = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
                StatusText.Text = $"{sources.SitesTotal} 个资源站中没有可直接播放的版本";
                StatusLink.Content = "重新检索";
                StatusLink.Visibility = Visibility.Visible;
                PanelSubtitle.Text = "未找到片源";
                break;

            default:
                PlayRing.Visibility = Visibility.Visible;
                PlayIcon.Visibility = Visibility.Collapsed;
                PlayLabel.Text = "寻找片源";
                PlayButton.IsEnabled = true;
                StatusDot.Fill = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
                StatusText.Text = sources.Items.Count > 0
                    ? $"正在检索 {sources.SitesTotal} 个资源站 · 已找到 {sources.Items.Count} 个"
                    : $"正在检索 {sources.SitesTotal} 个资源站";
                StatusLink.Content = "查看";
                StatusLink.Visibility = Visible(sources.Items.Count > 0);
                PanelSubtitle.Text = $"正在检索 {sources.SitesDone}/{sources.SitesTotal} 个资源站";
                break;
        }
    }

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Sources.Phase == SourcePhase.Ready)
        {
            ViewModel.Play();
        }
        else
        {
            OpenPanel();
        }
    }

    private void OnStatusLinkClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Sources.Phase == SourcePhase.Empty)
        {
            ViewModel.RetrySources();
        }
        else
        {
            OpenPanel();
        }
    }

    private void OnSourcesClick(object sender, RoutedEventArgs e) => OpenPanel();

    private void OnRetryClick(object sender, RoutedEventArgs e) => ViewModel.RetrySources();

    /// <summary>Brief brightness dip so an automatic source change is noticed but never jarring.</summary>
    private static void Pulse(UIElement element)
    {
        var animation = new DoubleAnimationUsingKeyFrames { EnableDependentAnimation = false };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = 0.35 });
        animation.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = TimeSpan.FromMilliseconds(420),
            Value = 1,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    // ----- Source panel -----------------------------------------------------------------------------------

    private void OpenPanel()
    {
        if (_panelOpen)
        {
            return;
        }

        _panelOpen = true;
        SourceLayer.Visibility = Visibility.Visible;
        AnimatePanel(open: true);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (SourceRepeater.TryGetElement(0) is Control first) first.Focus(FocusState.Programmatic);
        });
    }

    private void ClosePanel()
    {
        if (!_panelOpen)
        {
            return;
        }

        _panelOpen = false;
        AnimatePanel(open: false);
        PlayButton.Focus(FocusState.Programmatic);
    }

    private void AnimatePanel(bool open)
    {
        var panel = ElementCompositionPreview.GetElementVisual(SourcePanel);
        var dimmer = ElementCompositionPreview.GetElementVisual(SourceDimmer);
        var compositor = panel.Compositor;
        var easing = open
            ? compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f))
            : compositor.CreateCubicBezierEasingFunction(new Vector2(0.7f, 0f), new Vector2(1f, 0.5f));
        var duration = open ? PanelDuration : TimeSpan.FromMilliseconds(200);
        var offset = (float)SourcePanel.ActualWidth + 24;

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(0f, new Vector3(open ? offset : 0, 0, 0));
        slide.InsertKeyFrame(1f, new Vector3(open ? 0 : offset, 0, 0), easing);
        slide.Duration = duration;
        panel.StartAnimation("Translation", slide);

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, open ? 0f : 1f);
        fade.InsertKeyFrame(1f, open ? 1f : 0f, easing);
        fade.Duration = duration;
        dimmer.StartAnimation("Opacity", fade);

        batch.End();
        if (!open)
        {
            batch.Completed += (_, _) =>
            {
                if (!_panelOpen) SourceLayer.Visibility = Visibility.Collapsed;
            };
        }
    }

    private void OnDimmerTapped(object sender, TappedRoutedEventArgs e) => ClosePanel();

    private void OnPanelClose(object sender, RoutedEventArgs e) => ClosePanel();

    private void OnSourceEscape(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_panelOpen)
        {
            ClosePanel();
            args.Handled = true;
        }
    }

    /// <summary>Rows arriving from the network slide in once; recycled rows do not animate again.</summary>
    private void OnSourceElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (ViewModel.Sources.Items.ElementAtOrDefault(args.Index) is not { } item
            || !_revealedSources.Add(item.Candidate.Identity))
        {
            return;
        }

        ElementCompositionPreview.SetIsTranslationEnabled(args.Element, true);
        var visual = ElementCompositionPreview.GetElementVisual(args.Element);
        var compositor = visual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, easing);
        fade.Duration = TimeSpan.FromMilliseconds(360);
        visual.StartAnimation("Opacity", fade);

        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(0f, new Vector3(0, 10, 0));
        slide.InsertKeyFrame(1f, Vector3.Zero, easing);
        slide.Duration = TimeSpan.FromMilliseconds(360);
        visual.StartAnimation("Translation", slide);
    }

    // ----- Seasons ----------------------------------------------------------------------------------------

    private void BuildSeasonBar()
    {
        _syncingSeasons = true;
        SeasonBar.Items.Clear();
        foreach (var season in ViewModel.Seasons)
        {
            SeasonBar.Items.Add(new SelectorBarItem { Text = $"第 {season.Number} 季", Tag = season });
        }

        SeasonBar.Visibility = Visible(ViewModel.Seasons.Count > 1);
        _syncingSeasons = false;
        SyncSeasonBar();
    }

    private void SyncSeasonBar()
    {
        _syncingSeasons = true;
        SeasonBar.SelectedItem = SeasonBar.Items.FirstOrDefault(i => i.Tag == ViewModel.SelectedSeason);
        _syncingSeasons = false;
        UpdatePlayState();
    }

    private async void OnSeasonChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (!_syncingSeasons && sender.SelectedItem?.Tag is SeasonSummary season)
        {
            await ViewModel.SelectSeasonAsync(season);
        }
    }

    // ----- Layout -----------------------------------------------------------------------------------------

    private void SetupScrollDimming()
    {
        if (_dimExpression is not null)
        {
            return;
        }

        var scroll = ElementCompositionPreview.GetScrollViewerManipulationPropertySet(Scroller);
        _dimExpression = scroll.Compositor.CreateExpressionAnimation("Clamp(1 + scroll.Translation.Y / (h * 1.1), 0.35, 1)");
        _dimExpression.SetReferenceParameter("scroll", scroll);
        _dimExpression.SetScalarParameter("h", (float)Math.Max(Hero.ActualHeight, Hero.Height));
        ElementCompositionPreview.GetElementVisual(Ambient.DimTarget).StartAnimation("Opacity", _dimExpression);

        // The copy fades before it can slide under the title bar.
        _fadeExpression = scroll.Compositor.CreateExpressionAnimation("Clamp(1 + scroll.Translation.Y / (h * 0.55), 0, 1)");
        _fadeExpression.SetReferenceParameter("scroll", scroll);
        _fadeExpression.SetScalarParameter("h", (float)Math.Max(Hero.ActualHeight, Hero.Height));
        ElementCompositionPreview.GetElementVisual(HeroCopy).StartAnimation("Opacity", _fadeExpression);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        Hero.Height = Math.Clamp(e.NewSize.Height * 0.84, 560, 1040);
        if (_dimExpression is not null && _fadeExpression is not null)
        {
            _dimExpression.SetScalarParameter("h", (float)Hero.Height);
            _fadeExpression.SetScalarParameter("h", (float)Hero.Height);
            ElementCompositionPreview.GetElementVisual(Ambient.DimTarget).StartAnimation("Opacity", _dimExpression);
            ElementCompositionPreview.GetElementVisual(HeroCopy).StartAnimation("Opacity", _fadeExpression);
        }
    }

    private static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
}

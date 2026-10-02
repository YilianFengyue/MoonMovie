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
using MoonMovie.Animations;
using MoonMovie.Imaging;
using MoonMovie.ViewModels;

namespace MoonMovie.Views;

public sealed partial class DetailPage : Page
{
    private static readonly TimeSpan PanelDuration = TimeSpan.FromMilliseconds(320);

    private readonly HashSet<string> _revealedSources = [];
    private ExpressionAnimation? _dimExpression;

    private bool _panelOpen;
    private bool _syncingSeasons;
    private string? _lastSelectedIdentity;
    private CancellationTokenSource? _autoPlayCts;

    public DetailPage()
    {
        ViewModel = App.Services.GetRequiredService<DetailViewModel>();
        InitializeComponent();

        Scroller.ViewChanged += OnScrollViewChanged;
        SizeChanged += OnSizeChanged;
        Loaded += (_, _) => SetupScrollDimming();

        ViewModel.PropertyChanged += OnViewModelChanged;
        ViewModel.Sources.PropertyChanged += (_, _) => UpdatePlayState();
        ViewModel.Sources.Items.CollectionChanged += (_, _) => UpdatePlayState();
        ViewModel.Sources.Chosen += (_, _) => ClosePanel();
        ViewModel.DownloadQueued += ConfirmDownload;
        ViewModel.Seasons.CollectionChanged += (_, _) => BuildSeasonBar();
        ViewModel.Cast.CollectionChanged += (_, _) => CastRow.Visibility = Visible(ViewModel.Cast.Count > 0);
        ViewModel.Recommendations.CollectionChanged += (_, _) =>
            RecommendationsRow.Visibility = Visible(ViewModel.Recommendations.Count > 0);
        ViewModel.BiliVideos.CollectionChanged += (_, _) =>
        {
            // Keep the row (and its sort bar) while a re-sort is loading; hide it only when B站 has nothing at all.
            if (ViewModel.BiliVideos.Count > 0) BiliRow.Visibility = Visibility.Visible;
            else if (ViewModel.BiliOrder == Core.Bilibili.BiliOrder.Relevance) BiliRow.Visibility = Visibility.Collapsed;
        };
    }

    public DetailViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not Services.DetailArgs args)
        {
            return;
        }

        var load = ViewModel.LoadAsync(args.Item, args.Season);
        ApplyHeader();
        Ambient.Show(ViewModel.BackdropUrl, ViewModel.PreviewUrl);
        UpdatePlayState();
        await load;

        // "继续观看" opens the page and starts playing as soon as a source is ready (only on a fresh visit,
        // never when coming back from the player).
        if (args.AutoPlay && e.NavigationMode == NavigationMode.New)
        {
            _autoPlayCts = new CancellationTokenSource();
            await AutoPlayAsync(_autoPlayCts.Token);
            return;
        }

#if DEBUG
        // Visual QA hooks: MOONMOVIE_DEBUG_PANEL=<ms> opens the source panel; MOONMOVIE_DEBUG_SCROLL scrolls.
        if (Environment.GetEnvironmentVariable("MOONMOVIE_DEBUG_PLAY") is "1")
        {
            for (var i = 0; i < 60 && ViewModel.Sources.Phase != SourcePhase.Ready; i++) await Task.Delay(250);
            await Task.Delay(1500); // let a few probes finish so the pick is the fastest
            ViewModel.Play();
        }
        else if (int.TryParse(Environment.GetEnvironmentVariable("MOONMOVIE_DEBUG_PANEL"), out var panelDelay))
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

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _autoPlayCts?.Cancel();
    }

    private async Task AutoPlayAsync(CancellationToken ct)
    {
        var sources = ViewModel.Sources;
        try
        {
            for (var i = 0; i < 160 && sources.Phase is not (SourcePhase.Ready or SourcePhase.Empty); i++)
            {
                await Task.Delay(250, ct);
            }

            if (sources.Phase != SourcePhase.Ready) return;
            await Task.Delay(700, ct); // let the first latency probes land so the pick is a fast one
            if (!_panelOpen) ViewModel.Play();
        }
        catch (OperationCanceledException)
        {
        }
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
            case nameof(DetailViewModel.Douban):
                ApplyDouban();
                break;
            case nameof(DetailViewModel.Official):
                ApplyOfficial();
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

    // ----- 豆瓣 / B站 ----------------------------------------------------------------------------------------

    /// <summary>「B站正版 · 免费」: the title is on B站; tells whether 大会员 is needed for (some of) it.</summary>
    private void ApplyOfficial()
    {
        var official = ViewModel.Official;
        OfficialBadge.Visibility = Visible(official is not null);
        if (official is null) return;
        var remarks = official.Candidate.Remarks;
        var vip = App.Services.GetRequiredService<Services.BiliAccountService>().Account?.IsVip == true;
        OfficialText.Text = remarks switch
        {
            "免费" => "B站正版 · 免费",
            _ when vip => "B站正版 · 大会员可看",
            "大会员" => "B站正版 · 大会员",
            _ => "B站正版 · 部分大会员",
        };
        var tip = remarks switch
        {
            "免费" => "哔哩哔哩正版，全部免费观看",
            _ when vip => "哔哩哔哩正版，你的大会员可以看全部集数",
            "大会员" => "哔哩哔哩正版，需要大会员",
            _ => $"哔哩哔哩正版，{remarks}；没有大会员时这些集会自动换用其他片源",
        };
        ToolTipService.SetToolTip(OfficialBadge, tip);
        AutomationProperties.SetName(OfficialBadge, tip);
    }

    private void ApplyDouban()
    {
        var rating = ViewModel.Douban;
        DoubanButton.Visibility = Visible(rating is not null);
        if (rating is null) return;
        DoubanText.Text = rating.Value.ToString("0.0");
        var people = rating.Count >= 10_000 ? $"{rating.Count / 10_000.0:0.#} 万" : rating.Count.ToString();
        var label = $"豆瓣 {rating.Value:0.0} · {people}人评价";
        ToolTipService.SetToolTip(DoubanButton, label + "，点击打开豆瓣页面");
        AutomationProperties.SetName(DoubanButton, label);
    }

    private void OnDoubanClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Douban is { } rating) _ = Windows.System.Launcher.LaunchUriAsync(new Uri(rating.Url));
    }

    private void OnBiliOrderChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem?.Tag is string tag && Enum.TryParse<Core.Bilibili.BiliOrder>(tag, out var order))
        {
            ViewModel.BiliOrder = order;
        }
    }

    private void OnBiliMoreClick(object sender, RoutedEventArgs e) =>
        _ = Windows.System.Launcher.LaunchUriAsync(new Uri(ViewModel.BiliSearchUrl));

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
                PlayLabel.Text = ViewModel.ResumeLabel
                                 ?? (ViewModel.IsSeries && ViewModel.Episodes.Count > 0 ? "播放 第 1 集" : "播放");
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

    // ----- Download -----------------------------------------------------------------------------------------

    /// <summary>Series: this episode, from here on, or the whole season; films: each version.</summary>
    private void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom };
        if (ViewModel.DownloadSource is not { } source)
        {
            flyout.Items.Add(new MenuFlyoutItem
            {
                Text = ViewModel.Sources.Phase == SourcePhase.Searching ? "正在寻找可下载的片源…" : "没有可下载的在线片源",
                IsEnabled = false,
            });
        }
        else
        {
            var episodes = source.Candidate.PrimaryLine.Episodes;
            if (ViewModel.IsSeries)
            {
                var next = Math.Clamp(ViewModel.NextIndex, 0, episodes.Count - 1);
                AddDownload(flyout, $"下载第 {next + 1} 集", [next]);
                if (next > 0 && next < episodes.Count - 1)
                {
                    AddDownload(flyout, $"下载第 {next + 1}–{episodes.Count} 集", Enumerable.Range(next, episodes.Count - next));
                }

                AddDownload(flyout, $"下载本季全部（{episodes.Count} 集）", Enumerable.Range(0, episodes.Count));
            }
            else if (episodes.Count == 1)
            {
                AddDownload(flyout, "下载正片", [0]);
            }
            else
            {
                for (var i = 0; i < episodes.Count; i++) AddDownload(flyout, $"下载 {episodes[i].Name}", [i]);
            }

            flyout.Items.Add(new MenuFlyoutSeparator());
            var from = new MenuFlyoutItem { Text = $"片源：{source.SiteName}", IsEnabled = false };
            flyout.Items.Add(from);
        }

        var manage = new MenuFlyoutItem { Text = "管理下载…" };
        manage.Click += (_, _) => App.MainWindow.Navigate(typeof(DownloadsPage), null);
        flyout.Items.Add(manage);
        flyout.ShowAt(DownloadButton);
    }

    private void AddDownload(MenuFlyout flyout, string text, IEnumerable<int> indexes)
    {
        var list = indexes.ToArray();
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => ConfirmDownload(ViewModel.Download(list));
        flyout.Items.Add(item);
    }

    /// <summary>The button itself says what happened for a moment.</summary>
    private async void ConfirmDownload(int added)
    {
        DownloadLabel.Text = added > 0 ? $"已加入下载（{added}）" : "已在下载列表中";
        DownloadIcon.Glyph = "\uE73E";
        await Task.Delay(2400);
        DownloadLabel.Text = "下载";
        DownloadIcon.Glyph = "\uE896";
    }

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

    // Storyboards only: the panel hosts controls (see Animations/Motion.cs for why composition is avoided here).
    private async void AnimatePanel(bool open)
    {
        var duration = open ? PanelDuration : TimeSpan.FromMilliseconds(200);
        var offset = (SourcePanel.ActualWidth > 0 ? SourcePanel.ActualWidth : SourcePanel.Width) + 24;

        Motion.FadeTo(SourceDimmer, open ? 1 : 0, duration);
        await Motion.SlideFadeAsync(SourcePanel, open ? offset : 0, open ? 0 : offset, 0, 0, null, 1, duration, decelerate: open);

        if (!open && !_panelOpen)
        {
            SourceLayer.Visibility = Visibility.Collapsed;
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

        _ = Motion.SlideFadeAsync(args.Element, 0, 0, 10, 0, 0, 1, TimeSpan.FromMilliseconds(360));
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
    }

    /// <summary>The copy fades before it can slide under the title bar (plain property: it hosts the buttons).</summary>
    private void OnScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) =>
        HeroCopy.Opacity = Math.Clamp(1 - Scroller.VerticalOffset / (Hero.Height * 0.55), 0, 1);

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        Hero.Height = Math.Clamp(e.NewSize.Height * 0.84, 560, 1040);
        if (_dimExpression is not null)
        {
            _dimExpression.SetScalarParameter("h", (float)Hero.Height);
            ElementCompositionPreview.GetElementVisual(Ambient.DimTarget).StartAnimation("Opacity", _dimExpression);
        }
    }

    private static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
}

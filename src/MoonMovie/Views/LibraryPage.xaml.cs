using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using MoonMovie.Core.Models;
using MoonMovie.Services;
using MoonMovie.ViewModels;

namespace MoonMovie.Views;

/// <summary>媒体库: continue watching, history and favourites, all local.</summary>
public sealed partial class LibraryPage : Page
{
    private LibraryTab _tab;
    private bool _isActive;
    private bool _clearArmed;
    private DispatcherQueueTimer? _disarmTimer;

    public LibraryPage()
    {
        ViewModel = App.Services.GetRequiredService<LibraryViewModel>();
        InitializeComponent();
        ViewModel.PropertyChanged += (_, e) =>
        {
            // Refresh() (after a removal or clear) announces everything at once.
            if (string.IsNullOrEmpty(e.PropertyName) && _isActive) ShowTab(_tab);
        };
        WeakReferenceMessenger.Default.Register<LibraryPage, AmbientRequest>(this, static (page, msg) => page.OnAmbientRequest(msg));
    }

    public LibraryViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Refresh();
        _isActive = true;

        var tab = e.Parameter is LibraryTab t ? t : LibraryTab.Continue;
        if (e.NavigationMode == NavigationMode.New && tab == LibraryTab.Continue && ViewModel.Continue.Count == 0)
        {
            // Nothing to continue: land somewhere with content instead of an empty state.
            tab = ViewModel.HistoryCount > 0 ? LibraryTab.History
                : ViewModel.FavoriteCount > 0 ? LibraryTab.Favorites
                : LibraryTab.Continue;
        }

        Tabs.SelectedItem = tab switch
        {
            LibraryTab.History => HistoryTab,
            LibraryTab.Favorites => FavoritesTab,
            _ => ContinueTab,
        };
        ShowTab(tab);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _isActive = false;
    }

    private void OnTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem?.Tag is string tag && Enum.TryParse<LibraryTab>(tag, out var tab))
        {
            ShowTab(tab);
        }
    }

    private void ShowTab(LibraryTab tab)
    {
        _tab = tab;
        ContinueRepeater.Visibility = Visible(tab == LibraryTab.Continue);
        HistoryHost.Visibility = Visible(tab == LibraryTab.History);
        FavoritesRepeater.Visibility = Visible(tab == LibraryTab.Favorites);
        ClearHistoryButton.Visibility = Visible(tab == LibraryTab.History && ViewModel.HistoryCount > 0);
        FavoriteFilterBar.Visibility = Visible(tab == LibraryTab.Favorites && ViewModel.FavoriteCount > 0);
        Disarm();
        UpdateEmptyState();
        UpdateAmbient();
    }

    private void UpdateEmptyState()
    {
        var (empty, glyph, title, caption) = _tab switch
        {
            LibraryTab.History => (ViewModel.History.Count == 0, "", "还没有观看记录", "看过的影片和剧集会按时间出现在这里"),
            LibraryTab.Favorites => (ViewModel.Favorites.Count == 0, "",
                ViewModel.FavoriteCount == 0 ? "还没有收藏" : "这个分类里还没有收藏",
                "在详情页点「收藏」，想看的片子就会留在这里"),
            _ => (ViewModel.Continue.Count == 0, "", "没有正在看的内容", "看了一半的电影和追到一半的剧会出现在这里，点一下接着看"),
        };

        EmptyState.Visibility = Visible(empty);
        EmptyGlyph.Glyph = glyph;
        EmptyTitle.Text = title;
        EmptyCaption.Text = caption;
    }

    /// <summary>The newest thing on the tab sets the mood; hovering an item takes over.</summary>
    private void UpdateAmbient()
    {
        var url = _tab switch
        {
            LibraryTab.Continue => ViewModel.Continue.FirstOrDefault(i => i.AmbientUrl is not null)?.AmbientUrl,
            LibraryTab.History => ViewModel.History.SelectMany(g => g.Items).FirstOrDefault(i => i.AmbientUrl is not null)?.AmbientUrl,
            _ => ViewModel.Favorites.FirstOrDefault(i => i.AmbientUrl is not null)?.AmbientUrl,
        };
        Ambient.Show(url);
    }

    private void OnAmbientRequest(AmbientRequest request)
    {
        if (_isActive) Ambient.Show(request.Url);
    }

    private void OnRowPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: WatchedItemViewModel { AmbientUrl: { } url } })
        {
            Ambient.Show(url);
        }
    }

    private void OnFavoriteFilterChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        ViewModel.FavoriteFilter = sender.SelectedItem?.Tag switch
        {
            "Movie" => MediaKind.Movie,
            "Tv" => MediaKind.Tv,
            _ => null,
        };
        UpdateEmptyState();
    }

    /// <summary>Two-step clear: the first click asks, a second click within a few seconds confirms.</summary>
    private void OnClearHistory(object sender, RoutedEventArgs e)
    {
        if (!_clearArmed)
        {
            _clearArmed = true;
            ClearHistoryText.Text = "再点一次确认清空";
            _disarmTimer ??= CreateDisarmTimer();
            _disarmTimer.Start();
            return;
        }

        Disarm();
        ViewModel.ClearHistory();
        ShowTab(_tab);
    }

    private DispatcherQueueTimer CreateDisarmTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(4);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => Disarm();
        return timer;
    }

    private void Disarm()
    {
        _clearArmed = false;
        _disarmTimer?.Stop();
        ClearHistoryText.Text = "清空历史";
    }

    private void OnBrowseHome(object sender, RoutedEventArgs e) => App.MainWindow.NavigateHome();

    private static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
}

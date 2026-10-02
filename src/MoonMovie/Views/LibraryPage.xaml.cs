using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using MoonMovie.Core.Models;
using MoonMovie.Core.Local;
using MoonMovie.Services;
using MoonMovie.ViewModels;

namespace MoonMovie.Views;

/// <summary>媒体库: continue watching, history, favourites and the files on this PC.</summary>
public sealed partial class LibraryPage : Page
{
    private LibraryTab _tab;
    private bool _isActive;
    private bool _clearArmed;
    private DispatcherQueueTimer? _disarmTimer;
    private LocalTitleViewModel? _matching;
    private int _matchVersion;

    public LibraryPage()
    {
        ViewModel = App.Services.GetRequiredService<LibraryViewModel>();
        Local = App.Services.GetRequiredService<LocalLibraryViewModel>();
        InitializeComponent();
        Local.Changed += () =>
        {
            if (_isActive && _tab == LibraryTab.Local) ShowTab(_tab);
        };
        Local.FixMatchRequested += item => _ = ShowMatchDialogAsync(item);
        ViewModel.PropertyChanged += (_, e) =>
        {
            // Refresh() (after a removal or clear) announces everything at once.
            if (string.IsNullOrEmpty(e.PropertyName) && _isActive) ShowTab(_tab);
        };
        WeakReferenceMessenger.Default.Register<LibraryPage, AmbientRequest>(this, static (page, msg) => page.OnAmbientRequest(msg));
    }

    public LibraryViewModel ViewModel { get; }

    public LocalLibraryViewModel Local { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Refresh();
        Local.Attach();
        Local.Refresh();
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
            LibraryTab.Local => LocalTab,
            _ => ContinueTab,
        };
        ShowTab(tab);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _isActive = false;
        Local.Detach();
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
        LocalHost.Visibility = Visible(tab == LibraryTab.Local && Local.Folders.Count + Local.Titles.Count > 0);
        LocalTools.Visibility = Visible(tab == LibraryTab.Local);
        RescanButton.Visibility = Visible(Local.Folders.Count > 0);
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
            LibraryTab.Local => (Local.Titles.Count == 0, Local.Folders.Count == 0 ? "" : "",
                Local.Folders.Count == 0 ? "还没有本地影片" : Local.IsScanning ? "正在扫描文件夹…" : "文件夹里没有找到视频",
                Local.Folders.Count == 0
                    ? "添加存放电影、剧集的文件夹，MoonMovie 会认出片名、配上海报和简介。也可以直接把视频拖进窗口播放"
                    : "支持 mkv、mp4、avi、ts、rmvb 等常见格式，以及蓝光、DVD 目录"),
            _ => (ViewModel.Continue.Count == 0, "", "没有正在看的内容", "看了一半的电影和追到一半的剧会出现在这里，点一下接着看"),
        };

        EmptyState.Visibility = Visible(empty);
        EmptyActionText.Text = _tab == LibraryTab.Local ? "添加文件夹" : "去首页逛逛";
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
            LibraryTab.Local => Local.Titles.FirstOrDefault(i => i.AmbientUrl is not null)?.AmbientUrl,
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
        timer.Tick += (_, _) => SafeDispatch.Run(Disarm);
        return timer;
    }

    private void Disarm()
    {
        _clearArmed = false;
        _disarmTimer?.Stop();
        ClearHistoryText.Text = "清空历史";
    }

    private async void OnEmptyAction(object sender, RoutedEventArgs e)
    {
        if (_tab == LibraryTab.Local) await Local.AddFolderAsync();
        else App.MainWindow.NavigateHome();
    }

    // ----- 本地 ---------------------------------------------------------------------------------------------

    private async void OnOpenFile(object sender, RoutedEventArgs e) => await LocalPlayback.PickAndOpenAsync();

    private async void OnAddFolder(object sender, RoutedEventArgs e) => await Local.AddFolderAsync();

    private async void OnRescan(object sender, RoutedEventArgs e) => await Local.RescanAsync();

    /// <summary>Search TMDB by hand for a title the file names did not identify (or identified wrongly).</summary>
    private async Task ShowMatchDialogAsync(LocalTitleViewModel item)
    {
        _matching = item;
        var title = item.Model;
        MatchFileText.Text = title.Files.Count == 1 ? title.Files[0].FileName : $"{title.Files[0].FileName} 等 {title.Files.Count} 个文件";
        MatchQuery.Text = title.ParsedTitle;
        MatchKind.SelectedItem = title.Kind == MediaKind.Tv ? MatchTv : MatchMovie; // triggers the first search
        MatchDialog.IsPrimaryButtonEnabled = false;
        MatchDialog.SecondaryButtonText = title.IsMatched ? "设为未识别" : string.Empty;
        await SearchMatchesAsync();

        var result = await MatchDialog.ShowAsync();
        if (_matching is null) return;
        if (result == ContentDialogResult.Primary && MatchResults.SelectedItem is MediaCardViewModel chosen)
        {
            Local.ApplyMatch(title, chosen.Item);
        }
        else if (result == ContentDialogResult.Secondary)
        {
            Local.ApplyMatch(title, null);
        }

        _matching = null;
    }

    private async Task SearchMatchesAsync()
    {
        var version = ++_matchVersion;
        var kind = MatchKind.SelectedItem == MatchTv ? MediaKind.Tv : MediaKind.Movie;
        MatchRing.IsActive = true;
        MatchEmpty.Visibility = Visibility.Collapsed;
        MatchResults.ItemsSource = null;
        var results = await Local.SearchAsync(MatchQuery.Text, kind);
        if (version != _matchVersion) return;
        MatchRing.IsActive = false;
        MatchResults.ItemsSource = results;
        MatchEmpty.Visibility = Visible(results.Count == 0);
        if (results.Count > 0) MatchResults.SelectedIndex = 0;
    }

    private async void OnMatchQueryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        await SearchMatchesAsync();
    }

    private async void OnMatchKindChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_matching is not null && MatchDialog.IsLoaded) await SearchMatchesAsync();
    }

    private void OnMatchSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        MatchDialog.IsPrimaryButtonEnabled = MatchResults.SelectedItem is not null;

    private static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
}

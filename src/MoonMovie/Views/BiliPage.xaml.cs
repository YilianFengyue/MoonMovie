using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using MoonMovie.Core.Bilibili;
using MoonMovie.Services;
using MoonMovie.ViewModels;

namespace MoonMovie.Views;

/// <summary>「B站」: 番剧影视 (新番时间表 + 片库), 视频 (热门 / 排行榜), 我的 (the linked account).</summary>
public sealed partial class BiliPage : Page
{
    private readonly BiliAccountService _account = App.Services.GetRequiredService<BiliAccountService>();
    private bool _syncingTab;

    public BiliPage()
    {
        ViewModel = App.Services.GetRequiredService<BiliHubViewModel>();
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelChanged;
        ViewModel.Timeline.CollectionChanged += (_, _) => SyncPanels();
        ViewModel.Videos.CollectionChanged += (_, _) => SyncLoading();
        foreach (var list in new System.Collections.Specialized.INotifyCollectionChanged[]
                 { ViewModel.Following, ViewModel.Dramas, ViewModel.WatchLater, ViewModel.FolderItems })
        {
            list.CollectionChanged += (_, _) => SyncPanels();
        }
    }

    public BiliHubViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _account.Changed += OnAccountChanged;
        _syncingTab = true;
        TabBar.SelectedItem = TabBar.Items.FirstOrDefault(i => (string)i.Tag == ViewModel.Tab.ToString()) ?? TabBar.Items[0];
        _syncingTab = false;
        SyncPanels();
        SyncLoading();
        await ViewModel.EnsureLoadedAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _account.Changed -= OnAccountChanged;
    }

    private void OnAccountChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        ViewModel.AccountChanged();
        SyncPanels();
        _ = ViewModel.EnsureLoadedAsync();
    });

    private async void OnTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_syncingTab || sender.SelectedItem?.Tag is not string tag || !Enum.TryParse<BiliHubTab>(tag, out var tab)) return;
        ViewModel.Tab = tab;
        Scroller.ChangeView(null, 0, null, disableAnimation: true); // each tab starts at its top
        SyncPanels();
        SyncLoading();
        await ViewModel.EnsureLoadedAsync();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(BiliHubViewModel.IsLibraryLoading):
            case nameof(BiliHubViewModel.IsVideosLoading):
            case nameof(BiliHubViewModel.IsMineLoading):
            case nameof(BiliHubViewModel.LibraryMessage):
                SyncLoading();
                SyncPanels();
                break;
            case nameof(BiliHubViewModel.IsSignedIn):
            case nameof(BiliHubViewModel.FolderGroup):
                SyncPanels();
                break;
        }
    }

    /// <summary>One tab's panel at a time; empty rows hide; 我的 asks to link the account first.</summary>
    private void SyncPanels()
    {
        ShowsPanel.Visibility = Visible(ViewModel.Tab == BiliHubTab.Shows);
        VideosPanel.Visibility = Visible(ViewModel.Tab == BiliHubTab.Videos);
        MinePanel.Visibility = Visible(ViewModel.Tab == BiliHubTab.Mine);
        TimelineRow.Visibility = Visible(ViewModel.Timeline.Count > 0);

        var signedIn = ViewModel.IsSignedIn;
        LinkCard.Visibility = Visible(!signedIn);
        MineContent.Visibility = Visible(signedIn);
        FollowingRow.Visibility = Visible(ViewModel.Following.Count > 0);
        DramasRow.Visibility = Visible(ViewModel.Dramas.Count > 0);
        WatchLaterRow.Visibility = Visible(ViewModel.WatchLater.Count > 0);
        FolderSection.Visibility = Visible(ViewModel.FolderGroup is not null);
        MineEmpty.Visibility = Visible(signedIn && !ViewModel.IsMineLoading && ViewModel.Following.Count + ViewModel.Dramas.Count
            + ViewModel.WatchLater.Count == 0 && ViewModel.FolderGroup is null);
    }

    private void SyncLoading()
    {
        var loading = ViewModel.Tab switch
        {
            BiliHubTab.Shows => ViewModel.IsLibraryLoading,
            BiliHubTab.Videos => ViewModel.IsVideosLoading,
            _ => ViewModel.IsMineLoading,
        };
        LoadingRing.IsActive = loading;
        LoadingRing.Visibility = Visible(loading);
        var message = ViewModel.Tab == BiliHubTab.Shows ? ViewModel.LibraryMessage : null;
        MessageText.Text = message ?? string.Empty;
        MessageText.Visibility = Visible(!loading && message is not null);
    }

    private void OnScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (Scroller.VerticalOffset + Scroller.ViewportHeight * 2 > Scroller.ExtentHeight) _ = ViewModel.LoadMoreAsync();
    }

    private async void OnLinkClick(object sender, RoutedEventArgs e) => await Controls.BiliLoginDialog.ShowAsync(XamlRoot);

    private static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
}

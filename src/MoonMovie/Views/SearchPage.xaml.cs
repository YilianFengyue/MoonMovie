using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using MoonMovie.Services;
using MoonMovie.ViewModels;

namespace MoonMovie.Views;

public sealed partial class SearchPage : Page
{
    private CancellationTokenSource? _cts;

    public SearchPage()
    {
        ViewModel = App.Services.GetRequiredService<SearchViewModel>();
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelChanged;
        ViewModel.People.CollectionChanged += (_, _) => SyncPanels();
        Scroller.ViewChanged += OnScrollViewChanged;
        ViewModel.Results.CollectionChanged += (_, _) =>
        {
            // The first result sets the mood until the user points at another.
            if (ViewModel.Results.Count == 1 || ViewModel.Results.Count > 0 && Ambient.CurrentUrl is null)
            {
                Ambient.Show(ViewModel.Results.FirstOrDefault(r => r.AmbientUrl is not null)?.AmbientUrl);
            }
        };
    }

    public SearchViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not string query)
        {
            return;
        }

        QueryText.Text = $"“{query}”";
        _cts = new CancellationTokenSource();
#if DEBUG
        // QA hook: MOONMOVIE_DEBUG_SEARCH_TAB=bili opens on the 「B站」 tab.
        if (Environment.GetEnvironmentVariable("MOONMOVIE_DEBUG_SEARCH_TAB") == "bili") FilterBar.SelectedItem = FilterBar.Items[^1];
#endif
        await ViewModel.RunAsync(query, _cts.Token);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        if (e.NavigationMode == NavigationMode.Back) _cts?.Cancel();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SearchViewModel.IsLoading):
            case nameof(SearchViewModel.IsBiliLoading):
                SyncLoading();
                break;
            case nameof(SearchViewModel.Summary):
                SummaryText.Text = ViewModel.Summary;
                break;
        }
    }

    private void OnResultPointerEntered(object sender, PointerRoutedEventArgs e) => PreviewArtwork(sender);

    private void OnResultFocused(object sender, RoutedEventArgs e) => PreviewArtwork(sender);

    private void PreviewArtwork(object sender)
    {
        if (sender is FrameworkElement { Tag: SearchResultViewModel { AmbientUrl: { } url } })
        {
            Ambient.Show(url);
        }
    }

    private void OnFilterChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem?.Tag is string tag && Enum.TryParse<SearchFilter>(tag, out var filter) && filter != ViewModel.Filter)
        {
            ViewModel.ApplyFilter(filter);
            SyncPanels();
            SyncLoading();
        }
    }

    private void OnBiliOrderChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem?.Tag is string tag && Enum.TryParse<Core.Bilibili.BiliOrder>(tag, out var order))
        {
            ViewModel.SetBiliOrder(order);
        }
    }

    /// <summary>The 「B站」 tab swaps the TMDB results (and people) for the video grid.</summary>
    private void SyncPanels()
    {
        var bili = ViewModel.Filter == SearchFilter.Bili;
        BiliPanel.Visibility = bili ? Visibility.Visible : Visibility.Collapsed;
        ResultsPanel.Visibility = bili ? Visibility.Collapsed : Visibility.Visible;
        PeopleRow.Visibility = !bili && ViewModel.People.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The header ring covers a first load; later B站 pages show a ring under the grid.</summary>
    private void SyncLoading()
    {
        var bili = ViewModel.Filter == SearchFilter.Bili;
        var first = bili ? ViewModel.IsBiliLoading && ViewModel.BiliVideos.Count == 0 : ViewModel.IsLoading;
        LoadingRing.Visibility = first ? Visibility.Visible : Visibility.Collapsed;
        var more = bili && ViewModel.IsBiliLoading && ViewModel.BiliVideos.Count > 0;
        BiliMoreRing.IsActive = more;
        BiliMoreRing.Visibility = more ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (ViewModel.Filter == SearchFilter.Bili && ViewModel.BiliVideos.Count > 0
            && Scroller.VerticalOffset + Scroller.ViewportHeight * 2 > Scroller.ExtentHeight)
        {
            _ = ViewModel.LoadBiliAsync();
        }
    }
}

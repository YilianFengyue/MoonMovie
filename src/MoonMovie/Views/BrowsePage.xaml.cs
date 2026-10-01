using System.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using MoonMovie.Animations;
using MoonMovie.Core.Browse;
using MoonMovie.Services;
using MoonMovie.ViewModels;

namespace MoonMovie.Views;

/// <summary>电影 / 剧集 / 动漫: filter rows over an endless poster wall.</summary>
public sealed partial class BrowsePage : Page
{
    private bool _isActive;
    private bool _stickyShown;
    private bool _fadeInPending;

    public BrowsePage()
    {
        InitializeComponent();
        WeakReferenceMessenger.Default.Register<BrowsePage, AmbientRequest>(this, static (page, msg) => page.OnAmbientRequest(msg));
    }

    // Set on navigation (the section is the parameter); x:Bind tolerates null until Bindings.Update().
    public BrowseViewModel ViewModel { get; private set; } = null!;

    public BrowseSection Section => ViewModel.Section;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var section = e.Parameter is BrowseSection s ? s : BrowseSection.Movie;
        ViewModel = App.Services.GetRequiredService<BrowseSections>().Get(section);
        Bindings.Update();
        _isActive = true;

        ViewModel.PropertyChanged += OnViewModelChanged;
        ViewModel.Resetting += OnResetting;
        ViewModel.Items.CollectionChanged += OnItemsChanged;
        StickySummary.Text = ViewModel.SelectionSummary;
        UpdateStatus();

        if (ViewModel.Items.FirstOrDefault(i => i.AmbientUrl is not null) is { } first)
        {
            Ambient.Show(first.AmbientUrl);
        }

        if (ViewModel.ScrollOffset > 0)
        {
            // Back from a detail page: put the wall where it was once it has been measured.
            var offset = ViewModel.ScrollOffset;
            await Task.Delay(30);
            Scroller.UpdateLayout();
            Scroller.ChangeView(null, offset, null, disableAnimation: true);
        }

        await ViewModel.EnsureLoadedAsync();
        await FillViewportAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _isActive = false;
        ViewModel.ScrollOffset = Scroller.VerticalOffset;
        ViewModel.PropertyChanged -= OnViewModelChanged;
        ViewModel.Resetting -= OnResetting;
        ViewModel.Items.CollectionChanged -= OnItemsChanged;
    }

    /// <summary>Tall windows can show more than one page of results; keep loading until it scrolls.</summary>
    private async Task FillViewportAsync()
    {
        for (var i = 0; i < 4 && _isActive; i++)
        {
            Scroller.UpdateLayout();
            if (Scroller.ExtentHeight > Scroller.VerticalOffset + Scroller.ViewportHeight * 2) return;
            var before = ViewModel.Items.Count;
            await ViewModel.LoadMoreAsync();
            if (ViewModel.Items.Count == before) return;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(BrowseViewModel.IsLoading):
            case nameof(BrowseViewModel.Message):
                UpdateStatus();
                break;
            case nameof(BrowseViewModel.SelectionSummary):
                StickySummary.Text = ViewModel.SelectionSummary;
                break;
        }
    }

    private void UpdateStatus()
    {
        LoadingRing.Visibility = ViewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        MessageText.Text = ViewModel.Message ?? string.Empty;
        MessageText.Visibility = ViewModel.Message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>A new filter: the old wall fades out, the new one fades in with its first page.</summary>
    private void OnResetting(object? sender, EventArgs e)
    {
        WallRepeater.Opacity = 0;
        _fadeInPending = true;
        if (Scroller.VerticalOffset > Header.ActualHeight)
        {
            Scroller.ChangeView(null, 0, null);
        }
    }

    private void OnItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (ViewModel.Items.Count == 0) return;

        var fresh = _fadeInPending;
        if (fresh)
        {
            _fadeInPending = false;
            Motion.FadeTo(WallRepeater, 1, TimeSpan.FromMilliseconds(360));
            _ = FillViewportAsync();
        }

        // A new selection gets a new mood: the top result's artwork.
        if ((fresh || Ambient.CurrentUrl is null) && ViewModel.Items[0].AmbientUrl is { } url)
        {
            Ambient.Show(url);
        }
    }

    private void OnAmbientRequest(AmbientRequest request)
    {
        if (_isActive) Ambient.Show(request.Url);
    }

    private void OnScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        var offset = Scroller.VerticalOffset;

        // The bar takes over once the last filter row has passed under the title bar.
        var showSticky = offset > Header.ActualHeight + 104 - 112;
        if (showSticky != _stickyShown)
        {
            _stickyShown = showSticky;
            if (showSticky) StickyBar.Visibility = Visibility.Visible;
            Motion.FadeTo(StickyBar, showSticky ? 1 : 0, TimeSpan.FromMilliseconds(showSticky ? 180 : 140));
            StickyBar.IsHitTestVisible = showSticky;
        }

        if (offset + Scroller.ViewportHeight * 2 > Scroller.ExtentHeight)
        {
            _ = ViewModel.LoadMoreAsync();
        }
    }

    private void OnShowFilters(object sender, RoutedEventArgs e) => Scroller.ChangeView(null, 0, null);
}

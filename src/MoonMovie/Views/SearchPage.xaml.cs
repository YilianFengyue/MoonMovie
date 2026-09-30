using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using MoonMovie.Core.Tmdb;
using MoonMovie.Imaging;
using MoonMovie.Services;
using MoonMovie.ViewModels;

namespace MoonMovie.Views;

public sealed partial class SearchPage : Page
{
    private readonly TmdbClient _tmdb = App.Services.GetRequiredService<TmdbClient>();
    private CancellationTokenSource? _cts;

    public SearchPage()
    {
        ViewModel = App.Services.GetRequiredService<SearchViewModel>();
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelChanged;
        ViewModel.People.CollectionChanged += (_, _) =>
            PeopleRow.Visibility = ViewModel.People.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public SearchViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not string query || _cts is not null)
        {
            return; // navigating back restores the page from the frame without re-running
        }

        QueryText.Text = $"“{query}”";
        _cts = new CancellationTokenSource();
        await ViewModel.RunAsync(query, _cts.Token);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        if (e.NavigationMode != NavigationMode.New) _cts?.Cancel();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SearchViewModel.IsLoading):
                LoadingRing.Visibility = ViewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;
                break;
            case nameof(SearchViewModel.Summary):
                SummaryText.Text = ViewModel.Summary;
                break;
            case nameof(SearchViewModel.BestMatch):
                ApplyBestMatch();
                break;
        }
    }

    private void ApplyBestMatch()
    {
        var best = ViewModel.BestMatch;
        BestCard.Visibility = best is null ? Visibility.Collapsed : Visibility.Visible;
        MediaSection.Visibility = ViewModel.Media.Count > 0 || best is null ? Visibility.Visible : Visibility.Collapsed;
        if (best is null)
        {
            return;
        }

        BestOverline.Text = ViewModel.BestOverline;
        BestTitle.Text = best.Title;
        BestMeta.Text = best.RatingText is { } rating ? $"★ {rating}  ·  {best.MetaLine}" : best.MetaLine;
        BestOverview.Text = best.Overview ?? string.Empty;
        ImageEx.SetUrl(BestBackdrop, _tmdb.ImageUrl(best.BackdropPath, "w1280"));
        ImageEx.SetUrl(BestPoster, _tmdb.ImageUrl(best.PosterPath, "w342"));
        Ambient.Show(ViewModel.BestBackdropUrl, ViewModel.BestPreviewUrl);
    }

    private void OnBestClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.BestMatch is { } best) Navigator.OpenMedia(best, ViewModel.SeasonHint);
    }

    private void OnFilterChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem?.Tag is string tag && Enum.TryParse<SearchFilter>(tag, out var filter) && filter != ViewModel.Filter)
        {
            ViewModel.ApplyFilter(filter);
        }
    }
}

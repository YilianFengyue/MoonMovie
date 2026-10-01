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
        ViewModel.People.CollectionChanged += (_, _) =>
            PeopleRow.Visibility = ViewModel.People.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
                LoadingRing.Visibility = ViewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;
                break;
            case nameof(SearchViewModel.Summary):
                SummaryText.Text = ViewModel.Summary;
                break;
        }
    }

    private void OnResultClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SearchResultViewModel result })
        {
            Navigator.OpenMedia(result.Item, ViewModel.SeasonHint);
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
        }
    }
}

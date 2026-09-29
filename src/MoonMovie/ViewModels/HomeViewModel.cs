using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MoonMovie.Core.Home;
using MoonMovie.Core.Tmdb;

namespace MoonMovie.ViewModels;

public sealed partial class HomeViewModel : ObservableObject
{
    private const int MaxDiscoverPages = 25;

    private readonly HomeFeedService _feed;
    private readonly TmdbClient _tmdb;
    private readonly HashSet<string> _discoverSeen = [];
    private int _discoverPage;
    private bool _loadingMore;
    private bool _started;

    public HomeViewModel(HomeFeedService feed, TmdbClient tmdb)
    {
        _feed = feed;
        _tmdb = tmdb;
        foreach (var row in feed.Rows)
        {
            Rows.Add(new HomeRowViewModel(row));
        }
    }

    public ObservableCollection<SpotlightViewModel> Spotlight { get; } = [];

    public ObservableCollection<HomeRowViewModel> Rows { get; } = [];

    public ObservableCollection<MediaCardViewModel> Discover { get; } = [];

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    public async Task LoadAsync()
    {
        if (_started)
        {
            return;
        }

        _started = true;

        if (!_tmdb.IsConfigured)
        {
            ErrorMessage = "未找到 TMDB 密钥。请在 .env 中配置 TMDB_API_KEY。";
            return;
        }

        var spotlight = LoadSpotlightAsync();
        var rows = Task.WhenAll(Rows.Select(LoadRowAsync));
        var discover = LoadMoreDiscoverAsync();
        await Task.WhenAll(spotlight, rows, discover);

        if (Spotlight.Count == 0 && Rows.All(r => r.Items.Count == 0))
        {
            ErrorMessage = "暂时无法连接 TMDB，请检查网络或代理设置。";
        }
    }

    public async Task LoadMoreDiscoverAsync()
    {
        if (_loadingMore || _discoverPage >= MaxDiscoverPages)
        {
            return;
        }

        _loadingMore = true;
        try
        {
            var page = _discoverPage + 1;
            var items = await _feed.DiscoverPageAsync(page, CacheMode.StaleWhileRevalidate);
            _discoverPage = page;

            foreach (var item in items)
            {
                if (!_discoverSeen.Add(item.MediaKey))
                {
                    continue;
                }

                // Every seventh tile with good artwork goes wide; that is what gives the wall its rhythm.
                var variant = Discover.Count % 7 == 3 && item.BackdropPath is not null
                    ? CardVariant.Landscape
                    : CardVariant.Poster;
                Discover.Add(new MediaCardViewModel(item, _tmdb, variant));
            }
        }
        finally
        {
            _loadingMore = false;
        }
    }

    private async Task LoadSpotlightAsync()
    {
        var items = await _feed.SpotlightAsync(CacheMode.StaleWhileRevalidate);
        foreach (var item in items)
        {
            Spotlight.Add(new SpotlightViewModel(item, _tmdb));
        }
    }

    private async Task LoadRowAsync(HomeRowViewModel row)
    {
        var items = await _feed.LoadRowAsync(row.Definition, CacheMode.StaleWhileRevalidate);
        var variant = row.IsLandscape ? CardVariant.Landscape : CardVariant.Poster;
        foreach (var item in items)
        {
            row.Items.Add(new MediaCardViewModel(item, _tmdb, variant));
        }

        row.IsLoaded = row.Items.Count > 0;
    }
}

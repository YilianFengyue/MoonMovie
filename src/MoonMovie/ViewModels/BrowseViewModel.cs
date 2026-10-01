using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using MoonMovie.Core.Browse;
using MoonMovie.Core.Tmdb;

namespace MoonMovie.ViewModels;

/// <summary>One 电影 / 剧集 / 动漫 page: filter rows plus an endless poster wall.</summary>
/// <remarks>Kept alive per section (see <see cref="BrowseSections"/>) so going back restores filters and position.</remarks>
public sealed partial class BrowseViewModel : ObservableObject
{
    private const int MaxPages = 60;

    private readonly TmdbClient _tmdb;
    private readonly HashSet<string> _seen = [];
    private int _page;
    private int _version;
    private bool _exhausted;
    private bool _loading;
    private bool _started;

    public BrowseViewModel(BrowseSection section, TmdbClient tmdb)
    {
        Section = section;
        _tmdb = tmdb;
        foreach (var dimension in BrowseCatalog.Dimensions(section, DateTime.Now.Year))
        {
            var group = new FilterGroupViewModel(dimension);
            group.Changed += (_, _) => _ = ReloadAsync();
            Groups.Add(group);
        }
    }

    public BrowseSection Section { get; }

    public string Title => BrowseCatalog.Title(Section);

    public string Tagline => BrowseCatalog.Tagline(Section);

    public ObservableCollection<FilterGroupViewModel> Groups { get; } = [];

    public ObservableCollection<MediaCardViewModel> Items { get; } = [];

    /// <summary>Restored when the page is rebuilt after going back.</summary>
    public double ScrollOffset { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>Empty-state or error text; null while there is something to show.</summary>
    [ObservableProperty]
    public partial string? Message { get; private set; }

    /// <summary>"科幻 · 美国 · 2010年代 · 高分" — the non-default choices, for the compact sticky bar.</summary>
    public string SelectionSummary
    {
        get
        {
            var parts = Groups.Where(g => g.Selected.Option.Value is not null && g.Dimension.Key != BrowseCatalog.Sort)
                .Select(g => g.Selected.Label)
                .ToList();
            var sort = Groups.FirstOrDefault(g => g.Dimension.Key == BrowseCatalog.Sort)?.Selected.Label;
            if (sort is not null) parts.Add(sort);
            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>Raised before the wall is cleared for a new filter, so the view can fade it.</summary>
    public event EventHandler? Resetting;

    public async Task EnsureLoadedAsync()
    {
        if (_started) return;
        _started = true;

        if (!_tmdb.IsConfigured)
        {
            Message = "未找到 TMDB 密钥。请在 .env 中配置 TMDB_API_KEY。";
            return;
        }

        await LoadMoreAsync();
    }

    public async Task LoadMoreAsync()
    {
        if (_loading || _exhausted || !_started) return;

        _loading = true;
        IsLoading = true;
        var version = _version;
        try
        {
            var selection = Groups.ToDictionary(g => g.Dimension.Key, g => g.Selected.Option.Value);
            var (kind, query) = BrowseCatalog.Build(Section, selection, DateOnly.FromDateTime(DateTime.Now));

            // A page can come back with nothing new (duplicates, no artwork); keep going so the wall grows.
            var added = 0;
            for (var attempt = 0; attempt < 3 && added == 0 && !_exhausted; attempt++)
            {
                var page = _page + 1;
                var items = await _tmdb.DiscoverAsync(kind, query, page, Core.Tmdb.CacheMode.StaleWhileRevalidate);
                if (version != _version) return;

                _page = page;
                if (items.Count == 0 || page >= MaxPages) _exhausted = true;

                foreach (var item in items)
                {
                    if (item.PosterPath is null || !_seen.Add(item.MediaKey)) continue;
                    Items.Add(new MediaCardViewModel(item, _tmdb));
                    added++;
                }
            }

            Message = Items.Count > 0 ? null
                : _exhausted ? "没有符合这些条件的作品，换个组合试试"
                : "暂时无法连接 TMDB，请检查网络或代理设置。";
            if (Items.Count == 0 && !_exhausted) _exhausted = true; // stop hammering a dead connection
        }
        finally
        {
            if (version == _version)
            {
                _loading = false;
                IsLoading = false;
            }
        }
    }

    private async Task ReloadAsync()
    {
        _version++;
        _loading = false;
        _exhausted = false;
        _page = 0;
        _seen.Clear();
        OnPropertyChanged(nameof(SelectionSummary));
        Resetting?.Invoke(this, EventArgs.Empty);
        Items.Clear();
        Message = null;
        ScrollOffset = 0;
        await LoadMoreAsync();
    }
}

/// <summary>A labelled row of chips; exactly one is selected.</summary>
public sealed partial class FilterGroupViewModel : ObservableObject
{
    public FilterGroupViewModel(FilterDimension dimension)
    {
        Dimension = dimension;
        foreach (var option in dimension.Options)
        {
            Options.Add(new FilterOptionViewModel(this, option));
        }

        Selected = Options[dimension.DefaultIndex];
        Selected.IsSelected = true;
    }

    public FilterDimension Dimension { get; }

    public string Label => Dimension.Label;

    public ObservableCollection<FilterOptionViewModel> Options { get; } = [];

    public FilterOptionViewModel Selected { get; private set; }

    public event EventHandler? Changed;

    internal void Select(FilterOptionViewModel option)
    {
        if (option == Selected) return;
        Selected.IsSelected = false;
        Selected = option;
        option.IsSelected = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed partial class FilterOptionViewModel(FilterGroupViewModel group, FilterOption option) : ObservableObject
{
    private static readonly SolidColorBrush SelectedText = new(Windows.UI.Color.FromArgb(0xFF, 0x0C, 0x0D, 0x10));
    private static readonly SolidColorBrush IdleText = new(Windows.UI.Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF));

    public FilterOption Option { get; } = option;

    public string Label => Option.Label;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public Visibility SelectedVisibility => IsSelected ? Visibility.Visible : Visibility.Collapsed;

    public Brush TextBrush => IsSelected ? SelectedText : IdleText;

    partial void OnIsSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(SelectedVisibility));
        OnPropertyChanged(nameof(TextBrush));
    }

    [RelayCommand]
    private void Select() => group.Select(this);
}

/// <summary>One live <see cref="BrowseViewModel"/> per section for the app's lifetime.</summary>
public sealed class BrowseSections(TmdbClient tmdb)
{
    private readonly Dictionary<BrowseSection, BrowseViewModel> _models = [];

    public BrowseViewModel Get(BrowseSection section)
    {
        if (!_models.TryGetValue(section, out var model))
        {
            model = new BrowseViewModel(section, tmdb);
            _models[section] = model;
        }

        return model;
    }
}

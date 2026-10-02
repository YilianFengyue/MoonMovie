using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using MoonMovie.Core.Bilibili;
using MoonMovie.Core.Models;
using MoonMovie.Core.Search;
using MoonMovie.Core.Sources;
using MoonMovie.Core.Tmdb;

namespace MoonMovie.ViewModels;

/// <summary>One row of the title-bar suggestion list: a title, or a past query.</summary>
public sealed class SuggestionItem
{
    private SuggestionItem(string text, string meta, string? posterUrl, MediaItem? item, bool isHistory, bool isClearAll = false)
    {
        Text = text;
        Meta = meta;
        PosterUrl = posterUrl;
        Item = item;
        IsHistory = isHistory;
        IsClearAll = isClearAll;
    }

    public string Text { get; }

    public string Meta { get; }

    public string? PosterUrl { get; }

    public MediaItem? Item { get; }

    public bool IsHistory { get; }

    /// <summary>The last row under past queries: 「清除搜索记录」.</summary>
    public bool IsClearAll { get; }

    public Visibility PosterVisibility => IsHistory || IsClearAll ? Visibility.Collapsed : Visibility.Visible;

    public Visibility HistoryVisibility => IsHistory ? Visibility.Visible : Visibility.Collapsed;

    public Visibility EntryVisibility => IsClearAll ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ClearAllVisibility => IsClearAll ? Visibility.Visible : Visibility.Collapsed;

    public static SuggestionItem ForMedia(MediaItem item, TmdbClient tmdb) =>
        new(item.Title, item.Year is { } y ? $"{y} · {item.KindLabel}" : item.KindLabel,
            tmdb.ImageUrl(item.PosterPath, "w92"), item, isHistory: false);

    public static SuggestionItem ForHistory(string query) => new(query, "最近搜索", null, null, isHistory: true);

    public static SuggestionItem ClearAll() => new("清除搜索记录", string.Empty, null, null, isHistory: false, isClearAll: true);

    // AutoSuggestBox writes ToString() into the text box while the user arrows through suggestions.
    public override string ToString() => IsClearAll ? string.Empty : Text;
}

/// <summary>A row in the search result list: enough detail to tell same-named titles apart.</summary>
public sealed partial class SearchResultViewModel(MediaItem item, TmdbClient tmdb, bool isExact, int? seasonHint)
{
    public MediaItem Item { get; } = item;

    /// <summary>For x:Bind in templates: ItemsRepeater does not set DataContext, so {Binding} sees nothing.</summary>
    public SearchResultViewModel Self => this;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void Open() => Services.Navigator.OpenMedia(Item, seasonHint);

    public string Title => Item.Title;

    /// <summary>"The Legend of Hei · 2019"</summary>
    public string Subtitle
    {
        get
        {
            var parts = new List<string>(2);
            if (Item.OriginalTitle is { Length: > 0 } original) parts.Add(original);
            if (Item.Year is { } y) parts.Add(y.ToString());
            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>"剧集 · 动画 / 科幻奇幻"</summary>
    public string Meta
    {
        get
        {
            var genres = Item.GenreNames.Take(3).ToArray();
            return genres.Length > 0 ? $"{Item.KindLabel}  ·  {string.Join(" / ", genres)}" : Item.KindLabel;
        }
    }

    public string? RatingText => Item.RatingText;

    public Visibility RatingVisibility => RatingText is null ? Visibility.Collapsed : Visibility.Visible;

    public string Overview => Item.Overview ?? string.Empty;

    public Visibility OverviewVisibility => Item.Overview is null ? Visibility.Collapsed : Visibility.Visible;

    public string? PosterUrl => tmdb.ImageUrl(Item.PosterPath, "w185");

    public string? AmbientUrl => tmdb.ImageUrl(Item.BackdropPath, "w1280");

    public Visibility ExactVisibility => isExact ? Visibility.Visible : Visibility.Collapsed;
}

public enum SearchFilter
{
    All,
    Movie,
    Tv,
    Bili,
}

public sealed partial class SearchViewModel(SearchService search, TmdbClient tmdb, BiliClient bili) : ObservableObject
{
    private SearchResults? _results;
    private string _query = "";
    private int _biliPage;
    private bool _biliHasMore = true;
    private int _biliVersion;

    /// <summary>The 「B站」 tab: videos for the raw query, loaded the first time the tab opens, then page by page.</summary>
    public ObservableCollection<BiliVideoViewModel> BiliVideos { get; } = [];

    public BiliOrder BiliOrder { get; private set; }

    [ObservableProperty]
    public partial bool IsBiliLoading { get; private set; }

    public ObservableCollection<SearchResultViewModel> Results { get; } = [];

    public ObservableCollection<PersonViewModel> People { get; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    public SearchFilter Filter { get; private set; }

    public int? SeasonHint => _results?.Query.Season;

    public async Task RunAsync(string query, CancellationToken ct)
    {
        _query = query;
        IsLoading = true;
        Summary = "正在搜索…";
        try
        {
            _results = await search.SearchAsync(query, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _results = null;
        }
        finally
        {
            IsLoading = false;
        }

        if (ct.IsCancellationRequested) return;

        People.Clear();
        foreach (var p in _results?.People ?? [])
        {
            var role = p.Department switch { "Acting" => "演员", "Directing" => "导演", "Writing" => "编剧", _ => p.Department };
            People.Add(new PersonViewModel(new Person(p.Id, p.Name, role, p.ProfilePath), tmdb));
        }

        ApplyFilter(Filter);
    }

    public void ApplyFilter(SearchFilter filter)
    {
        Filter = filter;
        if (filter == SearchFilter.Bili)
        {
            if (BiliVideos.Count == 0 && !IsBiliLoading) _ = LoadBiliAsync(reset: true);
            else Summary = BiliSummary();
            return;
        }

        Results.Clear();
        if (_results is null)
        {
            Summary = "搜索失败，请检查网络";
            return;
        }

        var wanted = SourceMatcher.Normalize(_results.Query.Keyword);
        var items = _results.Media.Where(m => filter switch
        {
            SearchFilter.Movie => m.Kind == MediaKind.Movie,
            SearchFilter.Tv => m.Kind == MediaKind.Tv,
            _ => true,
        });

        foreach (var item in items)
        {
            var exact = SourceMatcher.Normalize(item.Title) == wanted || SourceMatcher.Normalize(item.OriginalTitle) == wanted;
            Results.Add(new SearchResultViewModel(item, tmdb, exact, _results.Query.Season));
        }

        var hints = new List<string>(3);
        if (_results.Person is { } person) hints.Add(person.Name);
        if (_results.Query.Year is { } y) hints.Add($"{y} 年");
        if (_results.Query.Season is { } s) hints.Add($"第 {s} 季");
        Summary = Results.Count == 0
            ? "没有找到相关的电影或剧集"
            : $"{Results.Count} 部作品{(People.Count > 0 ? $" · {People.Count} 位人物" : "")}{(hints.Count > 0 ? " · 已识别 " + string.Join("、", hints) : "")}";
    }

    public void SetBiliOrder(BiliOrder order)
    {
        if (order == BiliOrder) return;
        BiliOrder = order;
        _ = LoadBiliAsync(reset: true);
    }

    /// <summary>The next page (or the first, after a new order); stale answers from an older order are dropped.</summary>
    public async Task LoadBiliAsync(bool reset = false)
    {
        if (_query.Length == 0 || (!reset && (IsBiliLoading || !_biliHasMore))) return;
        var version = reset ? ++_biliVersion : _biliVersion;
        if (reset)
        {
            BiliVideos.Clear();
            _biliPage = 0;
            _biliHasMore = true;
        }

        IsBiliLoading = true;
        if (BiliVideos.Count == 0) Summary = "正在搜索 B站…";
        try
        {
            var page = await bili.SearchAsync(_query, BiliOrder, _biliPage + 1);
            if (version != _biliVersion) return;
            _biliPage++;
            _biliHasMore = page.Count >= 20 && _biliPage < 25;
            var seen = BiliVideos.Select(v => v.Video.Bvid).ToHashSet();
            foreach (var v in page.Where(v => seen.Add(v.Bvid))) BiliVideos.Add(new BiliVideoViewModel(v));
            if (Filter == SearchFilter.Bili) Summary = BiliSummary();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or BiliException or System.Text.Json.JsonException)
        {
            if (version == _biliVersion && Filter == SearchFilter.Bili && BiliVideos.Count == 0) Summary = "B站搜索失败，请稍后再试";
        }
        finally
        {
            if (version == _biliVersion) IsBiliLoading = false;
        }
    }

    private string BiliSummary() => BiliVideos.Count == 0 ? "B站上没有找到相关视频" : $"B站视频 · 已加载 {BiliVideos.Count} 个";
}

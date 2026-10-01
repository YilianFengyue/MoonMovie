using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using MoonMovie.Core.Models;
using MoonMovie.Core.Search;
using MoonMovie.Core.Sources;
using MoonMovie.Core.Tmdb;

namespace MoonMovie.ViewModels;

/// <summary>One row of the title-bar suggestion list: a title, or a past query.</summary>
public sealed class SuggestionItem
{
    private SuggestionItem(string text, string meta, string? posterUrl, MediaItem? item, bool isHistory)
    {
        Text = text;
        Meta = meta;
        PosterUrl = posterUrl;
        Item = item;
        IsHistory = isHistory;
    }

    public string Text { get; }

    public string Meta { get; }

    public string? PosterUrl { get; }

    public MediaItem? Item { get; }

    public bool IsHistory { get; }

    public Visibility PosterVisibility => IsHistory ? Visibility.Collapsed : Visibility.Visible;

    public Visibility HistoryVisibility => IsHistory ? Visibility.Visible : Visibility.Collapsed;

    public static SuggestionItem ForMedia(MediaItem item, TmdbClient tmdb) =>
        new(item.Title, item.Year is { } y ? $"{y} · {item.KindLabel}" : item.KindLabel,
            tmdb.ImageUrl(item.PosterPath, "w92"), item, isHistory: false);

    public static SuggestionItem ForHistory(string query) => new(query, "最近搜索", null, null, isHistory: true);

    // AutoSuggestBox writes ToString() into the text box while the user arrows through suggestions.
    public override string ToString() => Text;
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

    public string Overview => Item.Overview ?? "暂无简介";

    public string? PosterUrl => tmdb.ImageUrl(Item.PosterPath, "w185");

    public string? AmbientUrl => tmdb.ImageUrl(Item.BackdropPath, "w1280");

    public Visibility ExactVisibility => isExact ? Visibility.Visible : Visibility.Collapsed;
}

public enum SearchFilter
{
    All,
    Movie,
    Tv,
}

public sealed partial class SearchViewModel(SearchService search, TmdbClient tmdb) : ObservableObject
{
    private SearchResults? _results;

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
}

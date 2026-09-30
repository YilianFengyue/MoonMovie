using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using MoonMovie.Core.Models;
using MoonMovie.Core.Search;
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

public enum SearchFilter
{
    All,
    Movie,
    Tv,
}

public sealed partial class SearchViewModel(SearchService search, TmdbClient tmdb) : ObservableObject
{
    private SearchResults? _results;

    public ObservableCollection<MediaCardViewModel> Media { get; } = [];

    public ObservableCollection<PersonViewModel> People { get; } = [];

    [ObservableProperty]
    public partial MediaItem? BestMatch { get; private set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    public SearchFilter Filter { get; private set; }

    public string Query { get; private set; } = "";

    public string? BestBackdropUrl => tmdb.ImageUrl(BestMatch?.BackdropPath, "w1280");

    /// <summary>"最佳匹配", or "克里斯托弗·诺兰 的代表作" for a person search.</summary>
    public string BestOverline => _results?.Person is { } p ? $"{p.Name} 的代表作" : "最佳匹配";

    public int? SeasonHint => _results?.Query.Season;

    public string? BestPreviewUrl => tmdb.ImageUrl(BestMatch?.BackdropPath, "w780");

    public async Task RunAsync(string query, CancellationToken ct)
    {
        Query = query;
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
        Media.Clear();
        if (_results is null)
        {
            BestMatch = null;
            Summary = "搜索失败，请检查网络";
            return;
        }

        var items = _results.Media.Where(m => filter switch
        {
            SearchFilter.Movie => m.Kind == MediaKind.Movie,
            SearchFilter.Tv => m.Kind == MediaKind.Tv,
            _ => true,
        }).ToArray();

        BestMatch = items.FirstOrDefault(m => m.BackdropPath is not null);
        foreach (var item in items.Where(i => i != BestMatch && i.PosterPath is not null))
        {
            Media.Add(new MediaCardViewModel(item, tmdb));
        }

        var hints = new List<string>(2);
        if (_results.Query.Year is { } y) hints.Add($"{y} 年");
        if (_results.Query.Season is { } s) hints.Add($"第 {s} 季");
        if (_results.Person is { } person) hints.Insert(0, person.Name);
        Summary = items.Length == 0
            ? "没有找到相关的电影或剧集"
            : $"{items.Length} 部作品{(People.Count > 0 ? $" · {People.Count} 位人物" : "")}{(hints.Count > 0 ? " · 已识别 " + string.Join("、", hints) : "")}";
        OnPropertyChanged(nameof(BestOverline));
        OnPropertyChanged(nameof(BestBackdropUrl));
        OnPropertyChanged(nameof(BestPreviewUrl));
    }
}

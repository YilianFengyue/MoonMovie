using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using MoonMovie.Core.Bilibili;
using MoonMovie.Core.Browse;
using MoonMovie.Services;

namespace MoonMovie.ViewModels;

public enum BiliHubTab
{
    Shows,
    Videos,
    Mine,
}

/// <summary>A 正版 season card; opening it resolves to the detail page (or B站-only playback), which takes a moment.</summary>
public sealed partial class BiliSeasonViewModel(BiliSeasonCard card, bool square = false) : ObservableObject
{
    public BiliSeasonCard Card { get; } = card;

    public string Title => Card.Title;

    public string? CoverUrl => square ? BiliClient.Thumb(Card.Cover, 240, 240) : BiliClient.Thumb(Card.Cover, 300, 400);

    /// <summary>"全12话 · 9.7分", "看到第3话 · 更新至第12话", "第11话 · 已更新".</summary>
    public string Subtitle => string.Join(" · ", new[] { Card.Meta, Card.Score is { } s ? $"{s}分" : Card.Extra }.Where(x => !string.IsNullOrEmpty(x)));

    public string? Badge => Card.Badge;

    public Visibility BadgeVisibility => string.IsNullOrEmpty(Card.Badge) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The timeline's air time pill ("10:00").</summary>
    public string? Corner { get; init; }

    public Visibility CornerVisibility => string.IsNullOrEmpty(Corner) ? Visibility.Collapsed : Visibility.Visible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OpeningVisibility))]
    public partial bool IsOpening { get; private set; }

    public Visibility OpeningVisibility => IsOpening ? Visibility.Visible : Visibility.Collapsed;

    [RelayCommand]
    private async Task OpenAsync()
    {
        if (IsOpening) return;
        IsOpening = true;
        try
        {
            await BiliPlayback.OpenSeasonAsync(Card.SeasonId);
        }
        finally
        {
            IsOpening = false;
        }
    }

    [RelayCommand]
    private void OpenInBrowser() => _ = Windows.System.Launcher.LaunchUriAsync(new Uri($"https://www.bilibili.com/bangumi/play/ss{Card.SeasonId}"));
}

/// <summary>
/// The 「B站」 page: 番剧影视 (新番时间表 and the 正版 片库 with B站's own filters), 视频 (热门 / 排行榜), and 我的
/// (追番, 追剧, 稍后再看, 收藏夹) for a linked account. Each part loads the first time it is shown.
/// </summary>
public sealed partial class BiliHubViewModel : ObservableObject
{
    private static readonly (string Label, int Type)[] SeasonTypes = [("番剧", 1), ("国创", 4), ("电影", 2), ("电视剧", 5), ("纪录片", 3)];

    // B站's index offers more filters than a browse page needs; these are the ones people reach for.
    private static readonly HashSet<string> UsefulFilters = ["area", "style_id", "is_finish", "year", "season_status"];

    private readonly BiliClient _bili;
    private readonly BiliAccountService _account;
    private IReadOnlyList<BiliTimelineDay> _days = [];
    private int _libraryPage;
    private bool _libraryHasNext = true;
    private int _libraryVersion;
    private int _videoPage;
    private bool _videoHasMore = true;
    private bool _showsLoaded;
    private bool _videosLoaded;
    private bool _mineLoaded;
    private long? _folder;
    private int _folderPage;
    private bool _folderHasMore;

    public BiliHubViewModel(BiliClient bili, BiliAccountService account)
    {
        _bili = bili;
        _account = account;
        TypeGroup = new FilterGroupViewModel(new FilterDimension("season_type", "分类",
            SeasonTypes.Select(t => new FilterOption(t.Label, t.Type.ToString())).ToArray()));
        TypeGroup.Changed += (_, _) => _ = ReloadLibraryAsync(conditions: true);
        VideoGroup = new FilterGroupViewModel(new FilterDimension("video", "", [new("热门", "popular"), new("排行榜", "ranking")]));
        VideoGroup.Changed += (_, _) => _ = LoadVideosAsync(reset: true);
    }

    [ObservableProperty]
    public partial BiliHubTab Tab { get; set; }

    public bool IsSignedIn => _bili.IsSignedIn;

    // ----- 番剧影视 ----------------------------------------------------------------------------------------

    public FilterGroupViewModel? DayGroup { get; private set; }

    public ObservableCollection<BiliSeasonViewModel> Timeline { get; } = [];

    public FilterGroupViewModel TypeGroup { get; }

    /// <summary>分类 first, then B站's filters for that type, then 排序.</summary>
    public ObservableCollection<FilterGroupViewModel> LibraryGroups { get; } = [];

    public ObservableCollection<BiliSeasonViewModel> Library { get; } = [];

    [ObservableProperty]
    public partial bool IsLibraryLoading { get; private set; }

    [ObservableProperty]
    public partial string? LibraryMessage { get; private set; }

    // ----- 视频 --------------------------------------------------------------------------------------------

    public FilterGroupViewModel VideoGroup { get; }

    public ObservableCollection<BiliVideoViewModel> Videos { get; } = [];

    [ObservableProperty]
    public partial bool IsVideosLoading { get; private set; }

    // ----- 我的 --------------------------------------------------------------------------------------------

    public ObservableCollection<BiliSeasonViewModel> Following { get; } = [];

    public ObservableCollection<BiliSeasonViewModel> Dramas { get; } = [];

    public ObservableCollection<BiliVideoViewModel> WatchLater { get; } = [];

    public FilterGroupViewModel? FolderGroup { get; private set; }

    public ObservableCollection<BiliVideoViewModel> FolderItems { get; } = [];

    [ObservableProperty]
    public partial bool IsMineLoading { get; private set; }

    /// <summary>Loads what the current tab shows, once.</summary>
    public Task EnsureLoadedAsync() => Tab switch
    {
        BiliHubTab.Shows when !_showsLoaded => LoadShowsAsync(),
        BiliHubTab.Videos when !_videosLoaded => LoadVideosAsync(reset: true),
        BiliHubTab.Mine when !_mineLoaded && IsSignedIn => LoadMineAsync(),
        _ => Task.CompletedTask,
    };

    /// <summary>The next page of whatever grid the tab ends in (near the bottom of the page).</summary>
    public Task LoadMoreAsync() => Tab switch
    {
        BiliHubTab.Shows => LoadLibraryPageAsync(),
        BiliHubTab.Videos when VideoGroup.Selected.Option.Value == "popular" => LoadVideosAsync(reset: false),
        BiliHubTab.Mine => LoadFolderPageAsync(),
        _ => Task.CompletedTask,
    };

    /// <summary>After linking or unlinking the account: 我的 starts over.</summary>
    public void AccountChanged()
    {
        _mineLoaded = false;
        Following.Clear();
        Dramas.Clear();
        WatchLater.Clear();
        FolderItems.Clear();
        FolderGroup = null;
        OnPropertyChanged(nameof(FolderGroup));
        OnPropertyChanged(nameof(IsSignedIn));
    }

    private async Task LoadShowsAsync()
    {
        _showsLoaded = true;
        try
        {
            // Three days back to three ahead: the header row stays one line of chips.
            var all = await _bili.TimelineAsync();
            var center = Math.Max(0, all.ToList().FindIndex(d => d.IsToday));
            _days = all.Skip(Math.Max(0, center - 3)).Take(7).ToArray();
            var options = _days.Select(d => new FilterOption(d.IsToday ? "今天" : $"{WeekDay(d.DayOfWeek)} {d.Date.Month}/{d.Date.Day}", d.Date.ToString("O"))).ToArray();
            var today = Math.Max(0, _days.ToList().FindIndex(d => d.IsToday));
            if (options.Length > 0)
            {
                DayGroup = new FilterGroupViewModel(new FilterDimension("day", "放送", options, today));
                DayGroup.Changed += (_, _) => ShowDay();
                OnPropertyChanged(nameof(DayGroup));
                ShowDay();
            }
        }
        catch (Exception ex) when (IsNetwork(ex))
        {
            // The timeline is a bonus; the 片库 below still loads.
        }

        await ReloadLibraryAsync(conditions: true);
    }

    private void ShowDay()
    {
        Timeline.Clear();
        var day = _days.FirstOrDefault(d => d.Date.ToString("O") == DayGroup?.Selected.Option.Value);
        // A show releasing several episodes the same day appears once, with its latest.
        foreach (var e in (day?.Episodes ?? []).GroupBy(e => e.SeasonId).Select(g => g.Last()))
        {
            var card = new BiliSeasonCard(e.SeasonId, e.Title, e.Cover, 1, null, e.Index, e.Published ? "已更新" : "即将更新", null);
            Timeline.Add(new BiliSeasonViewModel(card, square: true) { Corner = e.Time });
        }
    }

    /// <summary>A new 分类 brings its own filters; any other filter change keeps them.</summary>
    private async Task ReloadLibraryAsync(bool conditions)
    {
        var version = ++_libraryVersion;
        Library.Clear();
        _libraryPage = 0;
        _libraryHasNext = true;
        LibraryMessage = null;

        if (conditions)
        {
            IsLibraryLoading = true;
            BiliIndexConditions found;
            try
            {
                found = await _bili.IndexConditionsAsync(SeasonType);
            }
            catch (Exception ex) when (IsNetwork(ex))
            {
                if (version == _libraryVersion)
                {
                    IsLibraryLoading = false;
                    LibraryMessage = "B站片库加载失败，请稍后再试";
                }

                return;
            }

            if (version != _libraryVersion) return;
            LibraryGroups.Clear();
            LibraryGroups.Add(TypeGroup);
            foreach (var filter in found.Filters.Where(f => UsefulFilters.Contains(f.Field)))
            {
                // B站 names "all" after the filter ("付费类型"); say 全部 like the other browse pages.
                var options = filter.Values.Select((v, i) => new FilterOption(i == 0 ? "全部" : v.Name, i == 0 ? null : v.Keyword)).ToArray();
                var group = new FilterGroupViewModel(new FilterDimension(filter.Field, filter.Name, options));
                group.Changed += (_, _) => _ = ReloadLibraryAsync(conditions: false);
                LibraryGroups.Add(group);
            }

            if (found.Orders.Count > 0)
            {
                var orders = new FilterGroupViewModel(new FilterDimension("order", "排序",
                    found.Orders.Select(o => new FilterOption(o.Name, o.Field)).ToArray()));
                orders.Changed += (_, _) => _ = ReloadLibraryAsync(conditions: false);
                LibraryGroups.Add(orders);
            }
        }

        await LoadLibraryPageAsync();
    }

    private int SeasonType => int.TryParse(TypeGroup.Selected.Option.Value, out var t) ? t : 1;

    private async Task LoadLibraryPageAsync()
    {
        if (!_libraryHasNext || (IsLibraryLoading && Library.Count > 0)) return;
        var version = _libraryVersion;
        IsLibraryLoading = true;
        try
        {
            var filters = LibraryGroups.Where(g => g != TypeGroup && g.Dimension.Key != "order" && g.Selected.Option.Value is not null)
                .ToDictionary(g => g.Dimension.Key, g => g.Selected.Option.Value!);
            var order = LibraryGroups.FirstOrDefault(g => g.Dimension.Key == "order")?.Selected.Option.Value ?? "3";
            var page = await _bili.IndexAsync(SeasonType, filters, order, _libraryPage + 1);
            if (version != _libraryVersion) return;
            _libraryPage++;
            _libraryHasNext = page.HasNext;
            foreach (var card in page.Items) Library.Add(new BiliSeasonViewModel(card));
            LibraryMessage = Library.Count == 0 ? "没有符合条件的作品" : null;
        }
        catch (Exception ex) when (IsNetwork(ex))
        {
            if (version == _libraryVersion && Library.Count == 0) LibraryMessage = "B站片库加载失败，请稍后再试";
        }
        finally
        {
            if (version == _libraryVersion) IsLibraryLoading = false;
        }
    }

    private async Task LoadVideosAsync(bool reset)
    {
        var popular = VideoGroup.Selected.Option.Value == "popular";
        if (reset)
        {
            _videosLoaded = true;
            Videos.Clear();
            _videoPage = 0;
            _videoHasMore = true;
        }
        else if (!popular || !_videoHasMore || IsVideosLoading)
        {
            return;
        }

        IsVideosLoading = true;
        try
        {
            if (popular)
            {
                var page = await _bili.PopularAsync(_videoPage + 1);
                if (VideoGroup.Selected.Option.Value != "popular") return;
                _videoPage++;
                _videoHasMore = page.HasMore && _videoPage < 10;
                Append(Videos, page.Items);
            }
            else
            {
                var ranking = await _bili.RankingAsync();
                if (VideoGroup.Selected.Option.Value != "ranking") return;
                Append(Videos, ranking);
            }
        }
        catch (Exception ex) when (IsNetwork(ex))
        {
        }
        finally
        {
            IsVideosLoading = false;
        }
    }

    private async Task LoadMineAsync()
    {
        _mineLoaded = true;
        IsMineLoading = true;
        try
        {
            var anime = _bili.FollowingAsync(1);
            var drama = _bili.FollowingAsync(2);
            var later = _bili.WatchLaterAsync();
            var folders = _bili.FavFoldersAsync();
            await Task.WhenAll(Quiet(anime), Quiet(drama), Quiet(later), Quiet(folders));

            if (anime.IsCompletedSuccessfully) foreach (var c in anime.Result.Items) Following.Add(new BiliSeasonViewModel(c));
            if (drama.IsCompletedSuccessfully) foreach (var c in drama.Result.Items) Dramas.Add(new BiliSeasonViewModel(c));
            if (later.IsCompletedSuccessfully) Append(WatchLater, later.Result);
            if (folders.IsCompletedSuccessfully && folders.Result.Count > 0)
            {
                FolderGroup = new FilterGroupViewModel(new FilterDimension("folder", "收藏夹",
                    folders.Result.Select(f => new FilterOption($"{f.Title} {f.Count}", f.Id.ToString())).ToArray()));
                FolderGroup.Changed += (_, _) => _ = OpenFolderAsync();
                OnPropertyChanged(nameof(FolderGroup));
                await OpenFolderAsync();
            }
        }
        finally
        {
            IsMineLoading = false;
        }
    }

    private async Task OpenFolderAsync()
    {
        _folder = long.TryParse(FolderGroup?.Selected.Option.Value, out var id) ? id : null;
        FolderItems.Clear();
        _folderPage = 0;
        _folderHasMore = true;
        await LoadFolderPageAsync();
    }

    private async Task LoadFolderPageAsync()
    {
        if (_folder is not { } folder || !_folderHasMore || IsMineLoading && FolderItems.Count > 0) return;
        try
        {
            var page = await _bili.FavItemsAsync(folder, _folderPage + 1);
            if (folder != _folder) return;
            _folderPage++;
            _folderHasMore = page.HasMore;
            Append(FolderItems, page.Items);
        }
        catch (Exception ex) when (IsNetwork(ex))
        {
            _folderHasMore = false;
        }
    }

    private static void Append(ObservableCollection<BiliVideoViewModel> target, IEnumerable<BiliVideo> videos)
    {
        var seen = target.Select(v => v.Video.Bvid).ToHashSet();
        foreach (var v in videos.Where(v => v.Bvid.Length > 0 && seen.Add(v.Bvid))) target.Add(new BiliVideoViewModel(v));
    }

    private static async Task Quiet(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex) when (IsNetwork(ex))
        {
        }
    }

    private static bool IsNetwork(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or BiliException or System.Text.Json.JsonException;

    private static string WeekDay(int day) => day switch
    {
        1 => "周一",
        2 => "周二",
        3 => "周三",
        4 => "周四",
        5 => "周五",
        6 => "周六",
        _ => "周日",
    };
}

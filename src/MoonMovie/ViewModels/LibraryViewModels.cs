using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using MoonMovie.Core.Library;
using MoonMovie.Core.Models;
using MoonMovie.Core.Playback;
using MoonMovie.Core.Tmdb;
using MoonMovie.Services;

namespace MoonMovie.ViewModels;

/// <summary>A watched title: drives the 继续观看 cards and the history rows.</summary>
public sealed partial class WatchedItemViewModel(WatchProgress progress, TmdbClient tmdb, Action<WatchedItemViewModel>? onRemove = null)
{
    public WatchProgress Progress { get; } = progress;

    public WatchedItemViewModel Self => this;

    public string Title => Progress.Title;

    public string? BackdropUrl => tmdb.ImageUrl(Progress.BackdropPath ?? Progress.PosterPath, "w780");

    public string? ThumbUrl => tmdb.ImageUrl(Progress.BackdropPath ?? Progress.PosterPath, "w300");

    public string? AmbientUrl => tmdb.ImageUrl(Progress.BackdropPath, "w1280");

    /// <summary>"第 2 季 · 第 5 集" for series, empty for films.</summary>
    public string EpisodeText
    {
        get
        {
            if (Progress.Kind != MediaKind.Tv) return "电影";
            var index = Progress.HasNextEpisode ? Progress.EpisodeIndex + 1 : Progress.EpisodeIndex;
            var episode = $"第 {index + 1} 集";
            return Progress.Season is { } s and > 1 ? $"第 {s} 季 · {episode}" : episode;
        }
    }

    /// <summary>"剩余 23 分钟" / "下一集" / "已看完".</summary>
    public string StateText
    {
        get
        {
            if (Progress.HasNextEpisode) return "下一集";
            if (Progress.IsFinished) return "已看完";
            var left = TimeSpan.FromMilliseconds(Math.Max(0, Progress.DurationMs - Progress.PositionMs));
            return left.TotalMinutes >= 60
                ? $"剩余 {(int)left.TotalHours} 小时 {left.Minutes} 分"
                : $"剩余 {Math.Max(1, (int)Math.Round(left.TotalMinutes))} 分钟";
        }
    }

    /// <summary>Progress of the episode the card would play; a waiting next episode starts at zero.</summary>
    public double Fraction => Progress.HasNextEpisode ? 0 : Progress.Fraction;

    public GridLength PlayedLength => new(Math.Max(Fraction, 0.0001), GridUnitType.Star);

    public GridLength RemainingLength => new(Math.Max(1 - Fraction, 0.0001), GridUnitType.Star);

    public Visibility ProgressVisibility => Fraction > 0.005 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>"21:04" today, "昨天 21:04", "周三", "3月14日".</summary>
    public string WhenText
    {
        get
        {
            var at = Progress.UpdatedAt.ToLocalTime();
            var today = DateTime.Today;
            var day = at.Date;
            if (day == today) return at.ToString("HH:mm", CultureInfo.InvariantCulture);
            if (day == today.AddDays(-1)) return $"昨天 {at:HH:mm}";
            if (day > today.AddDays(-7)) return CultureInfo.GetCultureInfo("zh-CN").DateTimeFormat.GetAbbreviatedDayName(day.DayOfWeek);
            return day.Year == today.Year ? $"{day.Month}月{day.Day}日" : $"{day.Year}年{day.Month}月{day.Day}日";
        }
    }

    /// <summary>"第 5 集 · 剩余 23 分钟" / "剩余 1 小时 2 分".</summary>
    public string CardMeta => Progress.Kind == MediaKind.Tv ? $"{EpisodeText}  ·  {StateText}" : StateText;

    public string AutomationName => $"{Title} {EpisodeText} {StateText}";

    [RelayCommand]
    private void Open() => Navigator.Resume(Progress.ToMediaItem(), Progress.Season);

    [RelayCommand]
    private void Details() => Navigator.OpenMedia(Progress.ToMediaItem(), Progress.Season);

    [RelayCommand]
    private void Remove() => onRemove?.Invoke(this);
}

public sealed class HistoryGroupViewModel(string header, IEnumerable<WatchedItemViewModel> items)
{
    public string Header { get; } = header;

    public ObservableCollection<WatchedItemViewModel> Items { get; } = new(items);
}

public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly WatchProgressStore _progress;
    private readonly FavoritesStore _favorites;
    private readonly TmdbClient _tmdb;

    public LibraryViewModel(WatchProgressStore progress, FavoritesStore favorites, TmdbClient tmdb)
    {
        _progress = progress;
        _favorites = favorites;
        _tmdb = tmdb;
    }

    public ObservableCollection<WatchedItemViewModel> Continue { get; } = [];

    public ObservableCollection<HistoryGroupViewModel> History { get; } = [];

    public ObservableCollection<MediaCardViewModel> Favorites { get; } = [];

    [ObservableProperty]
    public partial MediaKind? FavoriteFilter { get; set; }

    public int HistoryCount { get; private set; }

    public int FavoriteCount { get; private set; }

    public void Refresh()
    {
        var recent = _progress.Recent(400);

        Continue.Clear();
        foreach (var p in recent.Where(p => p.IsContinuable).Take(40))
        {
            Continue.Add(new WatchedItemViewModel(p, _tmdb, RemoveWatched));
        }

        History.Clear();
        HistoryCount = recent.Count;
        var today = DateTime.Today;
        foreach (var group in recent.GroupBy(p => Bucket(p.UpdatedAt.ToLocalTime().Date, today)))
        {
            History.Add(new HistoryGroupViewModel(group.Key, group.Select(p => new WatchedItemViewModel(p, _tmdb, RemoveWatched))));
        }

        RefreshFavorites();
        OnPropertyChanged(string.Empty);
    }

    public void RefreshFavorites()
    {
        Favorites.Clear();
        var all = _favorites.All();
        FavoriteCount = all.Count;
        foreach (var entry in all.Where(e => FavoriteFilter is null || e.Kind == FavoriteFilter))
        {
            Favorites.Add(new MediaCardViewModel(entry.ToMediaItem(), _tmdb));
        }

        OnPropertyChanged(nameof(FavoriteCount));
    }

    public void ClearHistory()
    {
        _progress.Clear();
        Refresh();
    }

    partial void OnFavoriteFilterChanged(MediaKind? value) => RefreshFavorites();

    private void RemoveWatched(WatchedItemViewModel item)
    {
        _progress.Remove(item.Progress.MediaKey);
        Refresh();
    }

    private static string Bucket(DateTime day, DateTime today)
    {
        if (day == today) return "今天";
        if (day == today.AddDays(-1)) return "昨天";
        if (day > today.AddDays(-7)) return "本周早些时候";
        if (day > today.AddDays(-30)) return "本月";
        return "更早";
    }
}

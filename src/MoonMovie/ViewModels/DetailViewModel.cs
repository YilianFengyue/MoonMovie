using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MoonMovie.Core.Downloads;
using MoonMovie.Core.Library;
using MoonMovie.Core.Local;
using MoonMovie.Core.Models;
using MoonMovie.Core.Playback;
using MoonMovie.Core.Sources;
using MoonMovie.Core.Tmdb;
using MoonMovie.Imaging;
using MoonMovie.Services;

namespace MoonMovie.ViewModels;

public sealed partial class DetailViewModel : ObservableObject
{
    private const double MinLogoLuminance = 0.32;

    private readonly TmdbClient _tmdb;
    private readonly FavoritesStore _favorites;
    private readonly ImageLoader _images;
    private readonly WatchProgressStore _progress;
    private readonly LocalLibrary _local;
    private readonly DownloadManager _downloads;
    private MediaItem? _item;
    private int _seasonVersion;

    public DetailViewModel(TmdbClient tmdb, SourceSearchService sources, FavoritesStore favorites, ImageLoader images,
        WatchProgressStore progress, LocalLibrary local, SourceMatchCache sourceCache, DownloadManager downloads)
    {
        _tmdb = tmdb;
        _favorites = favorites;
        _images = images;
        _progress = progress;
        _local = local;
        _downloads = downloads;
        Sources = new SourcePanelViewModel(sources, sourceCache)
        {
            // Files on disk come first: "本地文件" is chosen whenever it has the title (and the season).
            LocalProvider = target => _item is not null && _local.Find(_item.MediaKey) is { } title
                ? LocalPlayback.Candidate(title, target.Season)
                : null,
        };
    }

    public SourcePanelViewModel Sources { get; }

    public MediaItem Item => _item ?? throw new InvalidOperationException("Not loaded.");

    public MediaDetail? Detail { get; private set; }

    public bool IsSeries => _item?.Kind == MediaKind.Tv;

    public string Title => _item?.Title ?? string.Empty;

    public string? BackdropUrl => _tmdb.ImageUrl(_item?.BackdropPath, "original");

    public string? PreviewUrl => _tmdb.ImageUrl(_item?.BackdropPath, "w780");

    [ObservableProperty]
    public partial string? LogoUrl { get; private set; }

    [ObservableProperty]
    public partial bool IsFavorite { get; private set; }

    [ObservableProperty]
    public partial SeasonSummary? SelectedSeason { get; private set; }

    public ObservableCollection<PersonViewModel> Cast { get; } = [];

    public ObservableCollection<MediaCardViewModel> Recommendations { get; } = [];

    public ObservableCollection<SeasonSummary> Seasons { get; } = [];

    public ObservableCollection<EpisodeViewModel> Episodes { get; } = [];

    public string? RatingText => _item?.RatingText;

    /// <summary>"2014 · 2 小时 49 分 · 科幻 / 冒险 · 美国" or "2024 · 2 季 · 36 集 · 剧情 · 中国大陆".</summary>
    public string MetaLine
    {
        get
        {
            if (_item is null) return string.Empty;
            var parts = new List<string>(5);
            if (_item.Year is { } y) parts.Add(y.ToString());

            if (_item.Kind == MediaKind.Movie)
            {
                if (Detail?.RuntimeMinutes is > 0 and var rt) parts.Add(rt >= 60 ? $"{rt / 60} 小时 {rt % 60} 分" : $"{rt} 分钟");
            }
            else if (Detail is not null)
            {
                if (Detail.Seasons.Count > 0) parts.Add($"{Detail.Seasons.Count} 季");
                if (Detail.EpisodeCount is > 0 and var ep) parts.Add($"{ep} 集");
            }

            var genres = Detail?.Genres ?? _item.GenreNames;
            if (genres.Count > 0) parts.Add(string.Join(" / ", genres.Take(3)));
            if (Detail?.Countries is { Count: > 0 } countries) parts.Add(countries[0]);
            return string.Join("  ·  ", parts);
        }
    }

    public string? Tagline => Detail?.Tagline;

    public string? Overview => Detail?.Item.Overview ?? _item?.Overview;

    public string DirectorLabel => IsSeries ? "主创" : "导演";

    public string? Directors => Detail is { Directors.Count: > 0 } d ? string.Join(" / ", d.Directors) : null;

    public string? Starring => Detail is { Cast.Count: > 0 } d ? string.Join(" / ", d.Cast.Take(4).Select(p => p.Name)) : null;

    /// <summary>Shows the page immediately from the list item, then fills in detail.</summary>
    public async Task LoadAsync(MediaItem item, int? preferredSeason = null)
    {
        _item = item;
        IsFavorite = _favorites.IsFavorite(item.MediaKey);
        OnPropertyChanged(string.Empty);

        if (item.Kind == MediaKind.Movie)
        {
            Sources.Start(BuildTarget(null));
        }

        var detail = await _tmdb.DetailAsync(item.Kind, item.TmdbId);
        if (detail is null)
        {
            return;
        }

        Detail = detail;
        OnPropertyChanged(string.Empty);

        foreach (var person in detail.Cast.Take(18)) Cast.Add(new PersonViewModel(person, _tmdb));
        foreach (var rec in detail.Recommendations) Recommendations.Add(new MediaCardViewModel(rec, _tmdb));

        _ = ResolveLogoAsync(detail.LogoPath);

        if (item.Kind == MediaKind.Tv)
        {
            foreach (var season in detail.Seasons) Seasons.Add(season);
            if (Seasons.Count > 0)
            {
                // Search hint first, then the season the user was last watching, then season one.
                var resumeSeason = _progress.Latest(item.MediaKey)?.Season;
                var start = Seasons.FirstOrDefault(s => s.Number == preferredSeason)
                            ?? Seasons.FirstOrDefault(s => s.Number == resumeSeason)
                            ?? Seasons[0];
                await SelectSeasonAsync(start);
            }
        }
    }

    public async Task SelectSeasonAsync(SeasonSummary season)
    {
        if (SelectedSeason == season)
        {
            return;
        }

        SelectedSeason = season;
        var version = ++_seasonVersion;
        Sources.Start(BuildTarget(season));

        var episodes = await _tmdb.SeasonAsync(Item.TmdbId, season.Number);
        if (version != _seasonVersion)
        {
            return;
        }

        Episodes.Clear();
        foreach (var e in episodes) Episodes.Add(new EpisodeViewModel(e, _tmdb, PlayEpisode, ep => DownloadQueued?.Invoke(Download([ep.Number - 1]))));
    }

    public void ToggleFavorite() => IsFavorite = _favorites.Toggle(Item);

    /// <summary>Progress that "播放" should pick up (half-watched, or a finished episode with a next one), if it
    /// belongs to what is on screen.</summary>
    public WatchProgress? Resume
    {
        get
        {
            if (_item is null || _progress.Latest(_item.MediaKey) is not { IsContinuable: true } latest) return null;
            return IsSeries && latest.Season != SelectedSeason?.Number ? null : latest;
        }
    }

    /// <summary>"继续 第 3 集" / "下一集 第 4 集" / "继续播放 1:02:13", or null when starting fresh.</summary>
    public string? ResumeLabel => Resume switch
    {
        null => null,
        { HasNextEpisode: true } r => $"下一集 第 {r.EpisodeIndex + 2} 集",
        { } r when IsSeries => $"继续 第 {r.EpisodeIndex + 1} 集",
        { } r => $"继续播放 {Playback.TimeText.Format(TimeSpan.FromMilliseconds(r.PositionMs))}",
    };

    public void RetrySources() => Sources.Retry(BuildTarget(SelectedSeason));

    /// <summary>Resumes where the user left off, otherwise starts at the first episode of the selected season.</summary>
    public void Play() => PlayIndex(Resume switch
    {
        { HasNextEpisode: true } r => r.EpisodeIndex + 1,
        { } r => r.EpisodeIndex,
        null => 0,
    });

    /// <summary>The online source downloads come from: the chosen one, or the best reachable one when the chosen
    /// source is the files on disk.</summary>
    public SourceItemViewModel? DownloadSource => Sources.Selected is { IsLocal: false } selected
        ? selected
        : Sources.Items.FirstOrDefault(i => !i.IsLocal && i.State is ProbeOutcome.Ok or ProbeOutcome.Slow);

    /// <summary>The episode "播放" would start (zero-based).</summary>
    public int NextIndex => Resume switch
    {
        { HasNextEpisode: true } r => r.EpisodeIndex + 1,
        { } r => r.EpisodeIndex,
        null => 0,
    };

    /// <summary>An episode card's "下载这一集": how many were added.</summary>
    public event Action<int>? DownloadQueued;

    /// <summary>Queues episodes (indexes into the source's list); returns how many were new.</summary>
    public int Download(IEnumerable<int> indexes)
    {
        if (DownloadSource is not { } source) return 0;
        var line = source.Candidate.PrimaryLine;
        var item = Detail?.Item ?? Item;
        return _downloads.Enqueue(indexes
            .Where(i => i >= 0 && i < line.Episodes.Count)
            .Select(i => new DownloadRequest(item, SelectedSeason?.Number, i,
                IsSeries ? $"第 {i + 1} 集" : line.Episodes[i].Name, line.Episodes[i].Url, source.SiteName)));
    }

    private void PlayEpisode(EpisodeViewModel episode) => PlayIndex(episode.Number - 1);

    private void PlayIndex(int index)
    {
        if (Sources.Selected is not { } source)
        {
            return;
        }

        var line = source.Candidate.PrimaryLine;
        Navigator.OpenPlayback(new PlaybackRequest(
            Item,
            Sources,
            source,
            Math.Clamp(index, 0, line.Episodes.Count - 1),
            SelectedSeason?.Number,
            Episodes.Select(e => e.Episode).ToArray(),
            IsAnimation: (Detail?.Item.GenreIds ?? Item.GenreIds).Contains(TmdbGenres.Animation)));
    }

    private SourceTarget BuildTarget(SeasonSummary? season)
    {
        var people = Detail is null
            ? []
            : Detail.Directors.Concat(Detail.Cast.Take(6).Select(p => p.Name)).ToArray();

        return new SourceTarget(
            Item.Title,
            Item.OriginalTitle,
            season?.Year ?? Item.Year,
            Item.Kind,
            season?.Number,
            season?.EpisodeCount,
            people);
    }

    private async Task ResolveLogoAsync(string? path)
    {
        var url = _tmdb.ImageUrl(path, "w500");
        if (url is null) return;

        var file = await _images.GetFileAsync(url, default, highPriority: true);
        if (file is not null && await ColorExtractor.OpaqueLuminanceAsync(file) >= MinLogoLuminance)
        {
            LogoUrl = url;
        }
    }
}

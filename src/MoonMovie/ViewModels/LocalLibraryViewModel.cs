using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using MoonMovie.Core.Local;
using MoonMovie.Core.Models;
using MoonMovie.Core.Tmdb;
using MoonMovie.Services;

namespace MoonMovie.ViewModels;

/// <summary>A poster in the 「本地」 tab: TMDB artwork when identified, the file-name title otherwise.</summary>
public sealed partial class LocalTitleViewModel(LocalTitle title, TmdbClient tmdb, Action<LocalTitleViewModel> fixMatch)
{
    public LocalTitle Model { get; } = title;

    public string Title => Model.Title;

    /// <summary>"剧集 · 第 1–2 季 · 24 集" / "电影 · 2019" / "… · 未识别".</summary>
    public string Subtitle
    {
        get
        {
            var parts = new List<string>(4) { Model.Kind == MediaKind.Tv ? "剧集" : "电影" };
            if ((Model.Match?.Year ?? Model.Year) is { } y) parts.Add(y.ToString());
            if (Model.IsSeries)
            {
                var seasons = Model.Seasons;
                if (seasons.Count > 1) parts.Add($"{seasons.Count} 季");
                parts.Add($"{Model.Files.Count} 集");
            }
            else if (Model.Files.Count > 1)
            {
                parts.Add($"{Model.Files.Count} 个文件");
            }

            if (!Model.IsMatched) parts.Add("未识别");
            return string.Join(" · ", parts);
        }
    }

    public string? PosterUrl => tmdb.ImageUrl(Model.Match?.PosterPath, "w342");

    public string? AmbientUrl => tmdb.ImageUrl(Model.Match?.BackdropPath, "w1280");

    public Visibility PlaceholderVisibility => PosterUrl is null ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DetailsVisibility => Model.IsMatched ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Big letter on the placeholder poster.</summary>
    public string Initial => Model.Title.Length > 0 ? Model.Title[..1].ToUpperInvariant() : "?";

    /// <summary>The file name the title came from, under the placeholder.</summary>
    public string SourceName => Model.Files[0].FileName;

    /// <summary>Identified titles open their detail page (with 「本地文件」 as the source); others just play.</summary>
    [RelayCommand]
    private void Open()
    {
        if (Model.IsMatched) Navigator.OpenMedia(Model.ToMediaItem());
        else _ = LocalPlayback.PlayAsync(Model);
    }

    [RelayCommand]
    private void Play() => _ = LocalPlayback.PlayAsync(Model);

    [RelayCommand]
    private void Details() => Navigator.OpenMedia(Model.ToMediaItem());

    [RelayCommand]
    private void FixMatch() => fixMatch(this);

    [RelayCommand]
    private void Reveal() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{Model.Files[0].Path}\"")
        {
            UseShellExecute = true,
        });
}

public sealed partial class LocalFolderViewModel(string path, Action<LocalFolderViewModel> remove)
{
    public string Path { get; } = path;

    public string Name => System.IO.Path.GetFileName(Path) is { Length: > 0 } name ? name : Path;

    public bool Exists => Directory.Exists(Path);

    public double ContentOpacity => Exists ? 1 : 0.5;

    public string ToolTip => Exists ? Path : $"{Path}（未连接）";

    [RelayCommand]
    private void Remove() => remove(this);

    [RelayCommand]
    private void Open() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{Path}\"") { UseShellExecute = true });
}

/// <summary>The 「本地」 tab: watched folders, scan status and the titles found in them.</summary>
public sealed partial class LocalLibraryViewModel : ObservableObject
{
    private readonly LocalLibrary _library;
    private readonly TmdbClient _tmdb;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private bool _refreshQueued;

    public LocalLibraryViewModel(LocalLibrary library, TmdbClient tmdb)
    {
        _library = library;
        _tmdb = tmdb;
    }

    public ObservableCollection<LocalTitleViewModel> Titles { get; } = [];

    public ObservableCollection<LocalFolderViewModel> Folders { get; } = [];

    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsScanning { get; private set; }

    public LocalLibrary Library => _library;

    /// <summary>Raised when a poster's "修改匹配" is chosen.</summary>
    public event Action<LocalTitleViewModel>? FixMatchRequested;

    public void Refresh()
    {
        Titles.Clear();
        foreach (var title in _library.Titles) Titles.Add(new LocalTitleViewModel(title, _tmdb, t => FixMatchRequested?.Invoke(t)));

        Folders.Clear();
        foreach (var folder in _library.Folders) Folders.Add(new LocalFolderViewModel(folder, f => _library.RemoveFolder(f.Path)));

        IsScanning = _library.IsScanning;
        var unmatched = Titles.Count(t => !t.Model.IsMatched);
        StatusText = IsScanning ? "正在扫描…"
            : Folders.Count == 0 ? string.Empty
            : $"{Titles.Count} 部 · {_library.FileCount} 个文件" + (unmatched > 0 ? $" · {unmatched} 部未识别" : string.Empty);
        OnPropertyChanged(nameof(Titles));
    }

    public async Task AddFolderAsync()
    {
        if (await LocalPlayback.PickFolderAsync() is { } folder) await _library.AddFolderAsync(folder);
    }

    public Task RescanAsync() => _library.RescanAsync();

    /// <summary>Follow library changes while the page is on screen.</summary>
    public void Attach()
    {
        _library.Changed -= OnLibraryChanged;
        _library.Changed += OnLibraryChanged;
    }

    public void Detach() => _library.Changed -= OnLibraryChanged;

    /// <summary>Scans report often; coalesce into one refresh per UI turn.</summary>
    private void OnLibraryChanged()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        _dispatcher.TryEnqueue(() =>
        {
            _refreshQueued = false;
            Refresh();
            Changed?.Invoke();
        });
    }

    /// <summary>Raised on the UI thread after a refresh caused by the library.</summary>
    public event Action? Changed;

    /// <summary>TMDB candidates for the match dialog.</summary>
    public async Task<IReadOnlyList<MediaCardViewModel>> SearchAsync(string query, MediaKind kind)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        try
        {
            var results = await _tmdb.SearchAsync(kind, query.Trim());
            return results.Take(12).Select(r => new MediaCardViewModel(r, _tmdb)).ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return [];
        }
    }

    public void ApplyMatch(LocalTitle title, MediaItem? item) => _library.SetMatch(title.Key, item);
}

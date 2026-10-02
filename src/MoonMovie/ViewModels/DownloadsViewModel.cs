using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using MoonMovie.Core.Downloads;
using MoonMovie.Core.Models;
using MoonMovie.Core.Tmdb;
using MoonMovie.Services;

namespace MoonMovie.ViewModels;

/// <summary>A row on the 下载 page; <see cref="Refresh"/> re-reads the live item.</summary>
public sealed partial class DownloadItemViewModel(DownloadItem item, DownloadManager manager, TmdbClient tmdb) : ObservableObject
{
    public DownloadItem Item { get; } = item;

    public string Title => Item.Title;

    /// <summary>"第 1 季 · 第 3 集 · 来自 量子资源" / "来自 量子资源".</summary>
    public string Subtitle
    {
        get
        {
            var parts = new List<string>(3);
            if (Item.Kind == MediaKind.Tv)
            {
                if (Item.Season is > 1 and var s) parts.Add($"第 {s} 季");
                parts.Add($"第 {Item.EpisodeIndex + 1} 集");
            }
            else if (Item.EpisodeIndex > 0 && Item.EpisodeLabel is { Length: > 0 } label)
            {
                parts.Add(label);
            }

            parts.Add($"来自 {Item.SiteName}");
            return string.Join(" · ", parts);
        }
    }

    public string? ThumbUrl => tmdb.ImageUrl(Item.BackdropPath ?? Item.PosterPath, "w300");

    public double ProgressValue => Item.Progress * 100;

    public bool IsActive => Item.State is DownloadState.Queued or DownloadState.Running;

    public bool IsCompleted => Item.State == DownloadState.Completed;

    public Visibility ProgressVisibility => IsCompleted ? Visibility.Collapsed : Visibility.Visible;

    public Visibility PlayVisibility => IsCompleted ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ToggleVisibility => IsCompleted ? Visibility.Collapsed : Visibility.Visible;

    public string ToggleGlyph => IsActive ? "" : Item.State == DownloadState.Failed ? "" : "";

    public string ToggleLabel => IsActive ? "暂停" : Item.State == DownloadState.Failed ? "重试" : "继续";

    public bool ShowPaused => Item.State is DownloadState.Paused or DownloadState.Failed;

    /// <summary>"6.2 MB/s · 还剩 2 分钟 · 41%" / "等待中" / "已暂停 · 41%" / "1.3 GB · 10月2日" / "失败：…".</summary>
    public string StatusText => Item.State switch
    {
        DownloadState.Queued => Item.Progress > 0 ? $"等待中 · {Item.Progress:P0}" : "等待中",
        DownloadState.Running when Item.BytesPerSecond > 0 => $"{Size(Item.BytesPerSecond)}/s · {Remaining()} · {Item.Progress:P0}",
        DownloadState.Running => $"正在连接… · {Item.Progress:P0}",
        DownloadState.Paused => $"已暂停 · {Item.Progress:P0}",
        DownloadState.Failed => $"失败：{Item.Error}",
        _ => $"{Size(Item.BytesTotal)} · {Item.CompletedAt?.ToLocalTime():M月d日}",
    };

    private string Remaining()
    {
        if (Item.BytesTotal <= 0 || Item.BytesPerSecond <= 0) return "估算中";
        var seconds = Math.Max(0, (Item.BytesTotal - Item.BytesDone) / Item.BytesPerSecond);
        return seconds < 60 ? "不到 1 分钟" : seconds < 3600 ? $"还剩 {Math.Ceiling(seconds / 60)} 分钟" : $"还剩 {seconds / 3600:0.#} 小时";
    }

    public static string Size(double bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (1L << 20):0.0} MB",
        _ => $"{bytes / 1024:0} KB",
    };

    public void Refresh() => OnPropertyChanged(string.Empty);

    [RelayCommand]
    private void Toggle()
    {
        if (IsActive) manager.Pause(Item.Id);
        else manager.Resume(Item.Id);
    }

    [RelayCommand]
    private void Play()
    {
        if (Item.OutputPath is { } path) _ = LocalPlayback.OpenPathsAsync([path]);
    }

    [RelayCommand]
    private void Reveal()
    {
        var target = Item.OutputPath is { } path && File.Exists(path) ? $"/select,\"{path}\"" : $"\"{manager.Folder}\"";
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", target) { UseShellExecute = true });
    }

    [RelayCommand]
    private void Remove() => manager.Remove(Item.Id, deleteFile: false);

    [RelayCommand]
    private void RemoveWithFile() => manager.Remove(Item.Id, deleteFile: true);
}

/// <summary>The 下载 page: every task, newest first, ticking while anything is running.</summary>
public sealed partial class DownloadsViewModel : ObservableObject
{
    private readonly DownloadManager _manager;
    private readonly TmdbClient _tmdb;
    private readonly DispatcherQueueTimer _tick;
    private bool _listDirty = true;
    private volatile bool _itemsDirty;

    public DownloadsViewModel(DownloadManager manager, TmdbClient tmdb)
    {
        _manager = manager;
        _tmdb = tmdb;
        _tick = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _tick.Interval = TimeSpan.FromMilliseconds(500);
        _tick.Tick += (_, _) => SafeDispatch.Run(Update);
    }

    public ObservableCollection<DownloadItemViewModel> Items { get; } = [];

    public DownloadManager Manager => _manager;

    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    public event Action? ListChanged;

    public void Attach()
    {
        _manager.Changed += OnChanged;
        _listDirty = true;
        Update();
        _tick.Start();
    }

    public void Detach()
    {
        _manager.Changed -= OnChanged;
        _tick.Stop();
    }

    private void OnChanged(DownloadItem? item)
    {
        if (item is null) _listDirty = true;
        else _itemsDirty = true;
    }

    private void Update()
    {
        if (_listDirty)
        {
            _listDirty = false;
            Items.Clear();
            foreach (var item in _manager.Items.OrderByDescending(i => i.CreatedAt))
            {
                Items.Add(new DownloadItemViewModel(item, _manager, _tmdb));
            }

            ListChanged?.Invoke();
        }
        else if (_itemsDirty)
        {
            _itemsDirty = false;
            foreach (var vm in Items) vm.Refresh();
        }

        var done = Items.Where(i => i.IsCompleted).ToArray();
        var active = Items.Count(i => i.IsActive);
        var parts = new List<string> { $"保存在 {_manager.Folder}" };
        if (active > 0) parts.Add($"{active} 个进行中");
        if (done.Length > 0) parts.Add($"已下载 {done.Length} 个 · {DownloadItemViewModel.Size(done.Sum(d => d.Item.BytesTotal))}");
        Summary = string.Join(" · ", parts);
    }
}

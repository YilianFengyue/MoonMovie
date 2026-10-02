using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using MoonMovie.Core.Bilibili;
using MoonMovie.Core.Library;
using MoonMovie.Core.Local;
using MoonMovie.Core.Playback;
using MoonMovie.Core.Tmdb;
using MoonMovie.Services;

namespace MoonMovie.ViewModels;

/// <summary>个人主页: who you are here, what you have watched, and the linked B站 account.</summary>
public sealed partial class ProfileViewModel : ObservableObject
{
    private readonly ProfileStore _profile;
    private readonly WatchProgressStore _progress;
    private readonly FavoritesStore _favorites;
    private readonly LocalLibrary _local;
    private readonly BiliAccountService _bili;
    private readonly TmdbClient _tmdb;

    public ProfileViewModel(ProfileStore profile, WatchProgressStore progress, FavoritesStore favorites, LocalLibrary local,
        BiliAccountService bili, TmdbClient tmdb)
    {
        _profile = profile;
        _progress = progress;
        _favorites = favorites;
        _local = local;
        _bili = bili;
        _tmdb = tmdb;
    }

    public ObservableCollection<WatchedItemViewModel> Recent { get; } = [];

    public ObservableCollection<MediaCardViewModel> Favorites { get; } = [];

    public string Name => _profile.DisplayName;

    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "M";

    /// <summary>The chosen picture, or the B站 avatar when linked and preferred.</summary>
    public string? AvatarUrl => _profile.Current.UseBiliAvatar && Bili is { Face: { } face }
        ? BiliClient.Thumb(face, 240, 240)
        : _profile.Current.AvatarFile is { } file && File.Exists(file) ? file : null;

    /// <summary>"看过 32 部 · 累计 48 小时 · 收藏 12 · 本地 4 部".</summary>
    public string Stats { get; private set; } = string.Empty;

    public string SinceText => $"{_profile.Current.Since.ToLocalTime():yyyy 年 M 月}开始使用 MoonMovie";

    public BiliAccount? Bili => _bili.Account;

    public bool IsBiliLinked => _bili.IsLinked;

    public Visibility LinkedVisibility => Bili is not null ? Visibility.Visible : Visibility.Collapsed;

    public Visibility UnlinkedVisibility => Bili is null ? Visibility.Visible : Visibility.Collapsed;

    public string? BiliFace => BiliClient.Thumb(Bili?.Face, 96, 96);

    public string BiliName => Bili?.Name ?? string.Empty;

    public string BiliLevel => Bili is { Level: > 0 } b ? $"Lv{b.Level}" : string.Empty;

    public string? BiliVip => Bili is { IsVip: true } b ? b.VipLabel ?? "大会员" : null;

    public Visibility VipVisibility => BiliVip is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>What linking gives you right now.</summary>
    public string BiliCaption => Bili is { IsVip: true }
        ? "1080P 高码率、4K 与 HDR 画质 · 观看进度同步到 B站 · 全部评论"
        : Bili is not null
            ? "1080P 画质 · 观看进度同步到 B站 · 全部评论"
            : "关联后可看 1080P 与大会员画质，观看进度同步到 B站历史，还能查看全部评论";

    public void Attach()
    {
        _bili.Changed += OnAccountChanged;
        _profile.Changed += OnProfileChanged;
        Refresh();
    }

    public void Detach()
    {
        _bili.Changed -= OnAccountChanged;
        _profile.Changed -= OnProfileChanged;
    }

    public void Refresh()
    {
        Recent.Clear();
        foreach (var p in _progress.Recent(12)) Recent.Add(new WatchedItemViewModel(p, _tmdb));

        Favorites.Clear();
        var favorites = _favorites.All();
        foreach (var f in favorites.Take(20)) Favorites.Add(new MediaCardViewModel(f.ToMediaItem(), _tmdb));

        var all = _progress.All();
        var titles = all.Select(p => p.MediaKey).Distinct().Count();
        var hours = all.Sum(p => p.PositionMs) / 3_600_000.0;
        var parts = new List<string>(4) { $"看过 {titles} 部" };
        if (hours >= 1) parts.Add($"累计 {hours:0} 小时");
        else if (hours > 0) parts.Add($"累计 {Math.Max(1, hours * 60):0} 分钟");
        if (favorites.Count > 0) parts.Add($"收藏 {favorites.Count}");
        if (_local.Titles.Count > 0) parts.Add($"本地 {_local.Titles.Count} 部");
        Stats = string.Join(" · ", parts);
        OnPropertyChanged(string.Empty);
    }

    public void SetNickname(string? name)
    {
        _profile.Current.Nickname = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        _profile.Save();
    }

    public void SetAvatar(string file) => _profile.SetAvatar(file);

    public void UseBiliAvatar()
    {
        _profile.Current.UseBiliAvatar = true;
        _profile.Save();
    }

    public void ClearAvatar()
    {
        _profile.Current.AvatarFile = null;
        _profile.Current.UseBiliAvatar = false;
        _profile.Save();
    }

    public Task UnlinkBiliAsync() => _bili.LogoutAsync();

    private void OnAccountChanged() => OnPropertyChanged(string.Empty);

    private void OnProfileChanged(object? sender, EventArgs e) => OnPropertyChanged(string.Empty);
}

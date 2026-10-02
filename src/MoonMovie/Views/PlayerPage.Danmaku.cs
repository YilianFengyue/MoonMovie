using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MoonMovie.Controls;
using MoonMovie.Core.Danmaku;
using MoonMovie.Core.Settings;
using Windows.Media.Playback;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace MoonMovie.Views;

/// <summary>Player: bullet comments — loading per episode, the 弹幕 panel, and keeping the overlay on the clock.</summary>
public sealed partial class PlayerPage
{
    private static readonly (string Label, double Value)[] Areas = [("1/4", 0.25), ("半屏", 0.5), ("3/4", 0.75), ("满屏", 1.0)];

    private readonly DanmakuLibrary _danmaku = App.Services.GetRequiredService<DanmakuLibrary>();
    private readonly SettingsStore _settings = App.Services.GetRequiredService<SettingsStore>();
    private readonly List<(Button Chip, double Value)> _areaChips = [];
    private Button _topChip = null!;
    private Button _bottomChip = null!;
    private Button _mergeChip = null!;
    private DispatcherQueueTimer? _saveTimer;
    private CancellationTokenSource? _danmakuCts;
    private DanmakuTrack? _danmakuTrack;
    private int? _danmakuEpisode;
    // True until the panel is first synced: sliders raise ValueChanged while XAML is still being parsed.
    private bool _syncingDanmakuUi = true;
    private bool _candidatesLoaded;

    private DanmakuSettings DanmakuPrefs => _settings.Current.Danmaku;

    private void InitDanmaku()
    {
        foreach (var (label, value) in Areas)
        {
            var chip = Chips.Create(label, () =>
            {
                DanmakuPrefs.Area = value;
                OnDanmakuPrefsChanged();
            });
            _areaChips.Add((chip, value));
            AreaChips.Children.Add(chip);
        }

        _topChip = Chips.Create("顶部", () => { DanmakuPrefs.ShowTop = !DanmakuPrefs.ShowTop; OnDanmakuPrefsChanged(); });
        _bottomChip = Chips.Create("底部", () => { DanmakuPrefs.ShowBottom = !DanmakuPrefs.ShowBottom; OnDanmakuPrefsChanged(); });
        _mergeChip = Chips.Create("合并重复", () =>
        {
            DanmakuPrefs.MergeDuplicates = !DanmakuPrefs.MergeDuplicates;
            OnDanmakuPrefsChanged();
            RefilterDanmaku();
        });
        KindChips.Children.Add(_topChip);
        KindChips.Children.Add(_bottomChip);
        KindChips.Children.Add(_mergeChip);
        DanmakuPowerChip.Content = Chips.Content("已开启");
    }

    private void StartDanmaku()
    {
        Danmaku.ApplySettings(DanmakuPrefs);
        Danmaku.IsOn = DanmakuPrefs.Enabled;
        SyncDanmakuUi();
        UpdateOffsetText();
        SetDanmakuStatus(_danmaku.Client.IsConfigured ? "弹幕" : "未配置弹幕服务器",
            _danmaku.Client.IsConfigured ? string.Empty : "在 设置 → 弹幕 中填写服务器地址", busy: false);
    }

    private void StopDanmaku()
    {
        _danmakuCts?.Cancel();
        _saveTimer?.Stop();
        Danmaku.Clear();
    }

    /// <summary>Called whenever an episode opens; a source switch on the same episode keeps the loaded track.</summary>
    private async void LoadDanmakuForEpisode()
    {
#if DEBUG
        // Visual QA hook: MOONMOVIE_DEBUG_DANMAKU=1 feeds a synthetic, dense track (no server needed).
        if (Environment.GetEnvironmentVariable("MOONMOVIE_DEBUG_DANMAKU") == "1" && _danmakuEpisode != _episodeIndex)
        {
            _danmakuEpisode = _episodeIndex;
            var rng = new Random(7);
            string[] samples = ["前方高能", "哈哈哈哈哈哈", "这段太好看了", "名场面！", "BGM 一响泪目了", "来了来了",
                "第一次看，好紧张", "导演是懂镜头的", "空降成功", "这演技绝了", "画质好清晰", "awsl", "笑死我了这个表情",
                "这里有伏笔吧", "二刷打卡"];
            uint[] colors = [0xFFFFFF, 0xFFFFFF, 0xFFFFFF, 0xFFFFFF, 0xFE0302, 0xFFC107, 0x00CD00, 0x40C4FF, 0xCC0273];
            var raw = Enumerable.Range(0, 6000).Select(_ =>
            {
                var roll = rng.NextDouble();
                var mode = roll < 0.06 ? DanmakuMode.Top : roll < 0.1 ? DanmakuMode.Bottom : DanmakuMode.Scroll;
                return new DanmakuComment(rng.NextDouble() * 1800, mode, colors[rng.Next(colors.Length)], samples[rng.Next(samples.Length)]);
            }).OrderBy(c => c.Time).ToArray();
            ApplyTrack(new DanmakuTrack(new DanmakuMatch(0, "测试弹幕", "合成数据"), _danmaku.Filter(raw), raw), announce: true);
            return;
        }
#endif
        if (_danmakuEpisode == _episodeIndex || !_danmaku.Client.IsConfigured) return;
        _danmakuEpisode = _episodeIndex;
        _danmakuCts?.Cancel();
        var cts = _danmakuCts = new CancellationTokenSource();
        _danmakuTrack = null;
        _candidatesLoaded = false;
        CandidatesList.Children.Clear();
        Danmaku.Clear();
        Danmaku.Offset = 0;
        UpdateOffsetText();

        SetDanmakuStatus("正在匹配弹幕…", "首次匹配可能需要十几秒", busy: true);
        try
        {
            var track = await Task.Run(() => _danmaku.LoadAsync(DanmakuRequestFor(_episodeIndex), cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            ApplyTrack(track, announce: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
                                       or InvalidOperationException)
        {
            if (cts.IsCancellationRequested) return;
            SetDanmakuStatus("弹幕加载失败", ex is TaskCanceledException ? "弹幕服务器响应超时" : ex.Message, busy: false);
        }
    }

    private DanmakuRequest DanmakuRequestFor(int episodeIndex) => new(
        _request.Item.MediaKey,
        _request.Item.Title,
        _request.Item.Year,
        _request.Item.Kind,
        _request.Season,
        episodeIndex);

    private void ApplyTrack(DanmakuTrack? track, bool announce)
    {
        _danmakuTrack = track;
        if (track is null || track.Raw.Count == 0)
        {
            Danmaku.Clear();
            SetDanmakuStatus("没有找到弹幕", "可以在下方换一个来源试试", busy: false);
            if (_sideOpen && PanelTabs.SelectedItem == DanmakuTab) _ = LoadCandidatesAsync(null);
            return;
        }

        Danmaku.SetComments(track.Comments);
        SetDanmakuStatus($"{track.Comments.Count:N0} 条弹幕", MatchLabel(track.Match), busy: false);
        if (announce && DanmakuPrefs.Enabled) ShowToast($"已加载 {track.Comments.Count:N0} 条弹幕");
    }

    private static string MatchLabel(DanmakuMatch match)
    {
        var parts = new[] { match.AnimeTitle, match.EpisodeTitle }.Where(p => !string.IsNullOrWhiteSpace(p));
        return string.Join(" · ", parts);
    }

    private void RefilterDanmaku()
    {
        if (_danmakuTrack is not { } track) return;
        var filtered = _danmaku.Filter(track.Raw);
        _danmakuTrack = track with { Comments = filtered };
        Danmaku.SetComments(filtered);
        SetDanmakuStatus($"{filtered.Count:N0} 条弹幕", MatchLabel(track.Match), busy: false);
    }

    private void SetDanmakuStatus(string title, string detail, bool busy)
    {
        DanmakuStatusTitle.Text = title;
        DanmakuStatusDetail.Text = detail;
        DanmakuStatusDetail.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
        DanmakuRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Keeps the overlay on the media clock (called from the tick, state changes and seeks).</summary>
    private void SyncDanmaku(TimeSpan? position = null)
    {
        if (_player is null) return;
        var session = _player.PlaybackSession;
        Danmaku.Sync(position ?? session.Position, session.PlaybackState == MediaPlaybackState.Playing, session.PlaybackRate);
    }

    // ----- Switch and panel -------------------------------------------------------------------------------

    private void ToggleDanmaku()
    {
        DanmakuPrefs.Enabled = !DanmakuPrefs.Enabled;
        Danmaku.IsOn = DanmakuPrefs.Enabled;
        SyncDanmakuUi();
        ScheduleSave();
        ShowHud("", DanmakuPrefs.Enabled ? 1 : 0, DanmakuPrefs.Enabled ? "弹幕已开启" : "弹幕已关闭");
    }

    private void OnDanmakuToggleClick(object sender, RoutedEventArgs e) => ToggleDanmaku();

    private void OnDanmakuSettingsClick(object sender, RoutedEventArgs e) => OpenSidePanel(DanmakuTab);

    private void OnDanmakuSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingDanmakuUi) return;
        DanmakuPrefs.Opacity = OpacitySlider.Value / 100;
        DanmakuPrefs.FontScale = FontSlider.Value / 100;
        DanmakuPrefs.Speed = DanmakuSpeedSlider.Value / 100;
        OnDanmakuPrefsChanged();
    }

    private void OnDanmakuPrefsChanged()
    {
        Danmaku.ApplySettings(DanmakuPrefs);
        SyncDanmakuUi();
        ScheduleSave();
    }

    /// <summary>Sliders fire continuously while dragged; write the file once things settle.</summary>
    private void ScheduleSave()
    {
        _saveTimer ??= CreateTimer(TimeSpan.FromMilliseconds(500), repeating: false, _settings.Save);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SyncDanmakuUi()
    {
        _syncingDanmakuUi = true;
        var p = DanmakuPrefs;
        OpacitySlider.Value = Math.Round(p.Opacity * 100);
        FontSlider.Value = Math.Round(p.FontScale * 100);
        DanmakuSpeedSlider.Value = Math.Round(p.Speed * 100);
        OpacityValue.Text = $"{Math.Round(p.Opacity * 100)}%";
        FontValue.Text = $"{Math.Round(p.FontScale * 100)}%";
        SpeedValue.Text = $"{p.Speed:0.0#}x";
        foreach (var (chip, value) in _areaChips) Chips.Set(chip, Math.Abs(p.Area - value) < 0.01);
        Chips.Set(_topChip, p.ShowTop);
        Chips.Set(_bottomChip, p.ShowBottom);
        Chips.Set(_mergeChip, p.MergeDuplicates);
        Chips.Set(DanmakuPowerChip, p.Enabled);
        Chips.SetLabel(DanmakuPowerChip, p.Enabled ? "已开启" : "已关闭");

        // Control-bar badge: filled when on, outlined and dimmed when off.
        DanmakuBadge.Background = p.Enabled ? new SolidColorBrush(Microsoft.UI.Colors.White) : null;
        DanmakuBadgeText.Foreground = new SolidColorBrush(p.Enabled ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White);
        DanmakuBadge.Opacity = p.Enabled ? 1 : 0.6;
        _syncingDanmakuUi = false;
    }

    private void OnOffsetEarlier(object sender, RoutedEventArgs e) => NudgeOffset(-0.5);

    private void OnOffsetLater(object sender, RoutedEventArgs e) => NudgeOffset(0.5);

    private void NudgeOffset(double delta)
    {
        Danmaku.Offset = Math.Round(Danmaku.Offset + delta, 1);
        UpdateOffsetText();
    }

    private void UpdateOffsetText() =>
        OffsetText.Text = Danmaku.Offset switch
        {
            0 => "同步",
            > 0 => $"延后 {Danmaku.Offset:0.0} 秒",
            _ => $"提前 {-Danmaku.Offset:0.0} 秒",
        };

    // ----- Choosing another source show -----------------------------------------------------------------

    private void OnDanmakuSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        var keyword = DanmakuSearchBox.Text.Trim();
        if (keyword.Length > 0) _ = LoadCandidatesAsync(keyword);
    }

    private async Task LoadCandidatesAsync(string? keyword)
    {
        if (!_danmaku.Client.IsConfigured) return;
        keyword ??= _request.Item.Title;
        _candidatesLoaded = true;
        CandidatesRing.Visibility = Visibility.Visible;
        CandidatesEmpty.Visibility = Visibility.Collapsed;
        CandidatesList.Children.Clear();
        try
        {
            var animes = await Task.Run(() => _danmaku.CandidatesAsync(keyword));
            foreach (var anime in animes.Take(30)) CandidatesList.Children.Add(CandidateRow(anime));
            if (animes.Count == 0)
            {
                CandidatesEmpty.Text = $"弹幕服务器没有找到“{keyword}”";
                CandidatesEmpty.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            CandidatesEmpty.Text = "搜索失败：弹幕服务器没有响应";
            CandidatesEmpty.Visibility = Visibility.Visible;
        }
        finally
        {
            CandidatesRing.Visibility = Visibility.Collapsed;
        }
    }

    private Button CandidateRow(DanmakuAnime anime)
    {
        var meta = new[]
        {
            anime.TypeDescription,
            anime.EpisodeCount > 0 ? $"{anime.EpisodeCount} 集" : null,
            anime.StartDate is { Length: >= 4 } d ? d[..4] : null,
            anime.Source,
        }.Where(s => !string.IsNullOrWhiteSpace(s));

        var panel = new StackPanel { Padding = new Thickness(10, 8, 10, 8), Spacing = 2 };
        panel.Children.Add(new TextBlock
        {
            Text = anime.Title,
            FontSize = 13,
            Foreground = (Brush)Application.Current.Resources["MoonTextPrimaryBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        panel.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", meta),
            Style = (Style)Application.Current.Resources["MoonCardMetaStyle"],
        });

        var row = new Button { Content = panel, Style = (Style)Application.Current.Resources["MoonListRowButtonStyle"] };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row, anime.Title);
        row.Click += async (_, _) =>
        {
            _danmakuCts?.Cancel();
            var cts = _danmakuCts = new CancellationTokenSource();
            SetDanmakuStatus("正在加载弹幕…", anime.Title, busy: true);
            try
            {
                var track = await Task.Run(() => _danmaku.ChooseAsync(DanmakuRequestFor(_episodeIndex), anime, cts.Token), cts.Token);
                if (!cts.IsCancellationRequested) ApplyTrack(track, announce: true);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                if (!cts.IsCancellationRequested) SetDanmakuStatus("弹幕加载失败", "弹幕服务器没有响应", busy: false);
            }
        };
        return row;
    }

    private void OnDanmakuTabShown()
    {
        if (!_candidatesLoaded) _ = LoadCandidatesAsync(null);
    }
}

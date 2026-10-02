using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MoonMovie.Controls;
using MoonMovie.Core.Models;
using MoonMovie.Core.Playback;
using MoonMovie.Core.Settings;
using MoonMovie.Playback;
using MoonMovie.Playback.Engines;

namespace MoonMovie.Views;

/// <summary>
/// Player: the 画面 / 声音 / 字幕 panels (mpv only), intro/outro skipping, chapter marks and the info overlay.
/// Global choices (quality, night mode, subtitle style…) are saved to settings; per-title ones (sync offsets,
/// intro/outro) to <see cref="TitlePrefsStore"/>; the rest (aspect, picture adjustments) last for the session.
/// </summary>
public sealed partial class PlayerPage
{
    private static readonly (string Label, QualityPreset Value)[] QualityLevels =
        [("自动", QualityPreset.Auto), ("性能", QualityPreset.Performance), ("均衡", QualityPreset.Balanced), ("画质", QualityPreset.Quality)];

    private static readonly (string Label, string Aspect, bool Fill)[] Aspects =
        [("原始", "-1", false), ("16:9", "16:9", false), ("4:3", "4:3", false), ("2.35:1", "2.35:1", false), ("铺满", "-1", true)];

    private static readonly (string Label, string Property)[] Adjustments =
        [("亮度", "brightness"), ("对比度", "contrast"), ("饱和度", "saturation"), ("伽马", "gamma")];

    private readonly TitlePrefsStore _titlePrefs = App.Services.GetRequiredService<TitlePrefsStore>();
    private readonly List<(Button Chip, QualityPreset Value)> _qualityChips = [];
    private readonly List<(Button Chip, int Index)> _aspectChips = [];
    private readonly List<Slider> _adjustSliders = [];
    private Button? _interpolationChip;
    private Button? _hdrChip;
    private Button? _nightChip;
    private Button? _passthroughChip;
    private Button? _subBackgroundChip;
    private Button? _subOverrideChip;
    private Slider? _subScaleSlider;
    private Slider? _subPositionSlider;
    private int _aspectIndex;
    private bool _panelsBuilt;
    private bool _introHandled;
    private bool _outroHandled;

    private MpvEngine? Mpv => _engine as MpvEngine;

    private string TitleKey => TitlePrefsStore.Key(_request.Item.MediaKey, _request.Season);

    private TitlePrefs Prefs => _titlePrefs.Get(TitleKey);

    // ----- Setup ------------------------------------------------------------------------------------------

    /// <summary>The extra tabs only make sense on mpv; the system engine keeps the original three.</summary>
    private void ConfigureEnginePanels()
    {
        if (Mpv is null)
        {
            PanelTabs.Items.Remove(PictureTab);
            PanelTabs.Items.Remove(AudioTab);
            PanelTabs.Items.Remove(SubtitleTab);
            return;
        }

        if (_panelsBuilt) return;
        _panelsBuilt = true;
        var settings = _settings.Current;

        foreach (var (label, value) in QualityLevels)
        {
            var chip = Chips.Create(label, () =>
            {
                settings.Video.Quality = value;
                Mpv?.ApplyQuality(value);
                ScheduleSave();
                SyncPictureUi();
            });
            _qualityChips.Add((chip, value));
            QualityChips.Children.Add(chip);
        }

        _interpolationChip = Chips.Create("运动平滑", () =>
        {
            settings.Video.Interpolation = !settings.Video.Interpolation;
            Mpv?.SetInterpolation(settings.Video.Interpolation);
            ScheduleSave();
            SyncPictureUi();
        });
        _hdrChip = Chips.Create("HDR 直通", () =>
        {
            settings.Video.HdrPassthrough = !settings.Video.HdrPassthrough;
            Mpv?.ApplySettings(settings);
            ScheduleSave();
            SyncPictureUi();
        });
        EnhanceChips.Children.Add(_interpolationChip);
        EnhanceChips.Children.Add(_hdrChip);

        for (var i = 0; i < Aspects.Length; i++)
        {
            var index = i;
            var chip = Chips.Create(Aspects[i].Label, () =>
            {
                _aspectIndex = index;
                Mpv?.SetAspect(Aspects[index].Aspect);
                Mpv?.SetFill(Aspects[index].Fill);
                SyncPictureUi();
            });
            _aspectChips.Add((chip, i));
            AspectChips.Children.Add(chip);
        }

        foreach (var (label, property) in Adjustments)
        {
            var slider = LabeledSlider(AdjustSliders, label, -50, 50, 0, v => $"{v:+0;-0;0}",
                v => Mpv?.SetAdjust(property, (int)v));
            _adjustSliders.Add(slider);
        }

        ToolChips.Children.Add(Chips.Create("截图", () => TakeScreenshot(withSubtitles: false)));
        ToolChips.Children.Add(Chips.Create("含字幕截图", () => TakeScreenshot(withSubtitles: true)));
        ToolChips.Children.Add(Chips.Create("播放信息", ToggleInfoPanel));

        _nightChip = Chips.Create("夜间模式", () =>
        {
            settings.Audio.NightMode = !settings.Audio.NightMode;
            Mpv?.SetNightMode(settings.Audio.NightMode);
            ScheduleSave();
            SyncAudioUi();
        });
        _passthroughChip = Chips.Create("源码输出", () =>
        {
            settings.Audio.Passthrough = !settings.Audio.Passthrough;
            Mpv?.ApplySettings(settings);
            ScheduleSave();
            SyncAudioUi();
        });
        AudioChips.Children.Add(_nightChip);
        AudioChips.Children.Add(_passthroughChip);

        _subScaleSlider = LabeledSlider(SubtitleSliders, "字号", 50, 250, settings.Subtitles.Scale * 100, v => $"{v:0}%", v =>
        {
            settings.Subtitles.Scale = v / 100;
            Mpv?.ApplySubtitleStyle(settings.Subtitles);
            ScheduleSave();
        });
        _subPositionSlider = LabeledSlider(SubtitleSliders, "位置", 0, 100, settings.Subtitles.Position, v => v >= 100 ? "底部" : $"{v:0}", v =>
        {
            settings.Subtitles.Position = (int)v;
            Mpv?.ApplySubtitleStyle(settings.Subtitles);
            ScheduleSave();
        });
        _subBackgroundChip = Chips.Create("背景板", () =>
        {
            settings.Subtitles.Background = !settings.Subtitles.Background;
            Mpv?.ApplySubtitleStyle(settings.Subtitles);
            ScheduleSave();
            SyncSubtitleUi();
        });
        _subOverrideChip = Chips.Create("统一样式", () =>
        {
            settings.Subtitles.OverrideAss = !settings.Subtitles.OverrideAss;
            Mpv?.ApplySubtitleStyle(settings.Subtitles);
            ScheduleSave();
            SyncSubtitleUi();
        });
        SubtitleChips.Children.Add(_subBackgroundChip);
        SubtitleChips.Children.Add(_subOverrideChip);

        SyncPictureUi();
        SyncAudioUi();
        SyncSubtitleUi();
    }

    /// <summary>A new file is open: per-title offsets, track lists, chapters and skip marks.</summary>
    private void OnTracksMaybeChanged()
    {
        if (Mpv is not { } mpv) return;
        var prefs = Prefs;
        mpv.SubtitleDelay = prefs.SubtitleDelay;
        mpv.AudioDelay = prefs.AudioDelay;
        UpdateMarks();
        if (_sideOpen) RefreshPanelLists();
        SyncAudioUi();
        SyncSubtitleUi();
    }

    private void OnTuningTabShown() => RefreshPanelLists();

    private void RefreshPanelLists()
    {
        if (Mpv is not { } mpv) return;

        AudioTrackList.Children.Clear();
        var audio = mpv.Tracks("audio");
        foreach (var t in audio) AudioTrackList.Children.Add(Row(t.Label, t.Selected, () => { mpv.SelectTrack("audio", t.Id); RefreshPanelLists(); }));
        if (audio.Count == 0) AudioTrackList.Children.Add(Note("没有音轨"));

        AudioDeviceList.Children.Clear();
        var current = mpv.AudioDevice;
        foreach (var (name, description) in mpv.AudioDevices())
        {
            AudioDeviceList.Children.Add(Row(description, name == current, () =>
            {
                mpv.AudioDevice = name;
                _settings.Current.Audio.Device = name == "auto" ? null : name;
                ScheduleSave();
                RefreshPanelLists();
            }));
        }

        SubtitleTrackList.Children.Clear();
        SecondaryTrackList.Children.Clear();
        var subs = mpv.Tracks("sub");
        var anySelected = subs.Any(s => s.Selected);
        SubtitleTrackList.Children.Add(Row("关闭", !anySelected, () => { mpv.SelectTrack("sub", null); RefreshPanelLists(); }));
        foreach (var t in subs) SubtitleTrackList.Children.Add(Row(t.Label, t.Selected, () => { mpv.SelectTrack("sub", t.Id); RefreshPanelLists(); }));

        var secondary = mpv.SecondarySubtitle;
        SecondarySection.Visibility = subs.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        SecondaryTrackList.Children.Add(Row("关闭", secondary is null, () => { mpv.SelectSecondarySubtitle(null); RefreshPanelLists(); }));
        foreach (var t in subs)
        {
            SecondaryTrackList.Children.Add(Row(t.Label, secondary == t.Id, () => { mpv.SelectSecondarySubtitle(t.Id); RefreshPanelLists(); }));
        }
    }

    // ----- Sync UI --------------------------------------------------------------------------------------

    private void SyncPictureUi()
    {
        if (Mpv is not { } mpv) return;
        var video = _settings.Current.Video;
        foreach (var (chip, value) in _qualityChips) Chips.Set(chip, video.Quality == value);
        QualityNote.Text = video.Quality == QualityPreset.Auto
            ? $"当前：{QualityName(mpv.EffectiveQuality)}"
            : string.Empty;
        if (_interpolationChip is not null) Chips.Set(_interpolationChip, video.Interpolation);
        if (_hdrChip is not null) Chips.Set(_hdrChip, video.HdrPassthrough);
        foreach (var (chip, index) in _aspectChips) Chips.Set(chip, index == _aspectIndex);
        UpdateSkipTexts();
    }

    private void SyncAudioUi()
    {
        var audio = _settings.Current.Audio;
        if (_nightChip is not null) Chips.Set(_nightChip, audio.NightMode);
        if (_passthroughChip is not null) Chips.Set(_passthroughChip, audio.Passthrough);
        if (Mpv is { } mpv) AudioDelayText.Text = DelayLabel(mpv.AudioDelay, "声音");
    }

    private void SyncSubtitleUi()
    {
        var subs = _settings.Current.Subtitles;
        if (_subBackgroundChip is not null) Chips.Set(_subBackgroundChip, subs.Background);
        if (_subOverrideChip is not null) Chips.Set(_subOverrideChip, subs.OverrideAss);
        if (Mpv is { } mpv) SubtitleDelayText.Text = DelayLabel(mpv.SubtitleDelay, "字幕");
    }

    private static string QualityName(QualityPreset preset) => preset switch
    {
        QualityPreset.Performance => "性能",
        QualityPreset.Quality => "画质",
        _ => "均衡",
    };

    private static string DelayLabel(double seconds, string what) => Math.Abs(seconds) < 0.05
        ? "同步"
        : seconds > 0 ? $"{what}延后 {seconds:0.0} 秒" : $"{what}提前 {-seconds:0.0} 秒";

    // ----- Picture ----------------------------------------------------------------------------------------

    private void OnResetPicture(object sender, RoutedEventArgs e)
    {
        foreach (var slider in _adjustSliders) slider.Value = 0;
        _aspectIndex = 0;
        Mpv?.SetAspect("-1");
        Mpv?.SetFill(false);
        SyncPictureUi();
    }

    private void TakeScreenshot(bool withSubtitles)
    {
        if (Mpv is not { } mpv) return;
        var folder = _settings.Current.Video.ScreenshotFolder ?? MpvEngine.DefaultScreenshotFolder;
        Directory.CreateDirectory(folder);
        mpv.Screenshot(withSubtitles);
        ShowToast("截图已保存到「图片\\MoonMovie」", "打开文件夹", () =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true }));
    }

    // ----- Audio / subtitle offsets ---------------------------------------------------------------------

    private void OnAudioEarlier(object sender, RoutedEventArgs e) => NudgeAudio(-0.1);

    private void OnAudioLater(object sender, RoutedEventArgs e) => NudgeAudio(0.1);

    private void NudgeAudio(double delta)
    {
        if (Mpv is not { } mpv) return;
        var value = Math.Round(mpv.AudioDelay + delta, 1);
        mpv.AudioDelay = value;
        _titlePrefs.Update(TitleKey, p => p.AudioDelay = value);
        SyncAudioUi();
        ShowHud("", 0.5 + value / 4, DelayLabel(value, "声音"));
    }

    private void OnSubtitleEarlier(object sender, RoutedEventArgs e) => NudgeSubtitle(-0.1);

    private void OnSubtitleLater(object sender, RoutedEventArgs e) => NudgeSubtitle(0.1);

    private void NudgeSubtitle(double delta)
    {
        if (Mpv is not { } mpv) return;
        var value = Math.Round(mpv.SubtitleDelay + delta, 1);
        mpv.SubtitleDelay = value;
        _titlePrefs.Update(TitleKey, p => p.SubtitleDelay = value);
        SyncSubtitleUi();
        ShowHud("", 0.5 + value / 4, DelayLabel(value, "字幕"));
    }

    private void ToggleSubtitles()
    {
        if (Mpv is not { } mpv) return;
        mpv.SubtitlesVisible = !mpv.SubtitlesVisible;
        ShowHud("", mpv.SubtitlesVisible ? 1 : 0, mpv.SubtitlesVisible ? "字幕已开启" : "字幕已关闭");
    }

    /// <summary>Next track of a kind (wrapping through "off" for subtitles).</summary>
    private void CycleTrack(string kind)
    {
        if (Mpv is not { } mpv) return;
        var tracks = mpv.Tracks(kind);
        if (tracks.Count == 0)
        {
            ShowHud("", 0, kind == "sub" ? "没有字幕" : "没有其他音轨");
            return;
        }

        var current = tracks.ToList().FindIndex(t => t.Selected);
        MediaTrack? next = current + 1 < tracks.Count ? tracks[current + 1] : kind == "sub" ? null : tracks[0];
        mpv.SelectTrack(kind, next?.Id);
        ShowHud(kind == "sub" ? "" : "", 1, next?.Label ?? "字幕已关闭");
    }

    private async void OnLoadSubtitleFile(object sender, RoutedEventArgs e)
    {
        if (Mpv is not { } mpv) return;
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        foreach (var ext in new[] { ".ass", ".ssa", ".srt", ".vtt", ".sup", ".sub" }) picker.FileTypeFilter.Add(ext);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        mpv.AddSubtitle(file.Path);
        ShowToast($"已加载字幕 {file.Name}");
        await Task.Delay(400);
        RefreshPanelLists();
    }

    // ----- Intro / outro --------------------------------------------------------------------------------

    private void OnMarkIntro(object sender, RoutedEventArgs e)
    {
        var at = _engine?.Position.TotalSeconds ?? 0;
        _titlePrefs.Update(TitleKey, p => p.IntroEnd = at);
        UpdateSkipTexts();
        UpdateMarks();
        ShowToast($"片头结束设为 {TimeText.Format(TimeSpan.FromSeconds(at))}，本季后面的集会自动跳过");
    }

    private void OnClearIntro(object sender, RoutedEventArgs e)
    {
        _titlePrefs.Update(TitleKey, p => p.IntroEnd = 0);
        UpdateSkipTexts();
        UpdateMarks();
    }

    private void OnMarkOutro(object sender, RoutedEventArgs e)
    {
        if (_engine is null || _duration <= TimeSpan.Zero) return;
        var length = (_duration - _engine.Position).TotalSeconds;
        _titlePrefs.Update(TitleKey, p => p.OutroLength = length);
        UpdateSkipTexts();
        UpdateMarks();
        ShowToast($"片尾设为最后 {TimeText.Format(TimeSpan.FromSeconds(length))}，到这里会直接播下一集");
    }

    private void OnClearOutro(object sender, RoutedEventArgs e)
    {
        _titlePrefs.Update(TitleKey, p => p.OutroLength = 0);
        UpdateSkipTexts();
        UpdateMarks();
    }

    private void UpdateSkipTexts()
    {
        var series = _request.Item.Kind == MediaKind.Tv;
        SkipSection.Visibility = series ? Visibility.Visible : Visibility.Collapsed;
        if (!series) return;
        var prefs = Prefs;
        IntroText.Text = prefs.IntroEnd > 0 ? $"片头：跳到 {TimeText.Format(TimeSpan.FromSeconds(prefs.IntroEnd))}" : "片头：未设置";
        OutroText.Text = prefs.OutroLength > 0 ? $"片尾：最后 {TimeText.Format(TimeSpan.FromSeconds(prefs.OutroLength))}" : "片尾：未设置";
    }

    /// <summary>Chapters plus the intro end and credits start, as gaps in the seek bar.</summary>
    private void UpdateMarks()
    {
        var marks = new List<TimeSpan>();
        if (Mpv is { } mpv) marks.AddRange(mpv.Chapters().Select(c => TimeSpan.FromSeconds(c.Time)));
        var prefs = Prefs;
        if (prefs.IntroEnd > 0) marks.Add(TimeSpan.FromSeconds(prefs.IntroEnd));
        if (prefs.OutroLength > 0 && _duration > TimeSpan.Zero) marks.Add(_duration - TimeSpan.FromSeconds(prefs.OutroLength));
        SeekBar.SetMarks(marks);
    }

    /// <summary>Called every tick: jumps over a marked opening, and from marked credits into the next episode.</summary>
    private void UpdateSkips(TimeSpan position)
    {
        if (_engine?.State != EngineState.Playing || _request.Item.Kind != MediaKind.Tv) return;
        var prefs = Prefs;

        if (!_introHandled && prefs.SkipIntro && prefs.IntroEnd > 0)
        {
            var seconds = position.TotalSeconds;
            if (seconds < prefs.IntroEnd - 2)
            {
                _introHandled = true;
                SeekTo(TimeSpan.FromSeconds(prefs.IntroEnd));
                ShowToast("已跳过片头", "看片头", () => SeekTo(TimeSpan.Zero), TimeSpan.FromSeconds(6));
            }
            else
            {
                _introHandled = true; // already past it (resumed mid-episode)
            }
        }

        if (!_outroHandled && prefs.SkipOutro && prefs.OutroLength > 0 && HasNext && _duration > TimeSpan.FromMinutes(3)
            && _settings.Current.Playback.AutoNext && (_duration - position).TotalSeconds <= prefs.OutroLength)
        {
            _outroHandled = true;
            ShowToast("已跳过片尾");
            PlayEpisode(_episodeIndex + 1);
        }
    }

    private void ResetSkips()
    {
        _introHandled = false;
        _outroHandled = false;
    }

    // ----- Info panel -----------------------------------------------------------------------------------

    private void ToggleInfoPanel()
    {
        var show = InfoPanel.Visibility != Visibility.Visible;
        InfoPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) UpdateInfoPanel();
    }

    private void UpdateInfoPanel()
    {
        IReadOnlyList<(string Label, string Value)> rows = Mpv is { } mpv
            ? mpv.Stats()
            : [("内核", "系统（Media Foundation）"), ("缓冲", $"{_engine?.BufferedAhead ?? 0:0} 秒")];
        rows = [.. rows, ("片源", $"{_source.SiteName} · {_source.LatencyText}")];

        InfoRows.Children.Clear();
        InfoRows.RowDefinitions.Clear();
        for (var i = 0; i < rows.Count; i++)
        {
            InfoRows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = rows[i].Label, FontSize = 12, Foreground = (Brush)Application.Current.Resources["MoonTextTertiaryBrush"] };
            var value = new TextBlock { Text = rows[i].Value, FontSize = 12, Foreground = (Brush)Application.Current.Resources["MoonTextPrimaryBrush"] };
            Grid.SetRow(label, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            InfoRows.Children.Add(label);
            InfoRows.Children.Add(value);
        }
    }

    // ----- Small builders -------------------------------------------------------------------------------

    private static Button Row(string label, bool selected, Action click)
    {
        var grid = new Grid { Padding = new Thickness(10, 8, 10, 8), ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new FontIcon
        {
            Glyph = "",
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["MoonTextPrimaryBrush"],
            Visibility = selected ? Visibility.Visible : Visibility.Collapsed,
        });
        var text = new TextBlock
        {
            Text = label,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)Application.Current.Resources[selected ? "MoonTextPrimaryBrush" : "MoonTextSecondaryBrush"],
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var button = new Button { Content = grid, Style = (Style)Application.Current.Resources["MoonListRowButtonStyle"] };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
        button.Click += (_, _) => click();
        return button;
    }

    private static TextBlock Note(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Margin = new Thickness(10, 4, 0, 4),
        Foreground = (Brush)Application.Current.Resources["MoonTextTertiaryBrush"],
    };

    /// <summary>"label …… value" header over a slider; returns the slider.</summary>
    private static Slider LabeledSlider(Panel host, string label, double min, double max, double value, Func<double, string> format,
        Action<double> changed)
    {
        var header = new Grid();
        header.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = (Brush)Application.Current.Resources["MoonTextSecondaryBrush"] });
        var valueText = new TextBlock
        {
            Text = format(value),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = (Brush)Application.Current.Resources["MoonTextTertiaryBrush"],
        };
        header.Children.Add(valueText);

        var slider = new Slider { Minimum = min, Maximum = max, Value = value, StepFrequency = 1, IsThumbToolTipEnabled = false };
        slider.ValueChanged += (_, e) =>
        {
            valueText.Text = format(e.NewValue);
            changed(e.NewValue);
        };
        host.Children.Add(header);
        host.Children.Add(slider);
        return slider;
    }
}

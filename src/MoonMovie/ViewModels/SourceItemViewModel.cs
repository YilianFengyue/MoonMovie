using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using MoonMovie.Core.Sources;
using MoonMovie.Services;

namespace MoonMovie.ViewModels;

public sealed partial class SourceItemViewModel(SourceCandidate candidate, SourcePanelViewModel owner)
    : ObservableObject, IComparable<SourceItemViewModel>
{
    public SourceCandidate Candidate { get; private set; } = candidate;

    /// <summary>A fresh copy of the same title on the same site (new episodes since it was cached).</summary>
    public void Update(SourceCandidate fresh)
    {
        Candidate = fresh;
        OnPropertyChanged(nameof(Candidate));
        OnPropertyChanged(nameof(Detail));
    }

    public string SiteName => Candidate.Site.Name;

    public bool IsLocal => LocalPlayback.IsLocal(Candidate);

    /// <summary>"庆余年第二季 · 36 集 · HD国语"</summary>
    public string Detail
    {
        get
        {
            var parts = new List<string>(4) { Candidate.Title };
            if (Candidate.EpisodeCount > 1) parts.Add($"{Candidate.EpisodeCount} 集");
            if (Candidate.Lines.Count > 1) parts.Add($"{Candidate.Lines.Count} 条线路");
            if (Candidate.Remarks is { Length: > 0 and <= 12 } r) parts.Add(r);
            return string.Join(" · ", parts);
        }
    }

    [ObservableProperty]
    public partial ProbeResult? Probe { get; private set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial bool IsBest { get; set; }

    /// <summary>Null while the probe is still running.</summary>
    public ProbeOutcome? State => Probe?.Outcome;

    public string LatencyText => Probe switch
    {
        _ when IsLocal => "本地",
        null => "测速中",
        { Outcome: ProbeOutcome.Failed, Error: { } e } => e,
        { Outcome: ProbeOutcome.Failed } => "不可用",
        { LatencyMs: var ms } => $"{ms} ms",
    };

    /// <summary>Colour carries meaning only: success, caution, critical, or neutral while measuring.</summary>
    public Brush StateBrush => (Brush)Application.Current.Resources[State switch
    {
        ProbeOutcome.Ok => "SystemFillColorSuccessBrush",
        ProbeOutcome.Slow => "SystemFillColorCautionBrush",
        ProbeOutcome.Failed => "SystemFillColorCriticalBrush",
        _ => "TextFillColorTertiaryBrush",
    }];

    public Visibility SelectedVisibility => IsSelected ? Visibility.Visible : Visibility.Collapsed;

    public Visibility BestVisibility => IsBest ? Visibility.Visible : Visibility.Collapsed;

    public double ContentOpacity => State == ProbeOutcome.Failed ? 0.5 : 1;

    public void ApplyProbe(ProbeResult result) => Probe = result;

    /// <summary>Ranking: reachable first, then confident matches, then fastest.</summary>
    public int CompareTo(SourceItemViewModel? other)
    {
        if (other is null) return -1;

        var tier = Tier(this).CompareTo(Tier(other));
        if (tier != 0) return tier;

        var confidence = (other.Candidate.Score >= 120).CompareTo(Candidate.Score >= 120);
        if (confidence != 0) return confidence;

        return (Probe?.LatencyMs ?? int.MaxValue).CompareTo(other.Probe?.LatencyMs ?? int.MaxValue);

        static int Tier(SourceItemViewModel i) => i.State switch
        {
            ProbeOutcome.Ok => 0,
            ProbeOutcome.Slow => 1,
            null => 2,
            ProbeOutcome.Failed => 3,
            _ => 4,
        };
    }

    [RelayCommand]
    private void Choose() => owner.Choose(this);

    partial void OnProbeChanged(ProbeResult? value)
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(LatencyText));
        OnPropertyChanged(nameof(StateBrush));
        OnPropertyChanged(nameof(ContentOpacity));
    }

    partial void OnIsSelectedChanged(bool value) => OnPropertyChanged(nameof(SelectedVisibility));

    partial void OnIsBestChanged(bool value) => OnPropertyChanged(nameof(BestVisibility));
}

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using MoonMovie.Core.Sources;

namespace MoonMovie.ViewModels;

public enum SourcePhase
{
    Idle,
    Searching,
    Ready,
    Empty,
}

/// <summary>
/// Finds, probes and ranks playable sources in the background so that "播放" just works.
/// The best candidate is chosen automatically until the user picks one explicitly.
/// </summary>
public sealed partial class SourcePanelViewModel : ObservableObject
{
    private const int ProbeConcurrency = 4;

    // Results survive navigating away and back within a session.
    private static readonly Dictionary<string, List<(SourceCandidate Candidate, ProbeResult? Probe)>> SessionCache = [];

    private readonly SourceSearchService _search;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private CancellationTokenSource? _cts;
    private bool _userPicked;
    private int _pendingProbes;
    private bool _searchComplete;
    private SourceTarget? _target;

    public SourcePanelViewModel(SourceSearchService search)
    {
        _search = search;
        SitesTotal = search.Sites.Count;
    }

    public ObservableCollection<SourceItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial SourcePhase Phase { get; private set; }

    [ObservableProperty]
    public partial int SitesDone { get; private set; }

    [ObservableProperty]
    public partial int SitesTotal { get; private set; }

    [ObservableProperty]
    public partial SourceItemViewModel? Selected { get; private set; }

    public int PlayableCount => Items.Count(i => i.State is ProbeOutcome.Ok or ProbeOutcome.Slow);

    public double Progress => SitesTotal == 0 ? 0 : (double)SitesDone / SitesTotal;

    public void Start(SourceTarget target)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _target = target;
        Items.Clear();
        Selected = null;
        _userPicked = false;
        _pendingProbes = 0;
        _searchComplete = false;
        SitesDone = 0;
        Phase = SourcePhase.Searching;
        OnPropertyChanged(nameof(PlayableCount));
        OnPropertyChanged(nameof(Progress));

        if (SessionCache.TryGetValue(target.CacheKey, out var cached) && cached.Count > 0)
        {
            var gate = new SemaphoreSlim(ProbeConcurrency);
            foreach (var (candidate, probe) in cached)
            {
                var item = Insert(candidate);
                if (probe is not null) item.ApplyProbe(probe);
                else _ = ProbeAsync(item, gate, ct);
            }

            SitesDone = SitesTotal;
            _searchComplete = true;
            OnPropertyChanged(nameof(Progress));
            Reevaluate();
            return;
        }

        _ = RunAsync(target, ct);
    }

    /// <summary>User choice wins over automatic ranking from now on.</summary>
    public void Choose(SourceItemViewModel item)
    {
        _userPicked = true;
        SetSelected(item);
        Chosen?.Invoke(this, item);
    }

    /// <summary>Raised when the user explicitly picks a source (not for automatic selection).</summary>
    public event EventHandler<SourceItemViewModel>? Chosen;

    public void Retry(SourceTarget target)
    {
        SessionCache.Remove(target.CacheKey);
        Start(target);
    }

    private async Task RunAsync(SourceTarget target, CancellationToken ct)
    {
        var probeGate = new SemaphoreSlim(ProbeConcurrency);
        var siteTasks = _search.Sites.Select(async site =>
        {
            try
            {
                var found = await _search.SearchSiteAsync(site, target, ct).ConfigureAwait(false);
                if (found.Count > 0)
                {
                    // One entry per site keeps the list scannable; the site's best match represents it.
                    var candidate = found[0];
                    Post(() =>
                    {
                        if (ct.IsCancellationRequested) return;
                        var item = Insert(candidate);
                        _ = ProbeAsync(item, probeGate, ct);
                    });
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                // Dead or blocked site; nothing to show.
            }
            finally
            {
                Post(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    SitesDone++;
                    OnPropertyChanged(nameof(Progress));
                });
            }
        }).ToArray();

        await Task.WhenAll(siteTasks).ConfigureAwait(false);
        Post(() =>
        {
            if (ct.IsCancellationRequested) return;
            _searchComplete = true;
            Reevaluate();
        });
    }

    private async Task ProbeAsync(SourceItemViewModel item, SemaphoreSlim gate, CancellationToken ct)
    {
        _pendingProbes++;
        try
        {
            await gate.WaitAsync(ct);
            ProbeResult result;
            try
            {
                result = await _search.ProbeAsync(item.Candidate.PrimaryLine.Episodes[0].Url, ct);
            }
            finally
            {
                gate.Release();
            }

            if (ct.IsCancellationRequested) return;
            item.ApplyProbe(result);
            Reposition(item);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            _pendingProbes--;
        }

        Reevaluate();
    }

    private SourceItemViewModel Insert(SourceCandidate candidate)
    {
        var item = new SourceItemViewModel(candidate, this);
        var index = 0;
        while (index < Items.Count && Items[index].CompareTo(item) <= 0) index++;
        Items.Insert(index, item);
        OnPropertyChanged(nameof(PlayableCount));
        return item;
    }

    private void Reposition(SourceItemViewModel item)
    {
        var from = Items.IndexOf(item);
        if (from < 0) return;

        // The rest of the list is sorted; count how many of the others rank ahead of this item.
        var to = 0;
        foreach (var other in Items)
        {
            if (other == item) continue;
            if (other.CompareTo(item) <= 0) to++;
            else break;
        }

        if (to != from) Items.Move(from, to);
        OnPropertyChanged(nameof(PlayableCount));
    }

    private void Reevaluate()
    {
        var best = Items.FirstOrDefault(i => i.State is ProbeOutcome.Ok or ProbeOutcome.Slow);
        foreach (var item in Items) item.IsBest = item == best;

        if (!_userPicked || Selected is null || Selected.State == ProbeOutcome.Failed)
        {
            if (best is not null && best != Selected) SetSelected(best);
        }

        if (Selected is not null)
        {
            Phase = SourcePhase.Ready;
        }
        else if (_searchComplete && _pendingProbes == 0)
        {
            Phase = SourcePhase.Empty;
        }

        if (_searchComplete && _pendingProbes == 0 && _target is not null && Items.Count > 0)
        {
            SessionCache[_target.CacheKey] = Items.Select(i => (i.Candidate, i.Probe)).ToList();
        }
    }

    private void SetSelected(SourceItemViewModel item)
    {
        if (Selected is not null) Selected.IsSelected = false;
        Selected = item;
        item.IsSelected = true;
        Phase = SourcePhase.Ready;
    }

    private void Post(Action action)
    {
        if (_dispatcher.HasThreadAccess) action();
        else _dispatcher.TryEnqueue(() => action());
    }

}

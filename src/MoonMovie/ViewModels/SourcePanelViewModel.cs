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
    private readonly SourceMatchCache? _disk;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private CancellationTokenSource? _cts;
    private bool _userPicked;
    private int _pendingProbes;
    private bool _searchComplete;
    private SourceTarget? _target;

    public SourcePanelViewModel(SourceSearchService search, SourceMatchCache? disk = null)
    {
        _search = search;
        _disk = disk;
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

    /// <summary>Files on disk for the target, offered first (and chosen) ahead of the online search.</summary>
    public Func<SourceTarget, SourceCandidate?>? LocalProvider { get; set; }

    /// <summary>
    /// Licensed copies (B站正版), looked up alongside the resource sites. Each comes with whether it should lead:
    /// only when it plays every episode in full at a good quality for this account; otherwise it ranks behind the
    /// reachable resource sites and serves as a fallback.
    /// </summary>
    public Func<SourceTarget, CancellationToken, Task<IReadOnlyList<(SourceCandidate Candidate, bool Preferred)>>>? OfficialProvider { get; set; }

    /// <summary>Only the given local source: no online search (files opened directly, unidentified titles).</summary>
    public SourceItemViewModel UseLocal(SourceCandidate candidate)
    {
        _cts?.Cancel();
        Items.Clear();
        var item = Insert(candidate);
        item.ApplyProbe(new ProbeResult(ProbeOutcome.Ok, 0));
        SitesDone = SitesTotal;
        _searchComplete = true;
        SetSelected(item);
        OnPropertyChanged(nameof(Progress));
        return item;
    }

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

        if (LocalProvider?.Invoke(target) is { } local)
        {
            Insert(local).ApplyProbe(new ProbeResult(ProbeOutcome.Ok, 0));
            Reevaluate();
        }

        if (OfficialProvider is not null) _ = RunOfficialAsync(target, ct);

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

        // Known from an earlier visit: show it now (playable at once), then refresh latency and episode lists.
        if (_disk?.Load(target.CacheKey) is { } disk)
        {
            var gate = new SemaphoreSlim(ProbeConcurrency);
            foreach (var (candidate, probe) in disk.Items)
            {
                var item = Insert(candidate);
                if (probe is not null) item.ApplyProbe(probe);
                _ = ProbeAsync(item, gate, ct);
            }

            Reevaluate();
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
                        if (Items.FirstOrDefault(i => i.Candidate.Identity == candidate.Identity) is { } known)
                        {
                            known.Update(candidate); // already shown from the disk cache (and re-probed there)
                            return;
                        }

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

    private async Task RunOfficialAsync(SourceTarget target, CancellationToken ct)
    {
        _pendingProbes++;
        try
        {
            var found = await OfficialProvider!(target, ct);
            if (ct.IsCancellationRequested) return;
            foreach (var (candidate, preferred) in found)
            {
                if (Items.Any(i => i.Candidate.Identity == candidate.Identity)) continue;
                var item = new SourceItemViewModel(candidate, this) { Preferred = preferred };
                item.ApplyProbe(new ProbeResult(ProbeOutcome.Ok, 0)); // nothing to measure: B站's own CDN
                InsertSorted(item);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException
                                       or Core.Bilibili.BiliException or System.Text.Json.JsonException)
        {
            // B站 unreachable or the title is not there: the resource sites carry on alone.
        }
        finally
        {
            _pendingProbes--;
        }

        if (!ct.IsCancellationRequested) Reevaluate();
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

    private SourceItemViewModel Insert(SourceCandidate candidate) => InsertSorted(new SourceItemViewModel(candidate, this));

    private SourceItemViewModel InsertSorted(SourceItemViewModel item)
    {
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
            // Files and B站正版 are looked up fresh each time (rights and sign-in change); resource sites are kept.
            var keep = Items.Where(i => !i.IsLocal && !i.IsOfficial).ToArray();
            SessionCache[_target.CacheKey] = keep.Select(i => (i.Candidate, i.Probe)).ToList();
            _disk?.Save(_target.CacheKey, keep.Select(i => new CachedSource(i.Candidate, i.Probe)).ToArray());
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

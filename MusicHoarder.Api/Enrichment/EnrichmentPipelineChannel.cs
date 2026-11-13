using System.Threading.Channels;
using MusicHoarder.Api.Jobs;

namespace MusicHoarder.Api.Enrichment;

/// <summary>
/// Singleton channel that receives SongIds ready for enrichment, and the coordinator for the
/// enrichment "cycle". Every enrichment producer (manual /enrich and /enrich/folder, startup and
/// retry sweeps, fingerprint completion) enqueues here, and the always-running enrichment workers
/// are the only consumer — so this is the one choke point that knows when enrichment work is in
/// flight. It uses that to flip the <see cref="JobType.Enrich"/> step Running↔Idle in
/// <see cref="JobManager"/> and to drive <see cref="EnrichmentProgressTracker"/>, which together
/// make enrichment visible in the status snapshot, the SSE stream, and Runs history.
/// <para>
/// A song waits in the queue at most once. The startup sweeps overlap (a song can be both pending
/// and missing a provider) and the retry sweep re-selects the same rate-limited songs every few
/// minutes, so without this a backlog grew by thousands of repeats (issue #550). Only <em>waiting</em>
/// songs are deduplicated: one already handed to a worker can be queued again, because whatever
/// queued it (a reset, a fresh download) may have changed the row after the worker loaded it.
/// </para>
/// </summary>
public class EnrichmentPipelineChannel(JobManager jobManager, EnrichmentProgressTracker progressTracker)
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

    // Fast lane for explicitly-acquired tracks (wishlist/import downloads): drained before the
    // normal queue so a fresh import isn't stuck behind a thousands-deep backfill sweep.
    private readonly Channel<int> _priorityChannel = Channel.CreateUnbounded<int>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

    private readonly object _lock = new();

    // Songs waiting in a lane → true when that lane is the priority one. A song promoted from the
    // normal lane leaves a stale entry there, which the reader drops (Claim) without counting it.
    private readonly Dictionary<int, bool> _waiting = [];
    private int _inFlight;
    private Guid _runId;
    private string? _label;

    /// <summary>
    /// The normal lane, for inspection in tests. Workers read through
    /// <see cref="ReadAllPrioritizedAsync"/>, which also keeps the waiting set; there is no public
    /// writer, so every producer goes through the deduplicating Enqueue methods.
    /// </summary>
    public ChannelReader<int> Reader => _channel.Reader;

    /// <summary>Songs currently in flight in the active enrichment cycle (0 when idle). Surfaced as a metric gauge.</summary>
    public int InFlight
    {
        get { lock (_lock) return _inFlight; }
    }

    /// <summary>Label of the active enrichment cycle (e.g. "Manual enrich — Kanye West"), or null.</summary>
    public string? CurrentLabel
    {
        get { lock (_lock) return _label; }
    }

    /// <summary>Songs waiting for a worker (both lanes), excluding the one each worker holds.</summary>
    public int Waiting
    {
        get { lock (_lock) return _waiting.Count; }
    }

    /// <summary>Queues a song unless it is already waiting. True when it was queued.</summary>
    public bool Enqueue(int songId, string? label = null) => EnqueueRange([songId], label) > 0;

    /// <summary>
    /// Queues the songs that are not already waiting and returns how many that was — the number to
    /// log, since the rest were already in the queue.
    /// </summary>
    public int EnqueueRange(IEnumerable<int> songIds, string? label = null) => Add(songIds, priority: false, label);

    /// <summary>
    /// Fast lane: these songs are dequeued before anything in the normal queue. A song already
    /// waiting in the normal lane moves up (it is not counted twice). Returns how many were queued
    /// or moved up.
    /// </summary>
    public int EnqueueRangePriority(IEnumerable<int> songIds, string? label = null) => Add(songIds, priority: true, label);

    private int Add(IEnumerable<int> songIds, bool priority, string? label)
    {
        var lane = priority ? _priorityChannel : _channel;
        var accepted = 0;
        lock (_lock)
        {
            var added = 0;
            foreach (var id in songIds)
            {
                if (_waiting.TryGetValue(id, out var inPriority))
                {
                    if (inPriority || !priority)
                        continue;
                    _waiting[id] = true; // promotion: same item, new lane — not a new one
                }
                else
                {
                    _waiting[id] = priority;
                    added++;
                }

                accepted++;
                lane.Writer.TryWrite(id);
            }

            if (added > 0)
                BeginItemsLocked(added, label);
        }

        return accepted;
    }

    /// <summary>
    /// Takes a read entry off the waiting set. False for a stale normal-lane entry whose song was
    /// promoted to (and already read from) the priority lane, or was cleared by a reset.
    /// </summary>
    private bool Claim(int songId, bool fromPriority)
    {
        lock (_lock)
        {
            if (!_waiting.TryGetValue(songId, out var inPriority) || inPriority != fromPriority)
                return false;
            _waiting.Remove(songId);
            return true;
        }
    }

    /// <summary>
    /// The worker's consume stream: priority items strictly before normal ones. Safe for multiple
    /// concurrent consumers (each TryRead claims an item atomically); never completes — the
    /// channels stay open for the app's lifetime, so termination is via <paramref name="ct"/>.
    /// </summary>
    public async IAsyncEnumerable<int> ReadAllPrioritizedAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (_priorityChannel.Reader.TryRead(out var priorityId))
            {
                if (Claim(priorityId, fromPriority: true))
                    yield return priorityId;
                continue;
            }
            if (_channel.Reader.TryRead(out var songId))
            {
                if (Claim(songId, fromPriority: false))
                    yield return songId;
                continue;
            }

            // Nothing queued: wait until either lane has data, then loop back to the TryReads
            // (another consumer may win the race — that's fine).
            var waitPriority = _priorityChannel.Reader.WaitToReadAsync(ct).AsTask();
            var waitNormal = _channel.Reader.WaitToReadAsync(ct).AsTask();
            var completed = await Task.WhenAny(waitPriority, waitNormal);
            await completed; // propagate cancellation
        }
    }

    private void BeginItemsLocked(int count, string? label)
    {
        if (_inFlight == 0)
        {
            // Fresh cycle: register the enrich step as Running and start a progress cycle.
            _runId = Guid.NewGuid();
            jobManager.TryRegisterAutoJob(JobType.Enrich, _runId, out _);
            progressTracker.StartCycle(_runId, count);
            _label = label;
        }
        else
        {
            // Cycle already active: grow the denominator and adopt a label if we don't have one.
            progressTracker.AddToTotal(count);
            _label ??= label;
        }

        _inFlight += count;
    }

    /// <summary>Called by a worker once per dequeued item, regardless of outcome.</summary>
    public void MarkProcessed()
    {
        bool completed;
        lock (_lock)
        {
            if (_inFlight == 0)
                return;

            _inFlight--;
            completed = _inFlight == 0 && CompleteLocked(cancelled: false);
        }

        if (completed)
            TriggerBuild();
    }

    /// <summary>
    /// Drain any queued items and end the cycle. Used by the cancel path so a cancelled enrich
    /// doesn't leave the step stuck Running.
    /// </summary>
    public void ResetCycle(bool cancelled)
    {
        bool completed;
        lock (_lock)
        {
            while (_channel.Reader.TryRead(out _)) { }
            while (_priorityChannel.Reader.TryRead(out _)) { }
            _waiting.Clear();
            _inFlight = 0;
            completed = CompleteLocked(cancelled);
        }

        if (completed && !cancelled)
            TriggerBuild();
    }

    /// <summary>Returns true when an active cycle was just completed (so the caller can chain a build).</summary>
    private bool CompleteLocked(bool cancelled)
    {
        if (_runId == Guid.Empty)
            return false;

        jobManager.SignalComplete(JobType.Enrich, _runId, cancelled);
        progressTracker.CompleteCycle(_runId);
        _runId = Guid.Empty;
        _label = null;
        return true;
    }

    /// <summary>
    /// Chain a library build when an enrichment cycle finishes. This is what makes a manual enrich
    /// (e.g. "enrich this folder") land in the library even when AutoStartPipeline is off and the
    /// builder's auto-poll never runs — it reuses the same Build trigger as the manual /build button.
    /// Harmless in auto mode: TryStartJob no-ops if a build is already running. Fired outside the
    /// channel lock to avoid nesting it inside JobManager's lock.
    /// </summary>
    private void TriggerBuild() => jobManager.TryStartJob(JobType.Build, out _, out _);
}

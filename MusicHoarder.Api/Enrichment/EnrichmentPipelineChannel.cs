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
/// </summary>
public class EnrichmentPipelineChannel(JobManager jobManager, EnrichmentProgressTracker progressTracker)
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

    private readonly object _lock = new();
    private int _inFlight;
    private Guid _runId;
    private string? _label;

    public ChannelWriter<int> Writer => _channel.Writer;
    public ChannelReader<int> Reader => _channel.Reader;

    /// <summary>Label of the active enrichment cycle (e.g. "Manual enrich — Kanye West"), or null.</summary>
    public string? CurrentLabel
    {
        get { lock (_lock) return _label; }
    }

    public void Enqueue(int songId, string? label = null)
    {
        BeginItems(1, label);
        _channel.Writer.TryWrite(songId);
    }

    public void EnqueueRange(IEnumerable<int> songIds, string? label = null)
    {
        var ids = songIds as ICollection<int> ?? songIds.ToList();
        if (ids.Count == 0)
            return;

        BeginItems(ids.Count, label);
        foreach (var id in ids)
            _channel.Writer.TryWrite(id);
    }

    private void BeginItems(int count, string? label)
    {
        lock (_lock)
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
    }

    /// <summary>Called by a worker once per dequeued item, regardless of outcome.</summary>
    public void MarkProcessed()
    {
        lock (_lock)
        {
            if (_inFlight == 0)
                return;

            _inFlight--;
            if (_inFlight == 0)
                CompleteLocked(cancelled: false);
        }
    }

    /// <summary>
    /// Drain any queued items and end the cycle. Used by the cancel path so a cancelled enrich
    /// doesn't leave the step stuck Running.
    /// </summary>
    public void ResetCycle(bool cancelled)
    {
        lock (_lock)
        {
            while (_channel.Reader.TryRead(out _)) { }
            _inFlight = 0;
            CompleteLocked(cancelled);
        }
    }

    private void CompleteLocked(bool cancelled)
    {
        if (_runId == Guid.Empty)
            return;

        jobManager.SignalComplete(JobType.Enrich, _runId, cancelled);
        progressTracker.CompleteCycle(_runId);
        _runId = Guid.Empty;
        _label = null;
    }
}

using System.Threading.Channels;

namespace MusicHoarder.Api.Quality;

/// <summary>
/// Singleton work queue of song ids to grade, feeding <see cref="QualityGradingBackgroundService"/>.
/// Deliberately independent of <c>JobManager</c>: grading is cheap API work that can run alongside
/// scanning/enrichment/build without taking the one-job lock. It only drives the grading progress
/// tracker so a batch is visible in the UI.
/// </summary>
public class QualityGradingChannel(QualityGradingProgressTracker progressTracker)
{
    private readonly Channel<GradeWorkItem> _channel = Channel.CreateUnbounded<GradeWorkItem>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

    private readonly object _lock = new();

    /// <summary>Song ids currently queued or in flight, so the auto-sweep doesn't re-enqueue work it
    /// already handed off every <c>IdleDelaySeconds</c> (which floods the channel and the API).</summary>
    private readonly HashSet<int> _queued = [];
    private int _inFlight;
    private Guid _runId;

    public ChannelReader<GradeWorkItem> Reader => _channel.Reader;

    public void Enqueue(int songId, bool force) => EnqueueRange([songId], force);

    public void EnqueueRange(IEnumerable<int> songIds, bool force)
    {
        var ids = songIds as ICollection<int> ?? songIds.ToList();
        if (ids.Count == 0) return;

        List<int> toQueue;
        lock (_lock)
        {
            // Drop ids already queued/in-flight — but a forced (manual "grade now") request always
            // runs, even if a background sweep already queued the song.
            toQueue = new List<int>(ids.Count);
            foreach (var id in ids)
                if (_queued.Add(id) || force)
                    toQueue.Add(id);

            if (toQueue.Count == 0) return;

            if (_inFlight == 0)
            {
                _runId = Guid.NewGuid();
                progressTracker.StartCycle(_runId, toQueue.Count);
            }
            else
            {
                progressTracker.AddToTotal(toQueue.Count);
            }
            _inFlight += toQueue.Count;
        }

        foreach (var id in toQueue)
            _channel.Writer.TryWrite(new GradeWorkItem(id, force));
    }

    /// <summary>
    /// Called once per dequeued item, regardless of outcome. Returns <c>true</c> exactly on the call
    /// that drains the last in-flight item (the run→idle edge), so the caller can react to a grading
    /// run completing (e.g. capture a timeline snapshot).
    /// </summary>
    public bool MarkProcessed(int songId)
    {
        lock (_lock)
        {
            _queued.Remove(songId);
            if (_inFlight == 0) return false;
            _inFlight--;
            if (_inFlight == 0)
            {
                progressTracker.CompleteCycle(_runId);
                _runId = Guid.Empty;
                return true;
            }
            return false;
        }
    }
}

public record GradeWorkItem(int SongId, bool Force);

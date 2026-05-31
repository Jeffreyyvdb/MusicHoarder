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
    private int _inFlight;
    private Guid _runId;

    public ChannelReader<GradeWorkItem> Reader => _channel.Reader;

    public void Enqueue(int songId, bool force) => EnqueueRange([songId], force);

    public void EnqueueRange(IEnumerable<int> songIds, bool force)
    {
        var ids = songIds as ICollection<int> ?? songIds.ToList();
        if (ids.Count == 0) return;

        lock (_lock)
        {
            if (_inFlight == 0)
            {
                _runId = Guid.NewGuid();
                progressTracker.StartCycle(_runId, ids.Count);
            }
            else
            {
                progressTracker.AddToTotal(ids.Count);
            }
            _inFlight += ids.Count;
        }

        foreach (var id in ids)
            _channel.Writer.TryWrite(new GradeWorkItem(id, force));
    }

    /// <summary>
    /// Called once per dequeued item, regardless of outcome. Returns <c>true</c> exactly on the call
    /// that drains the last in-flight item (the run→idle edge), so the caller can react to a grading
    /// run completing (e.g. capture a timeline snapshot).
    /// </summary>
    public bool MarkProcessed()
    {
        lock (_lock)
        {
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

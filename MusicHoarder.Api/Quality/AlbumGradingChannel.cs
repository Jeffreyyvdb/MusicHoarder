using System.Threading.Channels;

namespace MusicHoarder.Api.Quality;

/// <summary>
/// Singleton work queue of canonical-album ids to grade, feeding <see cref="AlbumGradingBackgroundService"/>.
/// Mirrors <see cref="QualityGradingChannel"/> (independent of <c>JobManager</c>; drives only the album
/// grading progress tracker). Manual grading enqueues here too, so both paths share the same workers.
/// </summary>
public class AlbumGradingChannel(AlbumGradingProgressTracker progressTracker)
{
    private readonly Channel<GradeAlbumWorkItem> _channel = Channel.CreateUnbounded<GradeAlbumWorkItem>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

    private readonly object _lock = new();
    private int _inFlight;
    private Guid _runId;

    public ChannelReader<GradeAlbumWorkItem> Reader => _channel.Reader;

    public void Enqueue(int canonicalAlbumId, bool force) => EnqueueRange([canonicalAlbumId], force);

    public void EnqueueRange(IEnumerable<int> albumIds, bool force)
    {
        var ids = albumIds as ICollection<int> ?? albumIds.ToList();
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
            _channel.Writer.TryWrite(new GradeAlbumWorkItem(id, force));
    }

    /// <summary>Called once per dequeued item; returns true on the call that drains the last in-flight item.</summary>
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

public record GradeAlbumWorkItem(int CanonicalAlbumId, bool Force);

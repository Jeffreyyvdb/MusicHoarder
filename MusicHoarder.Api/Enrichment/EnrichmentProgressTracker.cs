namespace MusicHoarder.Api.Enrichment;

/// <summary>
/// Thread-safe holder for the latest enrichment run state.
/// </summary>
public class EnrichmentProgressTracker
{
    private Guid _runId;
    private int _totalTracks;
    private int _processed;
    private int _enriched;
    private int _failed;
    private int _needsReview;
    private bool _isComplete;
    private DateTime _startedAt;
    private DateTime? _completedAt;

    public EnrichmentState? GetCurrent()
    {
        var id = _runId;
        if (id == Guid.Empty) return null;

        return new EnrichmentState(
            id,
            _totalTracks,
            _processed,
            _enriched,
            _failed,
            _needsReview,
            _isComplete,
            _startedAt,
            _completedAt);
    }

    public void StartCycle(Guid runId, int totalTracks)
    {
        _runId = runId;
        _totalTracks = totalTracks;
        _processed = 0;
        _enriched = 0;
        _failed = 0;
        _needsReview = 0;
        _isComplete = false;
        _startedAt = DateTime.UtcNow;
        _completedAt = null;
    }

    public void IncrementEnriched()
    {
        Interlocked.Increment(ref _processed);
        Interlocked.Increment(ref _enriched);
    }

    public void IncrementNeedsReview()
    {
        Interlocked.Increment(ref _processed);
        Interlocked.Increment(ref _needsReview);
    }

    public void IncrementFailed()
    {
        Interlocked.Increment(ref _processed);
        Interlocked.Increment(ref _failed);
    }

    public void CompleteCycle(Guid runId)
    {
        if (_runId != runId) return;
        _isComplete = true;
        _completedAt = DateTime.UtcNow;
    }
}

public record EnrichmentState(
    Guid RunId,
    int TotalTracks,
    int Processed,
    int Enriched,
    int Failed,
    int NeedsReview,
    bool IsComplete,
    DateTime StartedAt,
    DateTime? CompletedAt);

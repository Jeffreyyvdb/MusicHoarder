namespace MusicHoarder.Api.Quality;

/// <summary>Thread-safe snapshot of the current AI grading run (manual batch or auto sweep).</summary>
public class QualityGradingProgressTracker
{
    private Guid _runId;
    private int _total;
    private int _processed;
    private int _graded;
    private int _skipped;
    private int _failed;
    private bool _isComplete;
    private DateTime _startedAt;
    private DateTime? _completedAt;

    public QualityGradingState? GetCurrent()
    {
        var id = _runId;
        if (id == Guid.Empty) return null;
        return new QualityGradingState(
            id, _total, _processed, _graded, _skipped, _failed, _isComplete, _startedAt, _completedAt);
    }

    public void StartCycle(Guid runId, int total)
    {
        _runId = runId;
        _total = total;
        _processed = 0;
        _graded = 0;
        _skipped = 0;
        _failed = 0;
        _isComplete = false;
        _startedAt = DateTime.UtcNow;
        _completedAt = null;
    }

    public void AddToTotal(int count) => Interlocked.Add(ref _total, count);

    public void IncrementGraded()
    {
        Interlocked.Increment(ref _processed);
        Interlocked.Increment(ref _graded);
    }

    public void IncrementSkipped()
    {
        Interlocked.Increment(ref _processed);
        Interlocked.Increment(ref _skipped);
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

public record QualityGradingState(
    Guid RunId,
    int Total,
    int Processed,
    int Graded,
    int Skipped,
    int Failed,
    bool IsComplete,
    DateTime StartedAt,
    DateTime? CompletedAt);

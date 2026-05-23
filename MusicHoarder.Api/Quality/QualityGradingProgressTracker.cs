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

    // Last grading error, kept independent of the per-cycle counters so it survives across runs
    // (the auto-sweep starts a new cycle each pass). Cleared on the next successful grade.
    private QualityGradingError? _lastError;

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
        // A fresh success means grading works again — clear any lingering error.
        Interlocked.Exchange(ref _lastError, null);
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

    /// <summary>Records the most recent grading failure so the UI can surface why grading stalled.</summary>
    public void RecordError(string code, string? message) =>
        Interlocked.Exchange(ref _lastError, new QualityGradingError(code, message, DateTime.UtcNow));

    public QualityGradingError? GetLastError() => _lastError;
}

public record QualityGradingError(string Code, string? Message, DateTime AtUtc);

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

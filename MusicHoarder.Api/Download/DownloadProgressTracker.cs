namespace MusicHoarder.Api.Download;

/// <summary>
/// Thread-safe singleton that tracks the progress of the most recent wishlist download run.
/// </summary>
public class DownloadProgressTracker
{
    private Guid _runId;
    private int _totalItems;
    private int _processed;
    private int _downloaded;
    private int _skipped;
    private int _failed;
    private int _notFound;
    private bool _isComplete;
    private DateTime _startedAt;
    private DateTime? _completedAt;

    public DownloadState? GetCurrent()
    {
        var id = _runId;
        if (id == Guid.Empty) return null;

        return new DownloadState(
            id,
            _totalItems,
            _processed,
            _downloaded,
            _skipped,
            _failed,
            _notFound,
            _isComplete,
            _startedAt,
            _completedAt);
    }

    public void StartRun(Guid runId, int totalItems)
    {
        _runId = runId;
        _totalItems = totalItems;
        _processed = 0;
        _downloaded = 0;
        _skipped = 0;
        _failed = 0;
        _notFound = 0;
        _isComplete = false;
        _startedAt = DateTime.UtcNow;
        _completedAt = null;
    }

    public void IncrementDownloaded()
    {
        Interlocked.Increment(ref _processed);
        Interlocked.Increment(ref _downloaded);
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

    public void IncrementNotFound()
    {
        Interlocked.Increment(ref _processed);
        Interlocked.Increment(ref _notFound);
    }

    public void CompleteRun(Guid runId)
    {
        if (_runId != runId) return;
        _isComplete = true;
        _completedAt = DateTime.UtcNow;
    }
}

public record DownloadState(
    Guid RunId,
    int TotalItems,
    int Processed,
    int Downloaded,
    int Skipped,
    int Failed,
    int NotFound,
    bool IsComplete,
    DateTime StartedAt,
    DateTime? CompletedAt);

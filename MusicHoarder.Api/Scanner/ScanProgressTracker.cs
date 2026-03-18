namespace MusicHoarder.Api.Scanner;

/// <summary>
/// Thread-safe singleton that holds the most recent scan state so it can be
/// surfaced by a status endpoint without touching the database.
/// </summary>
public class ScanProgressTracker
{
    // Separate int fields so individual increments can use Interlocked without
    // constructing a new record on every update.
    private Guid _scanId;
    private int _totalFiles;
    private int _processed;
    private int _newFiles;
    private int _changedFiles;
    private int _skippedFiles;
    private int _failedFiles;
    private bool _isComplete;
    private DateTime _startedAt;
    private DateTime? _completedAt;

    public ScanState? GetCurrent()
    {
        var id = _scanId;
        if (id == Guid.Empty) return null;

        return new ScanState(
            id,
            _totalFiles,
            _processed,
            _newFiles,
            _changedFiles,
            _skippedFiles,
            _failedFiles,
            _isComplete,
            _startedAt,
            _completedAt);
    }

    public void Start(Guid scanId, int totalFiles)
    {
        _scanId = scanId;
        _totalFiles = totalFiles;
        _processed = 0;
        _newFiles = 0;
        _changedFiles = 0;
        _skippedFiles = 0;
        _failedFiles = 0;
        _isComplete = false;
        _startedAt = DateTime.UtcNow;
        _completedAt = null;
    }

    public void IncrementNew()
    {
        Interlocked.Increment(ref _processed);
        Interlocked.Increment(ref _newFiles);
    }

    public void IncrementChanged()
    {
        Interlocked.Increment(ref _processed);
        Interlocked.Increment(ref _changedFiles);
    }

    public void IncrementSkipped() => Interlocked.Increment(ref _skippedFiles);

    public void AddSkipped(int count)
    {
        Interlocked.Add(ref _skippedFiles, count);
    }

    public void IncrementFailed()
    {
        Interlocked.Increment(ref _processed);
        Interlocked.Increment(ref _failedFiles);
    }

    public void Complete(Guid scanId)
    {
        if (_scanId != scanId) return;
        _isComplete = true;
        _completedAt = DateTime.UtcNow;
    }
}

public record ScanState(
    Guid ScanId,
    int TotalFiles,
    int Processed,
    int NewFiles,
    int ChangedFiles,
    int SkippedFiles,
    int FailedFiles,
    bool IsComplete,
    DateTime StartedAt,
    DateTime? CompletedAt);

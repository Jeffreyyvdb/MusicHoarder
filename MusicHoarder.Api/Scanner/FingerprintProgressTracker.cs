namespace MusicHoarder.Api.Scanner;

/// <summary>
/// Thread-safe singleton that tracks the progress of the most recent fingerprint run.
/// </summary>
public class FingerprintProgressTracker
{
    private Guid _runId;
    private int _totalTracks;
    private int _processed;
    private int _fingerprinted;
    private int _failed;
    private bool _isComplete;
    private DateTime _startedAt;
    private DateTime? _completedAt;

    public FingerprintState? GetCurrent()
    {
        var id = _runId;
        if (id == Guid.Empty) return null;

        return new FingerprintState(
            id,
            _totalTracks,
            _processed,
            _fingerprinted,
            _failed,
            _isComplete,
            _startedAt,
            _completedAt);
    }

    public void StartRun(Guid runId, int totalTracks)
    {
        _runId = runId;
        _totalTracks = totalTracks;
        _processed = 0;
        _fingerprinted = 0;
        _failed = 0;
        _isComplete = false;
        _startedAt = DateTime.UtcNow;
        _completedAt = null;
    }

    public void IncrementFingerprinted()
    {
        Interlocked.Increment(ref _processed);
        Interlocked.Increment(ref _fingerprinted);
    }

    public void IncrementFailed()
    {
        Interlocked.Increment(ref _processed);
        Interlocked.Increment(ref _failed);
    }

    public void CompleteRun(Guid runId)
    {
        if (_runId != runId) return;
        _isComplete = true;
        _completedAt = DateTime.UtcNow;
    }
}

public record FingerprintState(
    Guid RunId,
    int TotalTracks,
    int Processed,
    int Fingerprinted,
    int Failed,
    bool IsComplete,
    DateTime StartedAt,
    DateTime? CompletedAt);

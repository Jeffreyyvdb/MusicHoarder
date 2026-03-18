namespace MusicHoarder.Api.Library;

/// <summary>
/// Thread-safe singleton that tracks the progress of the most recent library build run.
/// Follows the same pattern as <see cref="MusicHoarder.Api.Scanner.ScanProgressTracker"/>
/// and <see cref="MusicHoarder.Api.Enrichment.EnrichmentProgressTracker"/>.
/// </summary>
public class LibraryBuilderProgressTracker
{
    private Guid _runId;
    private int _totalTracks;
    private int _processed;
    private int _built;
    private int _failed;
    private bool _isComplete;
    private DateTime _startedAt;
    private DateTime? _completedAt;

    public BuildState? GetCurrent()
    {
        var id = _runId;
        if (id == Guid.Empty) return null;

        return new BuildState(
            id,
            _totalTracks,
            _processed,
            _built,
            _failed,
            _isComplete,
            _startedAt,
            _completedAt);
    }

    public void StartRun(Guid runId, int totalTracks = 0)
    {
        _runId = runId;
        _totalTracks = totalTracks;
        _processed = 0;
        _built = 0;
        _failed = 0;
        _isComplete = false;
        _startedAt = DateTime.UtcNow;
        _completedAt = null;
    }

    public void AddTotal(int count) => Interlocked.Add(ref _totalTracks, count);

    public void IncrementBuilt()
    {
        Interlocked.Increment(ref _processed);
        Interlocked.Increment(ref _built);
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

public record BuildState(
    Guid RunId,
    int TotalTracks,
    int Processed,
    int Built,
    int Failed,
    bool IsComplete,
    DateTime StartedAt,
    DateTime? CompletedAt);

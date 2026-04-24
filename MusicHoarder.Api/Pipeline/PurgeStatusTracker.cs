namespace MusicHoarder.Api.Pipeline;

public record PurgeSnapshot(
    string Status,
    string? Mode,
    Guid? JobId,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    int SongsTotal,
    int SongsProcessed,
    int FilesTotal,
    int FilesDeleted,
    int FilesFailed,
    int SpotifyMatchesCleared,
    string? Error)
{
    public static PurgeSnapshot Idle { get; } =
        new("idle", null, null, null, null, 0, 0, 0, 0, 0, 0, null);
}

public class PurgeStatusTracker
{
    private readonly object _lock = new();
    private PurgeSnapshot _current = PurgeSnapshot.Idle;

    public PurgeSnapshot Get()
    {
        lock (_lock) return _current;
    }

    public void Start(string mode, Guid jobId)
    {
        lock (_lock)
        {
            _current = new PurgeSnapshot(
                Status: "running",
                Mode: mode,
                JobId: jobId,
                StartedAt: DateTime.UtcNow,
                CompletedAt: null,
                SongsTotal: 0,
                SongsProcessed: 0,
                FilesTotal: 0,
                FilesDeleted: 0,
                FilesFailed: 0,
                SpotifyMatchesCleared: 0,
                Error: null);
        }
    }

    public void SetTotals(int songsTotal, int filesTotal)
    {
        lock (_lock)
        {
            _current = _current with { SongsTotal = songsTotal, FilesTotal = filesTotal };
        }
    }

    public void UpdateFilesProgress(int deleted, int failed)
    {
        lock (_lock)
        {
            _current = _current with { FilesDeleted = deleted, FilesFailed = failed };
        }
    }

    public void SetSongsProcessed(int processed)
    {
        lock (_lock)
        {
            _current = _current with { SongsProcessed = processed };
        }
    }

    public void Complete(int spotifyMatchesCleared)
    {
        lock (_lock)
        {
            _current = _current with
            {
                Status = "completed",
                CompletedAt = DateTime.UtcNow,
                SpotifyMatchesCleared = spotifyMatchesCleared,
            };
        }
    }

    public void Fail(string error)
    {
        lock (_lock)
        {
            _current = _current with
            {
                Status = "failed",
                CompletedAt = DateTime.UtcNow,
                Error = error,
            };
        }
    }
}

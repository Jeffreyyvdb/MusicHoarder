namespace MusicHoarder.Api.Download;

/// <summary>Progress of the current (or last) staged-source release run, as the Settings UI polls it.</summary>
public record StagedSourceReleaseSnapshot(
    string Status,
    string? Mode,
    Guid? JobId,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    int Candidates,
    int Released,
    int AlreadyMissing,
    int SkippedVerification,
    int Raced,
    int Failed,
    long BytesReclaimed,
    string? Error)
{
    public static StagedSourceReleaseSnapshot Idle { get; } =
        new("idle", null, null, null, null, 0, 0, 0, 0, 0, 0, 0, null);
}

/// <summary>
/// Owns the single-flight flag, cancellation and progress of the staged-source release. Deliberately
/// not a <see cref="Jobs.JobType"/>: <see cref="Jobs.JobManager.IsAnyRunning"/> feeds the ingest-run
/// monitor and the pipeline status label, and a housekeeping sweep that ticks every hour must not open
/// ingest runs or flip the UI to "running". The destructive purge calls <see cref="CancelAndWaitAsync"/>
/// first so a release can never verify a destination file the purge is about to delete.
/// </summary>
public class StagedSourceReleaseTracker
{
    private readonly object _lock = new();
    private StagedSourceReleaseSnapshot _current = StagedSourceReleaseSnapshot.Idle;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource? _finished;

    public StagedSourceReleaseSnapshot Get()
    {
        lock (_lock) return _current;
    }

    public bool IsRunning
    {
        get { lock (_lock) return _cts is not null; }
    }

    public bool TryStart(string mode, out Guid jobId, out CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_cts is not null)
            {
                jobId = Guid.Empty;
                cancellationToken = CancellationToken.None;
                return false;
            }

            jobId = Guid.NewGuid();
            _cts = new CancellationTokenSource();
            _finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken = _cts.Token;
            _current = StagedSourceReleaseSnapshot.Idle with
            {
                Status = "running",
                Mode = mode,
                JobId = jobId,
                StartedAt = DateTime.UtcNow,
            };
            return true;
        }
    }

    public void SetCandidates(int candidates)
    {
        lock (_lock) _current = _current with { Candidates = candidates };
    }

    public void Report(int released, int alreadyMissing, int skippedVerification, int raced, int failed, long bytesReclaimed)
    {
        lock (_lock)
        {
            _current = _current with
            {
                Released = released,
                AlreadyMissing = alreadyMissing,
                SkippedVerification = skippedVerification,
                Raced = raced,
                Failed = failed,
                BytesReclaimed = bytesReclaimed,
            };
        }
    }

    public void Complete() => Finish("completed", null);

    public void Cancelled() => Finish("cancelled", "Release was cancelled.");

    public void Fail(string error) => Finish("failed", error);

    /// <summary>
    /// Asks a running release to stop and waits until it has. Returns immediately when idle.
    /// </summary>
    public async Task CancelAndWaitAsync(CancellationToken ct = default)
    {
        CancellationTokenSource? cts;
        TaskCompletionSource? finished;
        lock (_lock)
        {
            cts = _cts;
            finished = _finished;
        }

        if (cts is null || finished is null)
            return;

        cts.Cancel();
        await finished.Task.WaitAsync(ct);
    }

    private void Finish(string status, string? error)
    {
        CancellationTokenSource? cts;
        TaskCompletionSource? finished;
        lock (_lock)
        {
            _current = _current with { Status = status, CompletedAt = DateTime.UtcNow, Error = error };
            cts = _cts;
            finished = _finished;
            _cts = null;
            _finished = null;
        }

        cts?.Dispose();
        finished?.TrySetResult();
    }
}

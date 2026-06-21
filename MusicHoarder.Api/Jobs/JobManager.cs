using System.Threading.Channels;

namespace MusicHoarder.Api.Jobs;

public enum JobType { None, Scan, Fingerprint, Enrich, Build, Purge, Download }

public enum JobRunStatus { Idle, Running, Completed, Cancelled, Failed }

public record StepSnapshot(string Status, bool IsPaused);

public record ProgressSnapshot(
    string Status,
    Guid? JobId,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    bool IsComplete,
    int Discovered,
    int Scanned,
    int Fingerprinted,
    int Enriched,
    int NeedsReview,
    int Built,
    int Failed,
    StepSnapshot Scan,
    StepSnapshot Fingerprint,
    StepSnapshot Enrich,
    StepSnapshot Build,
    int Downloaded = 0,
    StepSnapshot? Download = null);

/// <summary>
/// Thread-safe singleton that provides per-step job orchestration.
/// Each pipeline step (Scan, Fingerprint, Enrich, Build) runs independently
/// and can be paused/resumed individually.
/// </summary>
public class JobManager
{
    private readonly object _lock = new();

    private class StepState
    {
        public JobRunStatus Status = JobRunStatus.Idle;
        public Guid JobId;
        public DateTime? StartedAt;
        public DateTime? CompletedAt;
        public CancellationTokenSource? Cts;
        public bool IsPaused;
    }

    private readonly Dictionary<JobType, StepState> _steps = new()
    {
        [JobType.Scan] = new(),
        [JobType.Fingerprint] = new(),
        [JobType.Enrich] = new(),
        [JobType.Build] = new(),
        [JobType.Purge] = new(),
        [JobType.Download] = new(),
    };

    private readonly Channel<Guid> _scanChannel =
        Channel.CreateBounded<Guid>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

    private readonly Channel<Guid> _fingerprintChannel =
        Channel.CreateBounded<Guid>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

    private readonly Channel<Guid> _buildChannel =
        Channel.CreateBounded<Guid>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

    private readonly Channel<Guid> _downloadChannel =
        Channel.CreateBounded<Guid>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

    public ChannelReader<Guid> ScanTriggers => _scanChannel.Reader;
    public ChannelReader<Guid> FingerprintTriggers => _fingerprintChannel.Reader;
    public ChannelReader<Guid> BuildTriggers => _buildChannel.Reader;
    public ChannelReader<Guid> DownloadTriggers => _downloadChannel.Reader;

    /// <summary>
    /// Start a job for the given step. Returns false if that step is already running.
    /// Clears the paused flag so the step can auto-trigger again.
    /// Different steps may run concurrently.
    /// </summary>
    public bool TryStartJob(JobType jobType, out Guid jobId, out CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var step = _steps[jobType];
            if (step.Status == JobRunStatus.Running)
            {
                jobId = Guid.Empty;
                cancellationToken = default;
                return false;
            }

            jobId = Guid.NewGuid();
            step.IsPaused = false;
            SetStepRunning(step, jobId);
            cancellationToken = step.Cts!.Token;
        }

        // Purge is not driven by an auto-trigger channel — the endpoint kicks off its own Task.
        // Enrich has no JobManager channel either: its work queue is the separate per-song
        // EnrichmentPipelineChannel, so TryStartJob(Enrich) only flips the step Running.
        if (jobType is not (JobType.Purge or JobType.Enrich))
            GetChannel(jobType).Writer.TryWrite(jobId);
        return true;
    }

    /// <summary>
    /// Called by background services when they auto-detect work.
    /// Returns false if the step is already running or is paused.
    /// </summary>
    public bool TryRegisterAutoJob(JobType jobType, Guid jobId, out CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var step = _steps[jobType];
            if (step.Status == JobRunStatus.Running || step.IsPaused)
            {
                cancellationToken = default;
                return false;
            }

            SetStepRunning(step, jobId);
            cancellationToken = step.Cts!.Token;
            return true;
        }
    }

    /// <summary>
    /// Returns the cancellation token for the given step's current job.
    /// </summary>
    public CancellationToken GetCancellationToken(JobType jobType)
    {
        lock (_lock)
        {
            var step = _steps[jobType];
            return step.Cts?.Token ?? CancellationToken.None;
        }
    }

    public void SignalComplete(JobType jobType, Guid jobId, bool cancelled = false)
    {
        lock (_lock)
        {
            var step = _steps[jobType];
            if (step.JobId != jobId) return;
            step.Status = cancelled ? JobRunStatus.Cancelled : JobRunStatus.Completed;
            step.CompletedAt = DateTime.UtcNow;
        }
    }

    public void SignalFailed(JobType jobType, Guid jobId)
    {
        lock (_lock)
        {
            var step = _steps[jobType];
            if (step.JobId != jobId) return;
            step.Status = JobRunStatus.Failed;
            step.CompletedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Pause a step: cancels any in-flight job and prevents auto-triggering until resumed.
    /// </summary>
    public bool PauseStep(JobType jobType)
    {
        lock (_lock)
        {
            var step = _steps[jobType];
            step.IsPaused = true;
            if (step.Status == JobRunStatus.Running)
                step.Cts?.Cancel();
            return true;
        }
    }

    /// <summary>
    /// Resume a paused step so auto-triggering can start again.
    /// </summary>
    public bool ResumeStep(JobType jobType)
    {
        lock (_lock)
        {
            var step = _steps[jobType];
            step.IsPaused = false;
            return true;
        }
    }

    public bool IsStepPaused(JobType jobType)
    {
        lock (_lock) return _steps[jobType].IsPaused;
    }

    /// <summary>Cancel all running steps.</summary>
    public bool Cancel()
    {
        lock (_lock)
        {
            var cancelled = false;
            foreach (var step in _steps.Values)
            {
                if (step.Status == JobRunStatus.Running)
                {
                    step.Cts?.Cancel();
                    cancelled = true;
                }
            }
            return cancelled;
        }
    }

    public bool IsAnyRunning()
    {
        lock (_lock)
        {
            foreach (var step in _steps.Values)
                if (step.Status == JobRunStatus.Running) return true;
            return false;
        }
    }

    public StepSnapshot GetStepSnapshot(JobType jobType)
    {
        lock (_lock)
        {
            var step = _steps[jobType];
            var statusLabel = step.IsPaused && step.Status != JobRunStatus.Running
                ? "Paused"
                : step.Status switch
                {
                    JobRunStatus.Running => "Running",
                    JobRunStatus.Completed => "Completed",
                    JobRunStatus.Cancelled => "Cancelled",
                    JobRunStatus.Failed => "Failed",
                    _ => "Idle"
                };
            return new StepSnapshot(statusLabel, step.IsPaused);
        }
    }

    // ── Backward-compat helpers used by ScannerBackgroundService ──────────────

    /// <summary>Backward compat: returns the CT for the most recently started step.</summary>
    public CancellationToken GetCurrentCancellationToken()
    {
        lock (_lock)
        {
            foreach (var step in _steps.Values)
                if (step.Status == JobRunStatus.Running && step.Cts is not null)
                    return step.Cts.Token;
            return CancellationToken.None;
        }
    }

    /// <summary>Backward compat: signals complete on whichever step owns this jobId.</summary>
    public void SignalComplete(Guid jobId, bool cancelled = false)
    {
        lock (_lock)
        {
            foreach (var (type, step) in _steps)
            {
                if (step.JobId == jobId)
                {
                    step.Status = cancelled ? JobRunStatus.Cancelled : JobRunStatus.Completed;
                    step.CompletedAt = DateTime.UtcNow;
                    return;
                }
            }
        }
    }

    /// <summary>Backward compat: signals failure on whichever step owns this jobId.</summary>
    public void SignalFailed(Guid jobId)
    {
        lock (_lock)
        {
            foreach (var (type, step) in _steps)
            {
                if (step.JobId == jobId)
                {
                    step.Status = JobRunStatus.Failed;
                    step.CompletedAt = DateTime.UtcNow;
                    return;
                }
            }
        }
    }

    private void SetStepRunning(StepState step, Guid jobId)
    {
        step.JobId = jobId;
        step.Status = JobRunStatus.Running;
        step.StartedAt = DateTime.UtcNow;
        step.CompletedAt = null;
        step.Cts?.Dispose();
        step.Cts = new CancellationTokenSource();
    }

    private Channel<Guid> GetChannel(JobType type) => type switch
    {
        JobType.Scan => _scanChannel,
        JobType.Fingerprint => _fingerprintChannel,
        JobType.Build => _buildChannel,
        JobType.Download => _downloadChannel,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };
}

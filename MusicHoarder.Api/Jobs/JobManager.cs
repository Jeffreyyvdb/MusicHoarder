using System.Threading.Channels;

namespace MusicHoarder.Api.Jobs;

public enum JobType { None, Scan, Fingerprint, Enrich, Build }

public enum JobRunStatus { Idle, Running, Completed, Cancelled, Failed }

public record JobStatusSnapshot(
    JobType JobType,
    JobRunStatus Status,
    Guid? JobId,
    DateTime? StartedAt,
    DateTime? CompletedAt);

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
    int Built,
    int Failed);

/// <summary>
/// Thread-safe singleton that provides job orchestration for pipeline HTTP endpoints.
/// Enforces the "one active job at a time" contract (HTTP 409), exposes trigger
/// channels for background services, and provides cancellation support for
/// in-flight jobs.
/// </summary>
public class JobManager
{
    private readonly object _lock = new();
    private JobType _jobType;
    private JobRunStatus _jobStatus = JobRunStatus.Idle;
    private Guid _jobId;
    private DateTime? _startedAt;
    private DateTime? _completedAt;
    private CancellationTokenSource? _jobCts;

    // Bounded to 1: only one pending trigger per job type at a time.
    private readonly Channel<Guid> _scanChannel =
        Channel.CreateBounded<Guid>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

    private readonly Channel<Guid> _enrichChannel =
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

    public ChannelReader<Guid> ScanTriggers => _scanChannel.Reader;
    public ChannelReader<Guid> FingerprintTriggers => _fingerprintChannel.Reader;
    public ChannelReader<Guid> EnrichTriggers => _enrichChannel.Reader;
    public ChannelReader<Guid> BuildTriggers => _buildChannel.Reader;

    /// <summary>
    /// Called by HTTP endpoints to start a new job.
    /// Returns <c>false</c> if another job is already running (HTTP 409).
    /// On success, writes the job ID to the appropriate trigger channel so the
    /// corresponding background service picks it up.
    /// </summary>
    public bool TryStartJob(JobType jobType, out Guid jobId, out CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_jobStatus == JobRunStatus.Running)
            {
                jobId = Guid.Empty;
                cancellationToken = default;
                return false;
            }

            jobId = Guid.NewGuid();
            SetRunning(jobId, jobType);
            cancellationToken = _jobCts!.Token;
        }

        var channel = jobType switch
        {
            JobType.Scan => _scanChannel,
            JobType.Fingerprint => _fingerprintChannel,
            JobType.Enrich => _enrichChannel,
            JobType.Build => _buildChannel,
            _ => throw new ArgumentOutOfRangeException(nameof(jobType))
        };
        channel.Writer.TryWrite(jobId);

        return true;
    }

    /// <summary>
    /// Called by background services when they auto-detect work to do (polling).
    /// Returns <c>false</c> if another job is already running — the auto-cycle should be skipped.
    /// On success, returns the cancellation token for the new job.
    /// </summary>
    public bool TryRegisterAutoJob(JobType jobType, Guid jobId, out CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_jobStatus == JobRunStatus.Running)
            {
                cancellationToken = default;
                return false;
            }

            SetRunning(jobId, jobType);
            cancellationToken = _jobCts!.Token;
            return true;
        }
    }

    /// <summary>
    /// Returns the cancellation token for the current job.
    /// Called by background services immediately after reading a job ID from a trigger channel.
    /// </summary>
    public CancellationToken GetCurrentCancellationToken()
    {
        lock (_lock) return _jobCts?.Token ?? CancellationToken.None;
    }

    /// <summary>Called by background services on normal job completion.</summary>
    public void SignalComplete(Guid jobId, bool cancelled = false)
    {
        lock (_lock)
        {
            if (_jobId != jobId) return;
            _jobStatus = cancelled ? JobRunStatus.Cancelled : JobRunStatus.Completed;
            _completedAt = DateTime.UtcNow;
        }
    }

    /// <summary>Called by background services on unhandled job error.</summary>
    public void SignalFailed(Guid jobId)
    {
        lock (_lock)
        {
            if (_jobId != jobId) return;
            _jobStatus = JobRunStatus.Failed;
            _completedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Cancels the currently running job via its internal CancellationTokenSource.
    /// Returns <c>false</c> if no job is running.
    /// </summary>
    public bool Cancel()
    {
        lock (_lock)
        {
            if (_jobStatus != JobRunStatus.Running) return false;
            _jobCts?.Cancel();
            return true;
        }
    }

    public bool IsRunning()
    {
        lock (_lock) return _jobStatus == JobRunStatus.Running;
    }

    public JobStatusSnapshot GetStatus()
    {
        lock (_lock)
        {
            return new JobStatusSnapshot(
                _jobType,
                _jobStatus,
                _jobId == Guid.Empty ? null : _jobId,
                _startedAt,
                _completedAt);
        }
    }

    private void SetRunning(Guid jobId, JobType jobType)
    {
        _jobId = jobId;
        _jobType = jobType;
        _jobStatus = JobRunStatus.Running;
        _startedAt = DateTime.UtcNow;
        _completedAt = null;
        _jobCts?.Dispose();
        _jobCts = new CancellationTokenSource();
    }
}

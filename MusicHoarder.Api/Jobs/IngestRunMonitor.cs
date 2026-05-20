using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Jobs;

/// <summary>
/// Persists ingest sessions as <see cref="IngestRun"/> rows. The pipeline has no single "run"
/// concept — scan, fingerprint, enrich and build are independent steps — so a run is defined as
/// the window where <em>any</em> step is running. This service watches <see cref="JobManager"/>
/// for the idle→running edge (open a run), refreshes its counters while active, and finalizes it
/// on the running→idle edge (status, duration, throughput). Rows are tagged with the owner's id
/// from <see cref="IOwnerLookupService"/> because hosted-service DB scopes have no current user.
/// </summary>
public class IngestRunMonitor(
    IServiceScopeFactory scopeFactory,
    JobManager jobManager,
    IOwnerLookupService ownerLookup,
    IOptions<MusicEnricherOptions> options,
    ILogger<IngestRunMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private const int LogTailCap = 20;

    private static readonly JsonSerializerOptions LogJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly JobType[] PipelineSteps =
        [JobType.Scan, JobType.Fingerprint, JobType.Enrich, JobType.Build];

    private Guid? _currentRunId;
    private readonly HashSet<JobType> _observedRunning = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Ingest run monitor started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Ingest run monitor tick failed");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// One monitor iteration: detect running edges, refresh the open run's counters, and finalize
    /// on the idle edge. Public so the lifecycle can be unit-tested without the polling loop.
    /// </summary>
    public async Task TickAsync(CancellationToken ct)
    {
        var running = jobManager.IsAnyRunning();

        // Track which steps participated in the current run so finalization ignores stale
        // Completed/Cancelled/Failed statuses left over from a previous run.
        if (running)
        {
            foreach (var step in PipelineSteps)
                if (jobManager.GetStepSnapshot(step).Status == "Running")
                    _observedRunning.Add(step);
        }

        if (running && _currentRunId is null)
        {
            await OpenRunAsync(ct);
        }
        else if (running && _currentRunId is { } openId)
        {
            await UpdateRunAsync(openId, finalize: false, ct);
        }
        else if (!running && _currentRunId is { } closeId)
        {
            await UpdateRunAsync(closeId, finalize: true, ct);
            _currentRunId = null;
            _observedRunning.Clear();
        }
    }

    private async Task OpenRunAsync(CancellationToken ct)
    {
        var opts = options.Value;
        var run = new IngestRun
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerLookup.OwnerUserId,
            StartedAtUtc = DateTime.UtcNow,
            Status = IngestRunStatus.Running,
            SourcePath = opts.SourceDirectory ?? string.Empty,
            DestinationPath = opts.DestinationDirectory ?? string.Empty,
        };

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        db.IngestRuns.Add(run);
        await db.SaveChangesAsync(ct);

        _currentRunId = run.Id;
        logger.LogInformation("Opened ingest run {RunId}", run.Id);
    }

    private async Task UpdateRunAsync(Guid runId, bool finalize, CancellationToken ct)
    {
        var ownerId = ownerLookup.OwnerUserId;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        var run = await db.IngestRuns
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null) return;

        var active = db.Songs
            .IgnoreQueryFilters()
            .Where(s => s.DeletedAtUtc == null && s.OwnerUserId == ownerId);

        var counts = await PipelineSnapshot.ComputeCountsAsync(active, ct);
        run.TracksDiscovered = counts.Discovered;
        run.TracksProcessed = counts.Processed;
        run.TracksFingerprinted = counts.Fingerprinted;
        run.TracksEnriched = counts.Enriched;
        run.TracksCopied = counts.Copied;
        run.TracksReview = counts.Review;
        run.TracksFailed = counts.Failed;

        var now = DateTime.UtcNow;
        var activity = await PipelineSnapshot.ComputeRecentActivityAsync(active, LogTailCap, now, ct);
        run.LogTailJson = JsonSerializer.Serialize(
            activity.Select(a => new { a.Id, a.Type, a.Track, a.Artist, a.Time }),
            LogJsonOptions);

        if (finalize)
        {
            run.EndedAtUtc = now;
            run.Status = ResolveFinalStatus();
            var durationSeconds = (now - run.StartedAtUtc).TotalSeconds;
            run.ThroughputPerSec = durationSeconds > 0
                ? Math.Round(run.TracksProcessed / durationSeconds, 2)
                : 0;
        }

        await db.SaveChangesAsync(ct);

        if (finalize)
            logger.LogInformation("Finalized ingest run {RunId} as {Status}", runId, run.Status);
    }

    private IngestRunStatus ResolveFinalStatus()
    {
        var anyFailed = false;
        var anyCancelled = false;
        foreach (var step in _observedRunning)
        {
            switch (jobManager.GetStepSnapshot(step).Status)
            {
                case "Failed":
                    anyFailed = true;
                    break;
                case "Cancelled":
                    anyCancelled = true;
                    break;
            }
        }

        if (anyFailed) return IngestRunStatus.Failed;
        if (anyCancelled) return IngestRunStatus.Cancelled;
        return IngestRunStatus.Completed;
    }
}

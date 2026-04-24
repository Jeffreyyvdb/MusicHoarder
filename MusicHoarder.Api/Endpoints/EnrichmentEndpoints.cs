using System.Runtime.CompilerServices;
using System.Text.Json;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Pipeline;
using MusicHoarder.Api.Scanner;

namespace MusicHoarder.Api.Endpoints;

public static class EnrichmentEndpoints
{
    private static readonly JsonSerializerOptions SseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static IEndpointRouteBuilder MapEnrichmentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/enrichment").WithTags("Enrichment");

        group.MapPost("/scan", (JobManager jobManager) =>
            {
                if (!jobManager.TryStartJob(JobType.Scan, out var jobId, out _))
                    return Results.Conflict(new { message = "A job is already running. Use POST /api/enrichment/cancel to stop it first." });

                return Results.Accepted("/api/enrichment/status", new { jobId });
            })
            .WithName("TriggerEnrichmentScan")
            .WithSummary("Trigger the ScannerService to index the source library.");

        group.MapPost("/enrich", (JobManager jobManager) =>
            {
                if (!jobManager.TryStartJob(JobType.Enrich, out var jobId, out _))
                    return Results.Conflict(new { message = "A job is already running. Use POST /api/enrichment/cancel to stop it first." });

                return Results.Accepted("/api/enrichment/status", new { jobId });
            })
            .WithName("TriggerEnrich")
            .WithSummary("Trigger the EnrichmentService to enrich pending tracks via AcoustID/MusicBrainz.");

        group.MapPost("/fingerprint", (JobManager jobManager) =>
            {
                if (!jobManager.TryStartJob(JobType.Fingerprint, out var jobId, out _))
                    return Results.Conflict(new { message = "Fingerprint step is already running." });

                return Results.Accepted("/api/enrichment/status", new { jobId });
            })
            .WithName("TriggerFingerprint")
            .WithSummary("Trigger the FingerprintService to fingerprint tracks with missing fingerprints.");

        group.MapPost("/build", (JobManager jobManager) =>
            {
                if (!jobManager.TryStartJob(JobType.Build, out var jobId, out _))
                    return Results.Conflict(new { message = "Build step is already running." });

                return Results.Accepted("/api/enrichment/status", new { jobId });
            })
            .WithName("TriggerBuild")
            .WithSummary("Trigger the LibraryBuilderService to copy and tag matched tracks to the destination.");

        group.MapPost("/cancel", (JobManager jobManager) =>
            {
                if (!jobManager.Cancel())
                    return Results.Ok(new { message = "No job is currently running." });

                return Results.Ok(new { message = "Cancellation requested for the running job." });
            })
            .WithName("CancelJob")
            .WithSummary("Cancel the currently running job.");

        group.MapPost("/pause", (string step, JobManager jobManager) =>
            {
                if (!ProgressSnapshotFactory.TryParseJobType(step, out var jobType))
                    return Results.BadRequest(new { message = $"Invalid step '{step}'. Use scan, fingerprint, enrich, or build." });

                jobManager.PauseStep(jobType);
                return Results.Ok(new { message = $"{step} paused.", step, paused = true });
            })
            .WithName("PauseStep")
            .WithSummary("Pause a pipeline step. Cancels any in-flight job for that step and prevents auto-triggering.");

        group.MapPost("/resume", (string step, JobManager jobManager) =>
            {
                if (!ProgressSnapshotFactory.TryParseJobType(step, out var jobType))
                    return Results.BadRequest(new { message = $"Invalid step '{step}'. Use scan, fingerprint, enrich, or build." });

                jobManager.ResumeStep(jobType);
                return Results.Ok(new { message = $"{step} resumed.", step, paused = false });
            })
            .WithName("ResumeStep")
            .WithSummary("Resume a paused pipeline step so it can auto-trigger again.");

        group.MapGet("/status", (
                JobManager jobManager,
                ScanProgressTracker scanTracker,
                FingerprintProgressTracker fingerprintTracker,
                EnrichmentProgressTracker enrichmentTracker,
                LibraryBuilderProgressTracker buildTracker) =>
            {
                var snapshot = ProgressSnapshotFactory.Create(
                    jobManager, scanTracker, fingerprintTracker, enrichmentTracker, buildTracker);
                return Results.Ok(new { Progress = snapshot });
            })
            .WithName("GetEnrichmentStatus")
            .WithSummary("Get the current job status and a progress snapshot.");

        group.MapGet("/progress", StreamProgress)
            .WithName("StreamEnrichmentProgress")
            .WithSummary("SSE stream that emits a ProgressSnapshot every second while a job is running.");

        group.MapPost("/scrape-trackers", () =>
                Results.Json(new { message = "Tracker scraping is not yet implemented." }, statusCode: 501))
            .WithName("ScrapeTrackers")
            .WithSummary("Trigger on-demand tracker scraping (not yet implemented).");

        group.MapPost("/purge-post-fingerprint", (
                JobManager jobManager,
                IServiceScopeFactory scopeFactory,
                PurgeStatusTracker tracker,
                ILoggerFactory loggerFactory) =>
                StartPurge(
                    mode: "post-fingerprint",
                    jobManager,
                    scopeFactory,
                    tracker,
                    loggerFactory,
                    (service, jobId, ct) => service.ResetPostFingerprintAsync(jobId, ct)))
            .WithName("PurgePostFingerprint")
            .WithSummary("Start a background reset of enrichment, lyrics, duplicate, and library-build state. Returns 202 Accepted with a jobId; poll /purge-status for progress.");

        group.MapPost("/purge-all", (
                JobManager jobManager,
                IServiceScopeFactory scopeFactory,
                PurgeStatusTracker tracker,
                ILoggerFactory loggerFactory) =>
                StartPurge(
                    mode: "all",
                    jobManager,
                    scopeFactory,
                    tracker,
                    loggerFactory,
                    (service, jobId, ct) => service.PurgeAllAsync(jobId, ct)))
            .WithName("PurgeAll")
            .WithSummary("Start a background hard-delete of every song, provider attempt, cached Spotify match, and copied destination file. Returns 202 Accepted.");

        group.MapGet("/purge-status", (PurgeStatusTracker tracker) => Results.Ok(tracker.Get()))
            .WithName("GetPurgeStatus")
            .WithSummary("Get the current purge status snapshot. Includes the last completed result until a new purge starts.");

        return app;
    }

    private static IResult StartPurge(
        string mode,
        JobManager jobManager,
        IServiceScopeFactory scopeFactory,
        PurgeStatusTracker tracker,
        ILoggerFactory loggerFactory,
        Func<IPipelinePurgeService, Guid, CancellationToken, Task<PurgeResult>> run)
    {
        if (!jobManager.TryStartJob(JobType.Purge, out var jobId, out var cancellationToken))
            return Results.Conflict(new { message = "A purge is already running. Wait for it to finish." });

        var logger = loggerFactory.CreateLogger("MusicHoarder.Api.Pipeline.PipelinePurge");

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IPipelinePurgeService>();
                await run(service, jobId, cancellationToken);
                jobManager.SignalComplete(JobType.Purge, jobId);
            }
            catch (OperationCanceledException)
            {
                jobManager.SignalComplete(JobType.Purge, jobId, cancelled: true);
                tracker.Fail("Purge was cancelled.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Purge ({Mode}) failed", mode);
                jobManager.SignalFailed(JobType.Purge, jobId);
                tracker.Fail(ex.Message);
            }
        });

        return Results.Accepted("/api/enrichment/purge-status", new { jobId, mode });
    }

    private static IResult StreamProgress(
        HttpContext context,
        JobManager jobManager,
        ScanProgressTracker scanTracker,
        FingerprintProgressTracker fingerprintTracker,
        EnrichmentProgressTracker enrichmentTracker,
        LibraryBuilderProgressTracker buildTracker)
    {
        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";
        context.Response.Headers.Append("X-Accel-Buffering", "no");

        var ct = context.RequestAborted;

        async IAsyncEnumerable<string> StreamJson([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var wasRunning = false;
            while (!cancellationToken.IsCancellationRequested)
            {
                var snapshot = ProgressSnapshotFactory.Create(
                    jobManager, scanTracker, fingerprintTracker, enrichmentTracker, buildTracker);
                yield return JsonSerializer.Serialize(snapshot, SseJsonOptions);

                if (snapshot.IsComplete && wasRunning)
                    yield break;

                if (!snapshot.IsComplete)
                    wasRunning = true;

                await Task.Delay(1000, cancellationToken);
            }
        }

        return Results.ServerSentEvents(StreamJson(ct));
    }
}

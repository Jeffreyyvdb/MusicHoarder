using System.Runtime.CompilerServices;
using System.Text.Json;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Library;
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

        return app;
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

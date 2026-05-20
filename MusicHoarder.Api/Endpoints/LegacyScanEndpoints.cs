using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Scanner;

namespace MusicHoarder.Api.Endpoints;

public static class LegacyScanEndpoints
{
    public static IEndpointRouteBuilder MapLegacyScanEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/scan", (JobManager jobManager) =>
            {
                if (!jobManager.TryStartJob(JobType.Scan, out var scanId, out _))
                    return Results.Conflict(new { message = "A job is already running. Use POST /api/enrichment/cancel to stop it first." });

                return Results.Accepted($"/scan/{scanId}/progress", new { scanId });
            })
            .WithName("TriggerScan")
            .WithSummary("Trigger a library scan (legacy endpoint, prefer /api/enrichment/scan).")
            // Triggers a real filesystem scan on the host; owner-only, matching /api/enrichment/scan.
            .RequireOwner();

        app.MapGet("/scan/{scanId}/progress", (Guid scanId, ScanProgressTracker tracker) =>
            {
                var state = tracker.GetCurrent();
                if (state is null || state.ScanId != scanId)
                    return Results.NotFound(new { message = "No scan found with that id." });

                return Results.Ok(state);
            })
            .WithName("GetScanProgress")
            .WithSummary("Get a snapshot of scan progress by scan ID.");

        return app;
    }
}

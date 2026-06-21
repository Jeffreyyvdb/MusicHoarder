using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Endpoints;

public static class RunsEndpoints
{
    private const int ListCap = 50;

    public static IEndpointRouteBuilder MapRunsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/runs", GetRuns).WithName("GetRuns").RequireOwner();
        app.MapGet("/runs/{id:guid}", GetRun).WithName("GetRun").RequireOwner();
        return app;
    }

    private static async Task<IResult> GetRuns(MusicHoarderDbContext db)
    {
        var runs = await db.IngestRuns
            .OrderByDescending(r => r.StartedAtUtc)
            .Take(ListCap)
            .ToListAsync();

        return Results.Ok(runs.Select(MapSummary));
    }

    private static async Task<IResult> GetRun(Guid id, MusicHoarderDbContext db)
    {
        var run = await db.IngestRuns.FirstOrDefaultAsync(r => r.Id == id);
        if (run is null) return Results.NotFound();

        JsonElement? logTail = null;
        if (!string.IsNullOrEmpty(run.LogTailJson))
        {
            try
            {
                logTail = JsonSerializer.Deserialize<JsonElement>(run.LogTailJson);
            }
            catch (JsonException)
            {
                logTail = null;
            }
        }

        return Results.Ok(new
        {
            run.Id,
            Status = StatusLabel(run.Status),
            run.StartedAtUtc,
            run.EndedAtUtc,
            run.SourcePath,
            run.DestinationPath,
            run.TriggerLabel,
            run.TracksDiscovered,
            run.TracksProcessed,
            run.TracksFingerprinted,
            run.TracksEnriched,
            run.TracksCopied,
            run.TracksReview,
            run.TracksFailed,
            run.ThroughputPerSec,
            DurationSeconds = DurationSeconds(run),
            LogTail = logTail,
        });
    }

    private static object MapSummary(IngestRun r) => new
    {
        r.Id,
        Status = StatusLabel(r.Status),
        r.StartedAtUtc,
        r.EndedAtUtc,
        r.SourcePath,
        r.DestinationPath,
        r.TriggerLabel,
        r.TracksDiscovered,
        r.TracksProcessed,
        r.TracksFingerprinted,
        r.TracksEnriched,
        r.TracksCopied,
        r.TracksReview,
        r.TracksFailed,
        r.ThroughputPerSec,
        DurationSeconds = DurationSeconds(r),
    };

    private static string StatusLabel(IngestRunStatus status) => status switch
    {
        IngestRunStatus.Running => "running",
        IngestRunStatus.Completed => "completed",
        IngestRunStatus.Cancelled => "cancelled",
        IngestRunStatus.Failed => "failed",
        IngestRunStatus.Interrupted => "interrupted",
        _ => "completed"
    };

    private static double? DurationSeconds(IngestRun r) =>
        r.EndedAtUtc is { } ended ? Math.Round((ended - r.StartedAtUtc).TotalSeconds, 0) : null;
}

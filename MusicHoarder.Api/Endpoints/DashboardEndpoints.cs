using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Scanner;

namespace MusicHoarder.Api.Endpoints;

public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/stats", GetStats).WithName("GetStats");
        app.MapGet("/overview", GetOverview).WithName("GetOverview");
        return app;
    }

    private static async Task<IResult> GetStats(MusicHoarderDbContext db)
    {
        var active = db.Songs.Where(s => s.DeletedAtUtc == null);
        var totalCount = await active.CountAsync();
        var deletedCount = await db.Songs.CountAsync(s => s.DeletedAtUtc != null);

        var storage = await active
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalBytes = g.Sum(s => s.FileSizeBytes),
                AvgBytes = (long)g.Average(s => s.FileSizeBytes),
            })
            .FirstOrDefaultAsync();

        var duration = await active
            .Where(s => s.DurationSeconds != null)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalSeconds = g.Sum(s => s.DurationSeconds ?? 0),
                TrackCountWithDuration = g.Count(),
            })
            .FirstOrDefaultAsync();

        var byExtensionRaw = await active
            .GroupBy(s => s.Extension)
            .Select(g => new { Extension = g.Key, Count = g.Count() })
            .ToListAsync();
        var byExtension = byExtensionRaw
            .GroupBy(x => x.Extension?.ToLowerInvariant() ?? "")
            .Select(g => new { Extension = g.Key, Count = g.Sum(x => x.Count) })
            .OrderByDescending(x => x.Count)
            .ToList();

        var enrichment = await active
            .GroupBy(_ => 1)
            .Select(g => new
            {
                WithFingerprint = g.Count(s => s.Fingerprint != null && s.Fingerprint != ""),
                WithMusicBrainzId = g.Count(s => s.MusicBrainzId != null && s.MusicBrainzId != ""),
                WithSpotifyId = g.Count(s => s.SpotifyId != null && s.SpotifyId != ""),
                WithIsrc = g.Count(s => s.Isrc != null && s.Isrc != ""),
                WithArtist = g.Count(s => s.Artist != null && s.Artist != ""),
                WithAlbum = g.Count(s => s.Album != null && s.Album != ""),
                WithTitle = g.Count(s => s.Title != null && s.Title != ""),
            })
            .FirstOrDefaultAsync();

        var indexWindow = await active
            .GroupBy(_ => 1)
            .Select(g => new
            {
                OldestIndexed = g.Min(s => s.IndexedAtUtc),
                NewestIndexed = g.Max(s => s.IndexedAtUtc),
                OldestModified = g.Min(s => s.LastModifiedUtc),
                NewestModified = g.Max(s => s.LastModifiedUtc),
            })
            .FirstOrDefaultAsync();

        var stats = new
        {
            Tracks = new
            {
                Total = totalCount,
                Deleted = deletedCount,
            },
            Storage = storage == null
                ? null
                : new
                {
                    TotalBytes = storage.TotalBytes,
                    TotalGiB = Math.Round(storage.TotalBytes / (1024.0 * 1024.0 * 1024.0), 2),
                    AverageBytesPerTrack = storage.AvgBytes,
                },
            Duration = duration == null
                ? null
                : new
                {
                    TotalSeconds = duration.TotalSeconds,
                    TotalHours = Math.Round(duration.TotalSeconds / 3600.0, 1),
                    TracksWithDuration = duration.TrackCountWithDuration,
                    AverageSecondsPerTrack = duration.TrackCountWithDuration > 0
                        ? Math.Round(duration.TotalSeconds / (double)duration.TrackCountWithDuration, 1)
                        : (double?)null,
                },
            ByExtension = byExtension,
            Enrichment = enrichment == null
                ? null
                : new
                {
                    enrichment.WithFingerprint,
                    enrichment.WithMusicBrainzId,
                    enrichment.WithSpotifyId,
                    enrichment.WithIsrc,
                    enrichment.WithArtist,
                    enrichment.WithAlbum,
                    enrichment.WithTitle,
                    FingerprintPct = totalCount > 0 ? Math.Round(100.0 * enrichment.WithFingerprint / totalCount, 1) : 0,
                    MusicBrainzPct = totalCount > 0 ? Math.Round(100.0 * enrichment.WithMusicBrainzId / totalCount, 1) : 0,
                },
            IndexWindow = indexWindow == null
                ? null
                : new
                {
                    OldestIndexedUtc = indexWindow.OldestIndexed,
                    NewestIndexedUtc = indexWindow.NewestIndexed,
                    OldestFileModifiedUtc = indexWindow.OldestModified,
                    NewestFileModifiedUtc = indexWindow.NewestModified,
                },
        };

        return Results.Ok(stats);
    }

    private static async Task<IResult> GetOverview(
        MusicHoarderDbContext db,
        IOptions<MusicEnricherOptions> options,
        ScanProgressTracker scanTracker,
        FingerprintProgressTracker fingerprintTracker,
        EnrichmentProgressTracker enrichmentTracker)
    {
        var opts = options.Value;
        var active = db.Songs.Where(s => s.DeletedAtUtc == null);

        var counts = await PipelineSnapshot.ComputeCountsAsync(active);

        var indexWindow = await active
            .GroupBy(_ => 1)
            .Select(g => new
            {
                NewestIndexed = g.Max(s => s.IndexedAtUtc),
            })
            .FirstOrDefaultAsync();

        var scanState = scanTracker.GetCurrent();
        var scanRunning = scanState is { IsComplete: false };
        var fingerprintState = fingerprintTracker.GetCurrent();
        var fingerprintRunning = fingerprintState is { IsComplete: false };
        var enrichmentState = enrichmentTracker.GetCurrent();
        var enrichmentRunning = enrichmentState is { IsComplete: false };

        var now = DateTime.UtcNow;
        var activities = await PipelineSnapshot.ComputeRecentActivityAsync(active, 50, now);

        var startedAt = scanState?.StartedAt ?? indexWindow?.NewestIndexed ?? now;

        var overview = new
        {
            SourcePath = opts.SourceDirectory,
            DestinationPath = opts.DestinationDirectory,
            Scan = scanState == null ? null : new
            {
                scanState.ScanId,
                scanState.TotalFiles,
                scanState.Processed,
                scanState.NewFiles,
                scanState.ChangedFiles,
                scanState.SkippedFiles,
                scanState.FailedFiles,
                scanState.IsComplete,
                scanState.StartedAt,
                scanState.CompletedAt,
            },
            Job = new
            {
                Status = scanRunning || fingerprintRunning || enrichmentRunning ? "running" : "completed",
                StartedAt = startedAt,
                TracksDiscovered = counts.Discovered,
                TracksProcessed = counts.Processed,
                TracksFingerprinted = counts.Fingerprinted,
                TracksEnriched = counts.Enriched,
                TracksBuildEligible = counts.BuildEligible,
                TracksCopied = counts.Copied,
                TracksReview = counts.Review,
                TracksFailed = counts.Failed,
            },
            Fingerprint = fingerprintState is { IsComplete: false } ? new
            {
                fingerprintState.RunId,
                fingerprintState.TotalTracks,
                fingerprintState.Processed,
                fingerprintState.Fingerprinted,
                fingerprintState.Failed,
                fingerprintState.IsComplete,
                fingerprintState.StartedAt,
                fingerprintState.CompletedAt,
            } : null,
            Enrichment = enrichmentState is { IsComplete: false } ? new
            {
                enrichmentState.RunId,
                enrichmentState.TotalTracks,
                enrichmentState.Processed,
                enrichmentState.Enriched,
                enrichmentState.Failed,
                enrichmentState.NeedsReview,
                enrichmentState.IsComplete,
                enrichmentState.StartedAt,
                enrichmentState.CompletedAt,
            } : null,
            RecentActivity = activities.Select(a => new
            {
                a.Id,
                a.Type,
                a.Track,
                a.Artist,
                a.Time,
            }),
        };

        return Results.Ok(overview);
    }
}

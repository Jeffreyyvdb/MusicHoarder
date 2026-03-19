using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Enrichment;
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
        app.MapGet("/api/library/duplicates", GetDuplicates)
            .WithName("GetDuplicates")
            .WithSummary("List all tracks flagged as duplicates, grouped by fingerprint.")
            .WithTags("Library");
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

    private static async Task<IResult> GetDuplicates(MusicHoarderDbContext db)
    {
        var duplicates = await db.Songs
            .AsNoTracking()
            .Where(s => s.DeletedAtUtc == null && s.IsDuplicate)
            .OrderBy(s => s.Fingerprint)
            .ThenByDescending(s => s.FileSizeBytes)
            .Select(s => new
            {
                s.Id,
                s.SourcePath,
                s.FileName,
                s.Extension,
                s.FileSizeBytes,
                s.Artist,
                s.AlbumArtist,
                s.Album,
                s.Title,
                s.Year,
                s.TrackNumber,
                s.DurationSeconds,
                s.Bitrate,
                s.Fingerprint,
                s.IsDuplicate,
                s.DuplicateOfId,
                s.EnrichmentStatus,
                QualityScore = s.Extension != null
                    ? (s.Extension.ToLower() == ".flac" ? 1000 :
                       s.Extension.ToLower() == ".wav" ? 900 :
                       s.Extension.ToLower() == ".aiff" ? 900 :
                       s.Bitrate ?? 0)
                    : 0
            })
            .ToListAsync();

        var bestIds = duplicates.Select(d => d.DuplicateOfId).Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        var bestSongs = await db.Songs
            .AsNoTracking()
            .Where(s => bestIds.Contains(s.Id))
            .Select(s => new
            {
                s.Id,
                s.SourcePath,
                s.FileName,
                s.Extension,
                s.FileSizeBytes,
                s.Artist,
                s.Album,
                s.Title,
                s.Bitrate,
                s.Fingerprint,
                QualityScore = s.Extension != null
                    ? (s.Extension.ToLower() == ".flac" ? 1000 :
                       s.Extension.ToLower() == ".wav" ? 900 :
                       s.Extension.ToLower() == ".aiff" ? 900 :
                       s.Bitrate ?? 0)
                    : 0
            })
            .ToDictionaryAsync(s => s.Id);

        var groups = duplicates
            .GroupBy(d => d.Fingerprint)
            .Select(g =>
            {
                var bestId = g.First().DuplicateOfId;
                var best = bestId.HasValue && bestSongs.TryGetValue(bestId.Value, out var b)
                    ? (object)b
                    : null;
                return new
                {
                    Fingerprint = g.Key,
                    Best = best,
                    Duplicates = g.ToList()
                };
            })
            .ToList();

        return Results.Ok(new
        {
            TotalDuplicates = duplicates.Count,
            Groups = groups.Count,
            DuplicateGroups = groups
        });
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

        var totalCount = await active.CountAsync();
        var fingerprintedCount = await active.CountAsync(s =>
            s.Fingerprint != null && s.Fingerprint != string.Empty && s.DurationSeconds != null);
        var enrichedCount = await active.CountAsync(s =>
            s.EnrichmentStatus == EnrichmentStatus.Matched || s.EnrichmentStatus == EnrichmentStatus.NeedsReview);
        var buildEligibleCount = await active.CountAsync(s => s.EnrichmentStatus == EnrichmentStatus.Matched);
        var copiedCount = await active.CountAsync(s =>
            s.LibraryBuildStatus == LibraryBuildStatus.Copied ||
            s.LibraryBuildStatus == LibraryBuildStatus.Tagged ||
            s.LibraryBuildStatus == LibraryBuildStatus.Done);
        var reviewCount = await active.CountAsync(s => s.EnrichmentStatus == EnrichmentStatus.NeedsReview);
        var failedCount = await active.CountAsync(s =>
            s.EnrichmentStatus == EnrichmentStatus.Failed ||
            s.LibraryBuildStatus == LibraryBuildStatus.Failed);

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

        var recentSongs = await active
            .OrderByDescending(s => s.LibraryBuiltAtUtc ?? s.EnrichedAtUtc ?? s.EnrichmentLastAttemptedAtUtc ?? s.IndexedAtUtc)
            .Take(50)
            .Select(s => new
            {
                s.Id,
                s.FileName,
                s.Artist,
                s.IndexedAtUtc,
                s.EnrichedAtUtc,
                s.EnrichmentLastAttemptedAtUtc,
                s.LibraryBuiltAtUtc,
                s.LibraryBuildLastAttemptedAtUtc,
                s.EnrichmentStatus,
                s.LibraryBuildStatus,
            })
            .ToListAsync();

        var now = DateTime.UtcNow;
        var activities = recentSongs.Select(s =>
        {
            string type;
            DateTime activityAt;
            if (s.LibraryBuildStatus is LibraryBuildStatus.Copied or LibraryBuildStatus.Tagged or LibraryBuildStatus.Done
                && s.LibraryBuiltAtUtc.HasValue)
            {
                type = "copied";
                activityAt = s.LibraryBuiltAtUtc.Value;
            }
            else if (s.EnrichmentStatus == EnrichmentStatus.Failed || s.LibraryBuildStatus == LibraryBuildStatus.Failed)
            {
                type = "failed";
                activityAt = s.EnrichedAtUtc ?? s.LibraryBuildLastAttemptedAtUtc ?? s.IndexedAtUtc;
            }
            else if (s.EnrichmentStatus == EnrichmentStatus.NeedsReview)
            {
                type = "review";
                activityAt = s.EnrichedAtUtc ?? s.EnrichmentLastAttemptedAtUtc ?? s.IndexedAtUtc;
            }
            else if (s.EnrichmentStatus == EnrichmentStatus.Matched && s.EnrichedAtUtc.HasValue)
            {
                type = "enriched";
                activityAt = s.EnrichedAtUtc.Value;
            }
            else
            {
                type = "discovered";
                activityAt = s.IndexedAtUtc;
            }

            var diff = now - activityAt;
            var timeAgo = diff.TotalMinutes < 1 ? "just now"
                : diff.TotalMinutes < 60 ? $"{(int)diff.TotalMinutes} min ago"
                : diff.TotalHours < 24 ? $"{(int)diff.TotalHours} hr ago"
                : $"{(int)diff.TotalDays} day{(diff.TotalDays >= 2 ? "s" : "")} ago";

            return new
            {
                Id = $"act-{s.Id}",
                Type = type,
                Track = s.FileName ?? "Unknown",
                Artist = s.Artist ?? "Unknown",
                Time = timeAgo,
                ActivityAt = activityAt,
            };
        }).OrderByDescending(a => a.ActivityAt).ToList();

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
                TracksDiscovered = totalCount,
                TracksProcessed = totalCount,
                TracksFingerprinted = fingerprintedCount,
                TracksEnriched = enrichedCount,
                TracksBuildEligible = buildEligibleCount,
                TracksCopied = copiedCount,
                TracksReview = reviewCount,
                TracksFailed = failedCount,
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

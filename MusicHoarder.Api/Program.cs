using System.IO.Abstractions;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Scanner;
using MusicHoarder.ServiceDefaults;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services
    .AddOptions<MusicEnricherOptions>()
    .BindConfiguration(MusicEnricherOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.AddNpgsqlDbContext<MusicHoarderDbContext>(connectionName: "musichoarderdb");

builder.Services.AddSingleton<JobManager>();
builder.Services.AddSingleton<ScanProgressTracker>();
builder.Services.AddSingleton<FingerprintProgressTracker>();
builder.Services.AddSingleton<EnrichmentProgressTracker>();
builder.Services.AddSingleton<LibraryBuilderProgressTracker>();
builder.Services.AddSingleton<IFpcalcService, FpcalcService>();
builder.Services.AddSingleton<IAcoustIdMatchValidator, AcoustIdMatchValidator>();
builder.Services.AddSingleton<IEnrichmentOrchestrator, EnrichmentOrchestrator>();
builder.Services.AddSingleton<IDestinationPathResolver, DestinationPathResolver>();
builder.Services.AddSingleton<IDuplicateDetectionService, DuplicateDetectionService>();
builder.Services.AddScoped<ILibraryTagWriter, TagLibLibraryTagWriter>();
builder.Services.AddScoped<ILibraryBuilderService, LibraryBuilderService>();

builder.Services.AddHostedService<ScannerBackgroundService>();
builder.Services.AddHostedService<FingerprintBackgroundService>();
builder.Services.AddHostedService<EnrichmentBackgroundService>();
builder.Services.AddHostedService<LibraryBuilderBackgroundService>();

builder.Services.AddScoped<IFileSystem, FileSystem>();
builder.Services.AddScoped<IFileScanner, FileScanner>();
builder.Services.AddScoped<IIndexService, IndexService>();

builder.Services.AddSingleton<IAcoustIdService>(sp =>
{
    var httpClient = new HttpClient
    {
        BaseAddress = new Uri("https://api.acoustid.org/")
    };
    var options = sp.GetRequiredService<IOptions<MusicEnricherOptions>>();
    var logger = sp.GetRequiredService<ILogger<AcoustIdService>>();
    return new AcoustIdService(httpClient, options, logger);
});

builder.Services.AddSingleton<ILrcLibService>(sp =>
{
    var httpClient = new HttpClient
    {
        BaseAddress = new Uri("https://lrclib.net/"),
        DefaultRequestHeaders =
        {
            { "User-Agent", "MusicHoarder/1.0 (https://github.com/Jeffreyyvdb/MusicHoarder)" }
        }
    };
    var logger = sp.GetRequiredService<ILogger<LrcLibService>>();
    return new LrcLibService(httpClient, logger);
});

builder.Services.AddOpenApi();

var app = builder.Build();

var musicEnricherOptions = app.Services.GetRequiredService<IOptions<MusicEnricherOptions>>().Value;

// Shared JSON serializer options for SSE streaming — matches the API's camelCase convention.
var sseJsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

app.MapDefaultEndpoints();

// Apply pending EF Core migrations on startup in all environments.
// For single-instance homelab deployments this is safe and ensures the
// schema is always up to date after a container image update.
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
    await dbContext.Database.MigrateAsync();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.MapPost("/scan", (JobManager jobManager) =>
{
    if (!jobManager.TryStartJob(JobType.Scan, out var scanId, out _))
        return Results.Conflict(new { message = "A job is already running. Use POST /api/enrichment/cancel to stop it first." });

    return Results.Accepted($"/scan/{scanId}/progress", new { scanId });
})
.WithName("TriggerScan")
.WithSummary("Trigger a library scan (legacy endpoint, prefer /api/enrichment/scan).");

app.MapGet("/scan/{scanId}/progress", (Guid scanId, ScanProgressTracker tracker) =>
{
    var state = tracker.GetCurrent();
    if (state is null || state.ScanId != scanId)
        return Results.NotFound(new { message = "No scan found with that id." });

    return Results.Ok(state);
})
.WithName("GetScanProgress")
.WithSummary("Get a snapshot of scan progress by scan ID.");

// ── Enrichment controller endpoints ──────────────────────────────────────────

app.MapPost("/api/enrichment/scan", (JobManager jobManager) =>
{
    if (!jobManager.TryStartJob(JobType.Scan, out var jobId, out _))
        return Results.Conflict(new { message = "A job is already running. Use POST /api/enrichment/cancel to stop it first." });

    return Results.Accepted($"/api/enrichment/status", new { jobId });
})
.WithName("TriggerEnrichmentScan")
.WithSummary("Trigger the ScannerService to index the source library.")
.WithTags("Enrichment");

app.MapPost("/api/enrichment/enrich", (JobManager jobManager) =>
{
    if (!jobManager.TryStartJob(JobType.Enrich, out var jobId, out _))
        return Results.Conflict(new { message = "A job is already running. Use POST /api/enrichment/cancel to stop it first." });

    return Results.Accepted($"/api/enrichment/status", new { jobId });
})
.WithName("TriggerEnrich")
.WithSummary("Trigger the EnrichmentService to enrich pending tracks via AcoustID/MusicBrainz.")
.WithTags("Enrichment");

app.MapPost("/api/enrichment/fingerprint", (JobManager jobManager) =>
{
    if (!jobManager.TryStartJob(JobType.Fingerprint, out var jobId, out _))
        return Results.Conflict(new { message = "Fingerprint step is already running." });

    return Results.Accepted($"/api/enrichment/status", new { jobId });
})
.WithName("TriggerFingerprint")
.WithSummary("Trigger the FingerprintService to fingerprint tracks with missing fingerprints.")
.WithTags("Enrichment");

app.MapPost("/api/enrichment/build", (JobManager jobManager) =>
{
    if (!jobManager.TryStartJob(JobType.Build, out var jobId, out _))
        return Results.Conflict(new { message = "Build step is already running." });

    return Results.Accepted($"/api/enrichment/status", new { jobId });
})
.WithName("TriggerBuild")
.WithSummary("Trigger the LibraryBuilderService to copy and tag matched tracks to the destination.")
.WithTags("Enrichment");

app.MapPost("/api/enrichment/cancel", (JobManager jobManager) =>
{
    if (!jobManager.Cancel())
        return Results.Ok(new { message = "No job is currently running." });

    return Results.Ok(new { message = "Cancellation requested for the running job." });
})
.WithName("CancelJob")
.WithSummary("Cancel the currently running job.")
.WithTags("Enrichment");

app.MapPost("/api/enrichment/pause", (string step, JobManager jobManager) =>
{
    if (!TryParseJobType(step, out var jobType))
        return Results.BadRequest(new { message = $"Invalid step '{step}'. Use scan, fingerprint, enrich, or build." });

    jobManager.PauseStep(jobType);
    return Results.Ok(new { message = $"{step} paused.", step, paused = true });
})
.WithName("PauseStep")
.WithSummary("Pause a pipeline step. Cancels any in-flight job for that step and prevents auto-triggering.")
.WithTags("Enrichment");

app.MapPost("/api/enrichment/resume", (string step, JobManager jobManager) =>
{
    if (!TryParseJobType(step, out var jobType))
        return Results.BadRequest(new { message = $"Invalid step '{step}'. Use scan, fingerprint, enrich, or build." });

    jobManager.ResumeStep(jobType);
    return Results.Ok(new { message = $"{step} resumed.", step, paused = false });
})
.WithName("ResumeStep")
.WithSummary("Resume a paused pipeline step so it can auto-trigger again.")
.WithTags("Enrichment");

app.MapGet("/api/enrichment/status", (
    JobManager jobManager,
    ScanProgressTracker scanTracker,
    FingerprintProgressTracker fingerprintTracker,
    EnrichmentProgressTracker enrichmentTracker,
    LibraryBuilderProgressTracker buildTracker) =>
{
    var snapshot = BuildProgressSnapshot(jobManager, scanTracker, fingerprintTracker, enrichmentTracker, buildTracker);
    return Results.Ok(new { Progress = snapshot });
})
.WithName("GetEnrichmentStatus")
.WithSummary("Get the current job status and a progress snapshot.")
.WithTags("Enrichment");

app.MapGet("/api/enrichment/progress", (
    HttpContext context,
    JobManager jobManager,
    ScanProgressTracker scanTracker,
    FingerprintProgressTracker fingerprintTracker,
    EnrichmentProgressTracker enrichmentTracker,
    LibraryBuilderProgressTracker buildTracker) =>
{
    context.Response.Headers.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Connection = "keep-alive";
    context.Response.Headers.Append("X-Accel-Buffering", "no");

    var ct = context.RequestAborted;

    var connectTime = DateTime.UtcNow;

    async IAsyncEnumerable<string> StreamJson([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        bool wasRunning = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            var snapshot = BuildProgressSnapshot(jobManager, scanTracker, fingerprintTracker, enrichmentTracker, buildTracker);
            yield return JsonSerializer.Serialize(snapshot, sseJsonOptions);

            if (snapshot.IsComplete && wasRunning)
                yield break;

            if (!snapshot.IsComplete)
                wasRunning = true;

            await Task.Delay(1000, cancellationToken);
        }
    }

    return Results.ServerSentEvents(StreamJson(ct));
})
.WithName("StreamEnrichmentProgress")
.WithSummary("SSE stream that emits a ProgressSnapshot every second while a job is running.")
.WithTags("Enrichment");

app.MapPost("/api/enrichment/scrape-trackers", (JobManager jobManager) =>
{
    var result = Results.Json(new { message = "Tracker scraping is not yet implemented." }, statusCode: 501);
    return result;
})
.WithName("ScrapeTrackers")
.WithSummary("Trigger on-demand tracker scraping (not yet implemented).")
.WithTags("Enrichment");

// ── Library / Duplicates endpoints ────────────────────────────────────────────

app.MapGet("/api/library/duplicates", async (MusicHoarderDbContext db) =>
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
})
.WithName("GetDuplicates")
.WithSummary("List all tracks flagged as duplicates, grouped by fingerprint.")
.WithTags("Library");

app.MapGet("/stats", async (MusicHoarderDbContext db) =>
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
                WithFingerprint = enrichment.WithFingerprint,
                WithMusicBrainzId = enrichment.WithMusicBrainzId,
                WithSpotifyId = enrichment.WithSpotifyId,
                WithIsrc = enrichment.WithIsrc,
                WithArtist = enrichment.WithArtist,
                WithAlbum = enrichment.WithAlbum,
                WithTitle = enrichment.WithTitle,
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
});

app.MapGet("/overview", async (
    MusicHoarderDbContext db,
    IOptions<MusicEnricherOptions> options,
    ScanProgressTracker scanTracker,
    FingerprintProgressTracker fingerprintTracker,
    EnrichmentProgressTracker enrichmentTracker) =>
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
});

app.MapGet("/songs", async (MusicHoarderDbContext db, bool includeDeleted = false) =>
{
    var query = db.Songs.AsNoTracking();
    if (!includeDeleted)
    {
        query = query.Where(s => s.DeletedAtUtc == null);
    }

    var songs = await query
        .OrderBy(s => s.Artist ?? "")
        .ThenBy(s => s.Album ?? "")
        .ThenBy(s => s.TrackNumber ?? 0)
        .ThenBy(s => s.Title ?? "")
        .ThenBy(s => s.FileName)
        .Select(s => new
        {
            s.Id,
            s.SourcePath,
            s.FileName,
            s.Extension,
            s.FileSizeBytes,
            s.LastModifiedUtc,
            s.IndexedAtUtc,
            s.DeletedAtUtc,
            s.Artist,
            s.AlbumArtist,
            s.Album,
            s.Title,
            s.Year,
            s.TrackNumber,
            s.DurationSeconds,
            s.DurationMs,
            s.Isrc,
            s.MusicBrainzId,
            s.SpotifyId,
            s.EnrichmentStatus,
            s.MatchedBy,
            s.MatchConfidence,
            s.MatchWarnings,
            s.EnrichedAtUtc,
            s.EnrichmentError,
            s.OriginalMetadataCaptured,
            s.OriginalArtist,
            s.OriginalAlbumArtist,
            s.OriginalAlbum,
            s.OriginalTitle,
            s.OriginalYear,
            s.OriginalTrackNumber,
            s.OriginalIsrc,
            s.OriginalMusicBrainzId,
            s.OriginalSpotifyId,
            s.OriginalMetadataCapturedAtUtc,
            s.Bitrate,
            s.IsDuplicate,
            s.DuplicateOfId,
            s.LibraryBuildStatus,
            s.LibraryBuiltAtUtc,
            s.LibraryBuildLastAttemptedAtUtc,
            s.LibraryBuildError,
            s.DestinationPath,
            s.PreviousDestinationPath,
            s.LyricsStatus,
            s.SyncedLyrics,
            s.PlainLyrics,
            s.IsInstrumental,
        })
        .ToListAsync();

    var projected = songs.Select(s => new
    {
        s.Id, s.SourcePath, s.FileName, s.Extension, s.FileSizeBytes,
        s.LastModifiedUtc, s.IndexedAtUtc, s.DeletedAtUtc,
        s.Artist, s.AlbumArtist, s.Album, s.Title, s.Year, s.TrackNumber,
        s.DurationSeconds, s.DurationMs,
        s.Bitrate,
        s.Isrc, s.MusicBrainzId, s.SpotifyId,
        s.EnrichmentStatus, s.MatchedBy, s.MatchConfidence,
        MatchWarnings = DeserializeWarnings(s.MatchWarnings),
        s.EnrichedAtUtc, s.EnrichmentError,
        s.OriginalMetadataCaptured, s.OriginalArtist, s.OriginalAlbumArtist,
        s.OriginalAlbum, s.OriginalTitle, s.OriginalYear, s.OriginalTrackNumber,
        s.OriginalIsrc, s.OriginalMusicBrainzId, s.OriginalSpotifyId,
        s.OriginalMetadataCapturedAtUtc,
        s.IsDuplicate, s.DuplicateOfId,
        s.LibraryBuildStatus, s.LibraryBuiltAtUtc, s.LibraryBuildLastAttemptedAtUtc,
        s.LibraryBuildError, s.DestinationPath, s.PreviousDestinationPath,
        LyricsStatus = s.LyricsStatus.ToString(),
        HasSyncedLyrics = s.SyncedLyrics != null && s.SyncedLyrics != string.Empty,
        HasPlainLyrics = s.PlainLyrics != null && s.PlainLyrics != string.Empty,
        s.IsInstrumental
    }).ToList();

    return Results.Ok(new
    {
        Count = projected.Count,
        IncludeDeleted = includeDeleted,
        Songs = projected
    });
});

app.MapGet("/api/tracks/{id:int}/lyrics", async (int id, MusicHoarderDbContext db) =>
{
    var song = await db.Songs
        .AsNoTracking()
        .Where(s => s.Id == id && s.DeletedAtUtc == null)
        .Select(s => new
        {
            s.Id,
            s.LyricsStatus,
            s.SyncedLyrics,
            s.PlainLyrics,
            s.IsInstrumental,
        })
        .FirstOrDefaultAsync();

    if (song is null)
        return Results.NotFound(new { message = $"Track with id {id} not found." });

    return Results.Ok(new
    {
        song.Id,
        LyricsStatus = song.LyricsStatus.ToString(),
        song.IsInstrumental,
        Synced = song.SyncedLyrics,
        Plain = song.PlainLyrics,
    });
});

app.MapPost("/enrichment/reset", async (EnrichmentResetRequest request, MusicHoarderDbContext db) =>
{
    var target = request.Target?.Trim().ToLowerInvariant();

    IQueryable<SongMetadata> active = db.Songs.Where(s => s.DeletedAtUtc == null);
    IQueryable<SongMetadata>? query = target switch
    {
        "all" => active,
        "pending" => active.Where(s => s.EnrichmentStatus == EnrichmentStatus.Pending),
        "matched" => active.Where(s => s.EnrichmentStatus == EnrichmentStatus.Matched),
        "needsreview" => active.Where(s => s.EnrichmentStatus == EnrichmentStatus.NeedsReview),
        "failed" => active.Where(s => s.EnrichmentStatus == EnrichmentStatus.Failed),
        _ => null
    };
    if (query is null)
    {
        return Results.BadRequest(new { message = "Invalid target. Use all|pending|matched|needsReview|failed." });
    }

    var songs = await query.ToListAsync();
    foreach (var song in songs)
    {
        song.ResetEnrichment(request.RestoreOriginalMetadata);
    }

    await db.SaveChangesAsync();

    return Results.Ok(new
    {
        request.Target,
        request.RestoreOriginalMetadata,
        ResetCount = songs.Count
    });
});

app.MapPost("/songs/{id:int}/reset-enrichment", async (int id, MusicHoarderDbContext db, bool restoreOriginalMetadata = true) =>
{
    var song = await db.Songs.FirstOrDefaultAsync(s => s.Id == id);
    if (song is null)
        return Results.NotFound(new { message = $"Song with id {id} not found." });

    song.ResetEnrichment(restoreOriginalMetadata);
    song.ResetLibraryBuild();

    await db.SaveChangesAsync();

    return Results.Ok(new
    {
        song.Id,
        song.FileName,
        song.EnrichmentStatus,
        song.LibraryBuildStatus,
        RestoredOriginalMetadata = restoreOriginalMetadata && song.OriginalMetadataCaptured,
        Message = "Song enrichment has been reset. It will be re-enriched in the next enrichment cycle."
    });
});

app.MapGet("/songs/{id:int}/stream", async (int id, MusicHoarderDbContext db) =>
{
    var song = await db.Songs.AsNoTracking()
        .FirstOrDefaultAsync(s => s.Id == id && s.DeletedAtUtc == null);

    if (song is null)
        return Results.NotFound(new { message = $"Song with id {id} not found." });

    // Prefer source path; fall back to destination path so both library modes work
    // even when the source NAS share is temporarily unavailable.
    var filePath =
        (!string.IsNullOrEmpty(song.SourcePath)      && File.Exists(song.SourcePath))      ? song.SourcePath :
        (!string.IsNullOrEmpty(song.DestinationPath) && File.Exists(song.DestinationPath)) ? song.DestinationPath :
        null;

    if (filePath is null)
        return Results.NotFound(new
        {
            message = "Audio file not found on disk.",
            sourcePath = song.SourcePath,
            destinationPath = song.DestinationPath
        });

    var mimeType = Path.GetExtension(filePath)?.ToLowerInvariant() switch
    {
        ".mp3"  => "audio/mpeg",
        ".flac" => "audio/flac",
        ".ogg"  => "audio/ogg",
        ".opus" => "audio/opus",
        ".m4a"  => "audio/mp4",
        ".aac"  => "audio/aac",
        ".wav"  => "audio/wav",
        ".wma"  => "audio/x-ms-wma",
        _       => "application/octet-stream"
    };

    var stream = new FileStream(
        filePath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 65536,
        useAsync: true);

    return Results.Stream(stream, contentType: mimeType, enableRangeProcessing: true);
});

app.Run();

static string[]? DeserializeWarnings(string? json)
{
    if (string.IsNullOrWhiteSpace(json)) return null;
    try { return JsonSerializer.Deserialize<string[]>(json); }
    catch { return null; }
}

static ProgressSnapshot BuildProgressSnapshot(
    JobManager jobManager,
    ScanProgressTracker scanTracker,
    FingerprintProgressTracker fingerprintTracker,
    EnrichmentProgressTracker enrichmentTracker,
    LibraryBuilderProgressTracker buildTracker)
{
    var scanStep = jobManager.GetStepSnapshot(JobType.Scan);
    var fpStep = jobManager.GetStepSnapshot(JobType.Fingerprint);
    var enrichStep = jobManager.GetStepSnapshot(JobType.Enrich);
    var buildStep = jobManager.GetStepSnapshot(JobType.Build);

    var anyRunning = jobManager.IsAnyRunning();

    var runningLabels = new List<string>();
    if (scanStep.Status == "Running") runningLabels.Add("Scanning");
    if (fpStep.Status == "Running") runningLabels.Add("Fingerprinting");
    if (enrichStep.Status == "Running") runningLabels.Add("Enriching");
    if (buildStep.Status == "Running") runningLabels.Add("Building");

    var statusLabel = runningLabels.Count > 0
        ? string.Join(", ", runningLabels)
        : anyRunning ? "Running" : "Idle";

    var scanState = scanTracker.GetCurrent();
    var fpState = fingerprintTracker.GetCurrent();
    var enrichState = enrichmentTracker.GetCurrent();
    var buildState = buildTracker.GetCurrent();

    var discovered = scanState?.TotalFiles ?? 0;
    var scanned = (scanState?.Processed ?? 0) + (scanState?.SkippedFiles ?? 0);
    var fingerprinted = fpState?.Fingerprinted ?? 0;
    var enriched = enrichState?.Enriched ?? 0;
    var built = buildState?.Built ?? 0;
    var failed = (scanState?.FailedFiles ?? 0)
        + (fpState?.Failed ?? 0)
        + (enrichState?.Failed ?? 0)
        + (buildState?.Failed ?? 0);

    return new ProgressSnapshot(
        statusLabel,
        null,
        null,
        null,
        !anyRunning,
        discovered,
        scanned,
        fingerprinted,
        enriched,
        built,
        failed,
        scanStep,
        fpStep,
        enrichStep,
        buildStep);
}

static bool TryParseJobType(string step, out JobType jobType)
{
    jobType = step.Trim().ToLowerInvariant() switch
    {
        "scan" => JobType.Scan,
        "fingerprint" => JobType.Fingerprint,
        "enrich" => JobType.Enrich,
        "build" => JobType.Build,
        _ => JobType.None
    };
    return jobType != JobType.None;
}

public record EnrichmentResetRequest(
    string Target = "all",
    bool RestoreOriginalMetadata = true);
using System.IO.Abstractions;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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

builder.Services.AddSingleton(Channel.CreateUnbounded<ScanRequest>());
builder.Services.AddSingleton<ScanProgressTracker>();
builder.Services.AddSingleton<EnrichmentProgressTracker>();
builder.Services.AddSingleton<IFpcalcService, FpcalcService>();
builder.Services.AddSingleton<IAcoustIdMatchValidator, AcoustIdMatchValidator>();
builder.Services.AddSingleton<IEnrichmentOrchestrator, EnrichmentOrchestrator>();
builder.Services.AddSingleton<IDestinationPathResolver, DestinationPathResolver>();
builder.Services.AddScoped<ILibraryTagWriter, TagLibLibraryTagWriter>();
builder.Services.AddScoped<ILibraryBuilderService, LibraryBuilderService>();

builder.Services.AddHostedService<ScannerBackgroundService>();
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

builder.Services.AddOpenApi();

var app = builder.Build();

var musicEnricherOptions = app.Services.GetRequiredService<IOptions<MusicEnricherOptions>>().Value;

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();

    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
    await dbContext.Database.MigrateAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.MapPost("/scan", async (Channel<ScanRequest> channel) =>
{
    var scanId = Guid.NewGuid();
    await channel.Writer.WriteAsync(new ScanRequest(scanId));
    return Results.Accepted($"/scan/{scanId}/progress", new { scanId });
});

app.MapGet("/scan/{scanId}/progress", (Guid scanId, ScanProgressTracker tracker) =>
{
    var state = tracker.GetCurrent();
    if (state is null || state.ScanId != scanId)
        return Results.NotFound(new { message = "No scan found with that id." });

    return Results.Ok(state);
});

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
    EnrichmentProgressTracker enrichmentTracker) =>
{
    var opts = options.Value;
    var active = db.Songs.Where(s => s.DeletedAtUtc == null);

    var totalCount = await active.CountAsync();
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
            Status = scanRunning || enrichmentRunning ? "running" : "completed",
            StartedAt = startedAt,
            TracksDiscovered = totalCount,
            TracksProcessed = totalCount,
            TracksCopied = copiedCount,
            TracksReview = reviewCount,
            TracksFailed = failedCount,
        },
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
            s.LibraryBuildStatus,
            s.LibraryBuiltAtUtc,
            s.LibraryBuildLastAttemptedAtUtc,
            s.LibraryBuildError,
            s.DestinationPath,
            s.PreviousDestinationPath
        })
        .ToListAsync();

    var projected = songs.Select(s => new
    {
        s.Id, s.SourcePath, s.FileName, s.Extension, s.FileSizeBytes,
        s.LastModifiedUtc, s.IndexedAtUtc, s.DeletedAtUtc,
        s.Artist, s.AlbumArtist, s.Album, s.Title, s.Year, s.TrackNumber,
        s.DurationSeconds, s.DurationMs,
        s.Isrc, s.MusicBrainzId, s.SpotifyId,
        s.EnrichmentStatus, s.MatchedBy, s.MatchConfidence,
        MatchWarnings = DeserializeWarnings(s.MatchWarnings),
        s.EnrichedAtUtc, s.EnrichmentError,
        s.OriginalMetadataCaptured, s.OriginalArtist, s.OriginalAlbumArtist,
        s.OriginalAlbum, s.OriginalTitle, s.OriginalYear, s.OriginalTrackNumber,
        s.OriginalIsrc, s.OriginalMusicBrainzId, s.OriginalSpotifyId,
        s.OriginalMetadataCapturedAtUtc,
        s.LibraryBuildStatus, s.LibraryBuiltAtUtc, s.LibraryBuildLastAttemptedAtUtc,
        s.LibraryBuildError, s.DestinationPath, s.PreviousDestinationPath
    }).ToList();

    return Results.Ok(new
    {
        Count = projected.Count,
        IncludeDeleted = includeDeleted,
        Songs = projected
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
    try { return System.Text.Json.JsonSerializer.Deserialize<string[]>(json); }
    catch { return null; }
}

public record EnrichmentResetRequest(
    string Target = "all",
    bool RestoreOriginalMetadata = true);
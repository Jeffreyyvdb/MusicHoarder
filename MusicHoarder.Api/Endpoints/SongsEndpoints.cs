using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Contracts;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Endpoints;

public static class SongsEndpoints
{
    public static IEndpointRouteBuilder MapSongsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/songs", ListSongs).WithName("ListSongs");
        app.MapGet("/api/tracks/{id:int}/lyrics", GetTrackLyrics).WithName("GetTrackLyrics");
        app.MapPost("/enrichment/reset", ResetEnrichmentBatch).WithName("ResetEnrichmentBatch");
        app.MapPost("/songs/{id:int}/reset-enrichment", ResetSongEnrichment).WithName("ResetSongEnrichment");
        app.MapGet("/songs/{id:int}/stream", StreamSong).WithName("StreamSong");

        app.MapPatch("/songs/{id:int}/manual-review", ManualReviewTrack)
            .WithName("ManualReviewTrack")
            .WithSummary("Approve or reject a track that needs manual review.")
            .WithTags("Tracks");

        app.MapPost("/songs/bulk-approve", BulkApprove)
            .WithName("BulkApprove")
            .WithSummary("Approve all NeedsReview tracks with match confidence >= minConfidence (default 0.75).")
            .WithTags("Tracks");

        app.MapDelete("/songs/{id:int}", SoftDeleteSong)
            .WithName("SoftDeleteSong")
            .WithSummary("Soft-delete a song so it is excluded from review listings and library build.")
            .WithTags("Tracks");

        return app;
    }

    private static async Task<IResult> ListSongs(MusicHoarderDbContext db, bool includeDeleted = false, string? enrichmentStatus = null)
    {
        var query = db.Songs.AsNoTracking();
        if (!includeDeleted)
            query = query.Where(s => s.DeletedAtUtc == null);

        if (!string.IsNullOrWhiteSpace(enrichmentStatus) &&
            Enum.TryParse<EnrichmentStatus>(enrichmentStatus, ignoreCase: true, out var parsedStatus))
        {
            query = query.Where(s => s.EnrichmentStatus == parsedStatus);
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
            s.Isrc, s.MusicBrainzId, s.SpotifyId,
            s.EnrichmentStatus, s.MatchedBy, s.MatchConfidence,
            MatchWarnings = DeserializeWarnings(s.MatchWarnings),
            s.EnrichedAtUtc, s.EnrichmentError,
            s.OriginalMetadataCaptured, s.OriginalArtist, s.OriginalAlbumArtist,
            s.OriginalAlbum, s.OriginalTitle, s.OriginalYear, s.OriginalTrackNumber,
            s.OriginalIsrc, s.OriginalMusicBrainzId, s.OriginalSpotifyId,
            s.OriginalMetadataCapturedAtUtc,
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
    }

    private static async Task<IResult> GetTrackLyrics(int id, MusicHoarderDbContext db)
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
    }

    private static async Task<IResult> ResetEnrichmentBatch(EnrichmentResetRequest request, MusicHoarderDbContext db)
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
            return Results.BadRequest(new { message = "Invalid target. Use all|pending|matched|needsReview|failed." });

        var songs = await query.ToListAsync();
        foreach (var song in songs)
            song.ResetEnrichment(request.RestoreOriginalMetadata);

        await db.SaveChangesAsync();

        return Results.Ok(new
        {
            request.Target,
            request.RestoreOriginalMetadata,
            ResetCount = songs.Count
        });
    }

    private static async Task<IResult> ResetSongEnrichment(int id, MusicHoarderDbContext db, bool restoreOriginalMetadata = true)
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
    }

    private static async Task<IResult> StreamSong(int id, MusicHoarderDbContext db)
    {
        var song = await db.Songs.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id && s.DeletedAtUtc == null);

        if (song is null)
            return Results.NotFound(new { message = $"Song with id {id} not found." });

        var filePath =
            (!string.IsNullOrEmpty(song.SourcePath) && File.Exists(song.SourcePath)) ? song.SourcePath :
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
            ".mp3" => "audio/mpeg",
            ".flac" => "audio/flac",
            ".ogg" => "audio/ogg",
            ".opus" => "audio/opus",
            ".m4a" => "audio/mp4",
            ".aac" => "audio/aac",
            ".wav" => "audio/wav",
            ".wma" => "audio/x-ms-wma",
            _ => "application/octet-stream"
        };

        var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 65536,
            useAsync: true);

        return Results.Stream(stream, contentType: mimeType, enableRangeProcessing: true);
    }

    private static async Task<IResult> ManualReviewTrack(int id, ManualReviewRequest request, MusicHoarderDbContext db)
    {
        var song = await db.Songs.FirstOrDefaultAsync(s => s.Id == id);
        if (song is null)
            return Results.NotFound(new { message = $"Song with id {id} not found." });

        if (song.IsDeleted)
            return Results.UnprocessableEntity(new { message = "Cannot review a deleted song." });

        if (song.EnrichmentStatus != EnrichmentStatus.NeedsReview)
            return Results.UnprocessableEntity(new
            {
                message = $"Song is not in NeedsReview status (current: {song.EnrichmentStatus}).",
                currentStatus = song.EnrichmentStatus.ToString()
            });

        var decision = request.Decision?.Trim().ToLowerInvariant();
        if (decision is not ("approve" or "reject"))
            return Results.BadRequest(new { message = "Decision must be 'approve' or 'reject'." });

        if (decision == "approve")
        {
            if (request.Artist is not null) song.Artist = request.Artist;
            if (request.Album is not null) song.Album = request.Album;
            if (request.Title is not null) song.Title = request.Title;
            if (request.Year.HasValue) song.Year = request.Year.Value;
            if (request.AlbumArtist is not null) song.AlbumArtist = request.AlbumArtist;
            if (request.TrackNumber.HasValue) song.TrackNumber = request.TrackNumber.Value;

            song.EnrichmentStatus = EnrichmentStatus.Matched;
            song.EnrichmentError = null;
            song.ResetLibraryBuild();
        }
        else
        {
            song.EnrichmentStatus = EnrichmentStatus.NeedsReview;
            song.MatchedBy = null;
            song.MatchConfidence = null;
            song.MatchWarnings = null;
            song.EnrichmentError = string.IsNullOrWhiteSpace(request.RejectReason)
                ? "Manually rejected"
                : request.RejectReason;
        }

        await db.SaveChangesAsync();

        return Results.Ok(new
        {
            song.Id,
            song.FileName,
            Decision = decision,
            song.EnrichmentStatus,
            song.LibraryBuildStatus,
            song.Artist,
            song.Album,
            song.Title,
            song.Year,
        });
    }

    private static async Task<IResult> BulkApprove(BulkApproveRequest? request, MusicHoarderDbContext db)
    {
        var minConfidence = request?.MinConfidence ?? 0.75;

        var candidates = await db.Songs
            .Where(s => s.DeletedAtUtc == null
                && s.EnrichmentStatus == EnrichmentStatus.NeedsReview
                && s.MatchConfidence != null
                && s.MatchConfidence >= minConfidence)
            .ToListAsync();

        var approvedIds = new List<int>();
        foreach (var song in candidates)
        {
            song.EnrichmentStatus = EnrichmentStatus.Matched;
            song.EnrichmentError = null;
            song.ResetLibraryBuild();
            approvedIds.Add(song.Id);
        }

        await db.SaveChangesAsync();

        return Results.Ok(new
        {
            MinConfidence = minConfidence,
            ApprovedCount = approvedIds.Count,
            ApprovedIds = approvedIds,
        });
    }

    private static async Task<IResult> SoftDeleteSong(int id, MusicHoarderDbContext db)
    {
        var song = await db.Songs.FirstOrDefaultAsync(s => s.Id == id);
        if (song is null)
            return Results.NotFound(new { message = $"Song with id {id} not found." });

        if (song.IsDeleted)
            return Results.Ok(new { song.Id, song.FileName, message = "Song is already deleted.", song.DeletedAtUtc });

        song.SoftDelete();

        await db.SaveChangesAsync();

        return Results.Ok(new
        {
            song.Id,
            song.FileName,
            song.DeletedAtUtc,
            Message = "Song has been soft-deleted and will be excluded from review and library build."
        });
    }

    private static string[]? DeserializeWarnings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<string[]>(json); }
        catch { return null; }
    }
}

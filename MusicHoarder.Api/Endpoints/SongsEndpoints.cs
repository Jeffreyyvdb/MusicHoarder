using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Artwork;
using MusicHoarder.Api.Contracts;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Endpoints;

public static class SongsEndpoints
{
    public static IEndpointRouteBuilder MapSongsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/songs", ListSongs).WithName("ListSongs");
        app.MapGet("/songs/{id:int}/enrichment-detail", GetEnrichmentDetail)
            .WithName("GetEnrichmentDetail")
            .WithSummary("Dev: source vs current metadata + every provider attempt for one song.")
            .WithTags("Tracks");
        app.MapGet("/api/tracks/{id:int}/lyrics", GetTrackLyrics).WithName("GetTrackLyrics");
        app.MapPost("/songs/{id:int}/lyrics/transcribe", TranscribeLyrics)
            .WithName("TranscribeLyrics")
            .WithSummary("Experimental: transcribe a song's audio via OpenAI Whisper into a synced LRC, stored separately from LRCLIB lyrics for comparison.")
            .WithTags("Lyrics");
        app.MapPost("/songs/{id:int}/lyrics/preferred", SetPreferredLyrics)
            .WithName("SetPreferredLyrics")
            .WithSummary("Choose which lyrics (lrclib | transcribed) the synced viewer shows when both exist.")
            .WithTags("Lyrics");
        app.MapPost("/enrichment/reset", ResetEnrichmentBatch).WithName("ResetEnrichmentBatch");
        app.MapPost("/songs/{id:int}/reset-enrichment", ResetSongEnrichment).WithName("ResetSongEnrichment");
        app.MapPost("/songs/{id:int}/unlock", UnlockSong)
            .WithName("UnlockSong")
            .WithSummary("Clear a song's manual-approval lock so the pipeline can re-enrich it.")
            .WithTags("Tracks");
        app.MapPost("/songs/{id:int}/changes/{changeId:int}/revert", RevertMetadataChange)
            .WithName("RevertMetadataChange")
            .WithSummary("Revert a previously-applied automatic metadata change to its old value.")
            .WithTags("Tracks");
        app.MapGet("/songs/{id:int}/stream", StreamSong).WithName("StreamSong");
        app.MapGet("/songs/{id:int}/cover", GetSongCover)
            .WithName("GetSongCover")
            .WithSummary("Serve the track's album artwork (embedded picture or a cover/folder/front.* image in its directory).")
            .WithTags("Tracks");

        app.MapGet("/api/library/duplicates", ListDuplicates)
            .WithName("GetDuplicates")
            .WithSummary("List all tracks flagged as duplicates, grouped by fingerprint.")
            .WithTags("Library");

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

    internal static async Task<IResult> ListSongs(MusicHoarderDbContext db, bool includeDeleted = false, string? enrichmentStatus = null)
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
                s.Artists,
                s.AlbumArtist,
                s.Album,
                s.Title,
                s.Year,
                s.TrackNumber,
                s.DurationSeconds,
                s.DurationMs,
                s.Bitrate,
                s.HasCoverArt,
                s.Fingerprint,
                s.Isrc,
                s.MusicBrainzId,
                s.MusicBrainzReleaseId,
                s.SpotifyId,
                s.AcoustIdTrackId,
                s.LrclibId,
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
                // Lightweight transcription flags only — the (potentially large) AI lyric text is fetched
                // on demand via GetTrackLyrics, not shipped with every row in the songs list.
                HasTranscribedLyrics =
                    (s.TranscribedSyncedLyrics != null && s.TranscribedSyncedLyrics != string.Empty)
                    || (s.TranscribedPlainLyrics != null && s.TranscribedPlainLyrics != string.Empty),
                s.TranscriptionStatus,
                s.TranscribedAtUtc,
                s.TranscriptionModel,
                s.PreferredLyricsSource,
            })
            .ToListAsync();

        var projected = songs.Select(s => new
        {
            s.Id, s.SourcePath, s.FileName, s.Extension, s.FileSizeBytes,
            s.LastModifiedUtc, s.IndexedAtUtc, s.DeletedAtUtc,
            s.Artist, s.Artists, s.AlbumArtist, s.Album, s.Title, s.Year, s.TrackNumber,
            s.DurationSeconds, s.DurationMs,
            s.Bitrate,
            s.HasCoverArt,
            s.Fingerprint,
                s.Isrc, s.MusicBrainzId, s.MusicBrainzReleaseId, s.SpotifyId, s.AcoustIdTrackId, s.LrclibId,
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
            s.IsInstrumental,
            s.HasTranscribedLyrics,
            TranscriptionStatus = s.TranscriptionStatus.ToString(),
            s.TranscribedAtUtc,
            s.TranscriptionModel,
            PreferredLyricsSource = s.PreferredLyricsSource.ToString()
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
                s.TranscribedSyncedLyrics,
                s.TranscribedPlainLyrics,
                s.TranscriptionStatus,
                s.TranscribedAtUtc,
                s.TranscriptionModel,
                s.PreferredLyricsSource,
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
            TranscribedSynced = song.TranscribedSyncedLyrics,
            TranscribedPlain = song.TranscribedPlainLyrics,
            TranscriptionStatus = song.TranscriptionStatus.ToString(),
            song.TranscribedAtUtc,
            song.TranscriptionModel,
            PreferredLyricsSource = song.PreferredLyricsSource.ToString(),
        });
    }

    private static async Task<IResult> TranscribeLyrics(
        int id,
        MusicHoarderDbContext db,
        ILyricsTranscriptionService transcriber,
        CancellationToken ct)
    {
        if (!transcriber.IsConfigured)
            return Results.Json(
                new { message = "Lyrics transcription is not configured. Set LyricsTranscription:ApiKey (and optionally BaseUrl/Model)." },
                statusCode: StatusCodes.Status503ServiceUnavailable);

        var song = await db.Songs.FirstOrDefaultAsync(s => s.Id == id && s.DeletedAtUtc == null, ct);
        if (song is null)
            return Results.NotFound(new { message = $"Song with id {id} not found." });

        if (song.IsSynthetic)
            return Results.UnprocessableEntity(new { message = "Demo songs have no audio file on disk to transcribe." });

        if (song.IsInstrumental == true)
            return Results.UnprocessableEntity(new { message = "Track is marked instrumental — nothing to transcribe." });

        // Prefer the read-only source original; fall back to the built destination copy (mirrors StreamSong).
        var filePath =
            (!string.IsNullOrEmpty(song.SourcePath) && File.Exists(song.SourcePath)) ? song.SourcePath :
            (!string.IsNullOrEmpty(song.DestinationPath) && File.Exists(song.DestinationPath)) ? song.DestinationPath :
            null;

        if (filePath is null)
            return Results.UnprocessableEntity(new
            {
                message = "Audio file not found on disk.",
                sourcePath = song.SourcePath,
                destinationPath = song.DestinationPath,
            });

        try
        {
            var result = await transcriber.TranscribeAsync(song, filePath, ct);
            // Stored separately from SyncedLyrics/PlainLyrics so it never clobbers the LRCLIB version.
            song.ApplyTranscriptionResult(result.SyncedLyrics, result.PlainLyrics, result.Model);

            // If the AI version is this song's chosen default, the file's effective lyrics just changed —
            // re-tag the built destination so it reflects the fresh transcription.
            if (song.PreferredLyricsSource == PreferredLyricsSource.Transcribed
                && song.LibraryBuildStatus == LibraryBuildStatus.Done)
            {
                song.RequeueForRetag();
            }
            await db.SaveChangesAsync(ct);

            return Results.Ok(new
            {
                song.Id,
                Synced = song.TranscribedSyncedLyrics,
                Plain = song.TranscribedPlainLyrics,
                TranscriptionStatus = song.TranscriptionStatus.ToString(),
                song.TranscribedAtUtc,
                Model = song.TranscriptionModel,
                HasExistingLyrics = song.LyricsStatus == LyricsStatus.Fetched,
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            song.MarkTranscriptionFailed(ex.Message);
            await db.SaveChangesAsync(CancellationToken.None);
            return Results.Json(
                new { message = "Transcription failed.", error = ex.Message },
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> SetPreferredLyrics(int id, string? source, MusicHoarderDbContext db, CancellationToken ct)
    {
        PreferredLyricsSource? parsed = source?.Trim().ToLowerInvariant() switch
        {
            "lrclib" => PreferredLyricsSource.Lrclib,
            "transcribed" => PreferredLyricsSource.Transcribed,
            _ => null,
        };
        if (parsed is null)
            return Results.BadRequest(new { message = "source must be 'lrclib' or 'transcribed'." });

        var song = await db.Songs.FirstOrDefaultAsync(s => s.Id == id && s.DeletedAtUtc == null, ct);
        if (song is null)
            return Results.NotFound(new { message = $"Song with id {id} not found." });

        var changed = song.PreferredLyricsSource != parsed.Value;
        song.PreferredLyricsSource = parsed.Value;

        // Promote the choice into the file too: re-tag the built destination so external players
        // (Navidrome, etc.) embed the chosen lyrics. Only when the choice actually changed.
        var retagQueued = false;
        if (changed && song.LibraryBuildStatus == LibraryBuildStatus.Done)
        {
            song.RequeueForRetag();
            retagQueued = true;
        }
        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            song.Id,
            PreferredLyricsSource = song.PreferredLyricsSource.ToString(),
            retagQueued,
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

    private static async Task<IResult> ResetSongEnrichment(int id, MusicHoarderDbContext db, bool restoreOriginalMetadata = true, bool force = false)
    {
        var song = await db.Songs.FirstOrDefaultAsync(s => s.Id == id);
        if (song is null)
            return Results.NotFound(new { message = $"Song with id {id} not found." });

        if (song.IsManuallyApproved && !force)
            return Results.UnprocessableEntity(new
            {
                message = "Song is locked (manually approved). Pass force=true (or unlock it first) to reset.",
                song.Id,
                song.IsManuallyApproved,
            });

        song.ResetEnrichment(restoreOriginalMetadata, force);
        song.ResetLibraryBuild();

        await db.SaveChangesAsync();

        return Results.Ok(new
        {
            song.Id,
            song.FileName,
            song.EnrichmentStatus,
            song.LibraryBuildStatus,
            song.IsManuallyApproved,
            RestoredOriginalMetadata = restoreOriginalMetadata && song.OriginalMetadataCaptured,
            Message = "Song enrichment has been reset. It will be re-enriched in the next enrichment cycle."
        });
    }

    private static async Task<IResult> UnlockSong(int id, MusicHoarderDbContext db)
    {
        var song = await db.Songs.FirstOrDefaultAsync(s => s.Id == id);
        if (song is null)
            return Results.NotFound(new { message = $"Song with id {id} not found." });

        song.UnlockManualApproval();
        await db.SaveChangesAsync();

        return Results.Ok(new { song.Id, song.FileName, song.IsManuallyApproved });
    }

    private static async Task<IResult> RevertMetadataChange(int id, int changeId, MusicHoarderDbContext db)
    {
        var song = await db.Songs.FirstOrDefaultAsync(s => s.Id == id);
        if (song is null)
            return Results.NotFound(new { message = $"Song with id {id} not found." });

        var change = await db.SongMetadataChanges.FirstOrDefaultAsync(c => c.Id == changeId && c.SongId == id);
        if (change is null)
            return Results.NotFound(new { message = $"Change {changeId} not found for song {id}." });

        if (change.AppliedAtUtc is null || change.RevertedAtUtc is not null)
            return Results.UnprocessableEntity(new { message = "Only an applied, not-yet-reverted change can be reverted." });

        ApplyFieldValue(song, change.FieldName, change.OldValue);
        change.RevertedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return Results.Ok(new { song.Id, change.FieldName, revertedTo = change.OldValue });
    }

    private static void ApplyFieldValue(SongMetadata song, string field, string? value)
    {
        switch (field)
        {
            case "Artist": song.Artist = value; break;
            case "AlbumArtist": song.AlbumArtist = value; break;
            case "Title": song.Title = value; break;
            case "Album": song.Album = value; break;
            case "Year": song.Year = int.TryParse(value, out var y) ? y : null; break;
            case "TrackNumber": song.TrackNumber = int.TryParse(value, out var t) ? t : null; break;
        }
    }

    internal static async Task<IResult> StreamSong(int id, MusicHoarderDbContext db)
    {
        var song = await db.Songs.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id && s.DeletedAtUtc == null);

        if (song is null)
            return Results.NotFound(new { message = $"Song with id {id} not found." });

        return StreamSongFile(song);
    }

    /// <summary>Prefers the source file, falls back to the built destination copy.</summary>
    internal static string? ResolveAudioFilePath(SongMetadata song) =>
        (!string.IsNullOrEmpty(song.SourcePath) && File.Exists(song.SourcePath)) ? song.SourcePath :
        (!string.IsNullOrEmpty(song.DestinationPath) && File.Exists(song.DestinationPath)) ? song.DestinationPath :
        null;

    /// <summary>
    /// Range-enabled audio stream for a song row the caller has already loaded and authorized
    /// (also used by the anonymous share endpoints, which do their own token-based scoping).
    /// </summary>
    internal static IResult StreamSongFile(SongMetadata song)
    {
        var filePath = ResolveAudioFilePath(song);

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

    internal static async Task<IResult> GetSongCover(
        int id,
        MusicHoarderDbContext db,
        ICoverArtResolver coverArtResolver,
        ICoverThumbnailService thumbnails,
        HttpContext http,
        int? size)
    {
        var song = await db.Songs.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id && s.DeletedAtUtc == null);

        // Synthetic (demo) rows have no real file on disk — nothing to resolve.
        if (song is null || song.IsSynthetic)
            return Results.NotFound();

        return await ServeCoverAsync(song, coverArtResolver, thumbnails, http, size);
    }

    /// <summary>
    /// Resolve + serve a song's cover (thumbnailed when <paramref name="size"/> is set) for a row
    /// the caller has already authorized (also used by the anonymous share endpoints).
    /// </summary>
    internal static async Task<IResult> ServeCoverAsync(
        SongMetadata song,
        ICoverArtResolver coverArtResolver,
        ICoverThumbnailService thumbnails,
        HttpContext http,
        int? size)
    {
        var filePath = ResolveAudioFilePath(song);

        if (filePath is null)
            return Results.NotFound();

        var cover = coverArtResolver.Resolve(filePath);

        // An artless source still gets a destination cover from the external fetch — serve that
        // rather than 404ing just because the (preferred) source file resolved nothing.
        if (cover is null
            && filePath == song.SourcePath
            && !string.IsNullOrEmpty(song.DestinationPath)
            && File.Exists(song.DestinationPath))
        {
            cover = coverArtResolver.Resolve(song.DestinationPath);
        }

        if (cover is null)
            return Results.NotFound();

        // A `?size=` request gets a small cached WebP thumbnail instead of the multi-MB original —
        // this is what the album grid uses so scrolling doesn't download full-resolution art.
        if (size is int requested && requested > 0)
        {
            var identity = cover.FilePath ?? filePath;
            var thumb = await thumbnails.GetThumbnailAsync(cover, identity, requested, http.RequestAborted);
            if (thumb?.FilePath is not null)
            {
                http.Response.Headers.CacheControl = "private, max-age=604800";
                return Results.File(thumb.FilePath, contentType: thumb.ContentType);
            }
            // Thumbnailing failed (corrupt image) — fall through to the original below.
        }

        // Covers rarely change; let the browser cache them. Private because they're served through
        // the per-user authenticated proxy.
        http.Response.Headers.CacheControl = "private, max-age=86400";

        return cover.FilePath is not null
            ? Results.File(cover.FilePath, contentType: cover.ContentType)
            : Results.Bytes(cover.Bytes!, contentType: cover.ContentType);
    }

    private static async Task<IResult> ListDuplicates(MusicHoarderDbContext db)
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

        var bestIds = duplicates
            .Select(d => d.DuplicateOfId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

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
            song.LockManualApproval();
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
            .Include(s => s.ProviderAttempts)
            .Where(s => s.DeletedAtUtc == null
                && s.EnrichmentStatus == EnrichmentStatus.NeedsReview
                && s.MatchConfidence != null
                && s.MatchConfidence >= minConfidence)
            .ToListAsync();

        var approvedIds = new List<int>();
        var skippedIds = new List<int>();
        foreach (var song in candidates)
        {
            // The orchestrator no longer writes the candidate's metadata onto the song row
            // when a provider returns NeedsReview, so bulk-approve must apply the winning
            // candidate from the provider attempt's MatchedDataJson before flipping to Matched.
            // Skip rows where the recorded MatchedBy provider has no candidate JSON we can apply.
            if (!WinningCandidateApplier.TryApply(song))
            {
                skippedIds.Add(song.Id);
                continue;
            }

            song.EnrichmentStatus = EnrichmentStatus.Matched;
            song.EnrichmentError = null;
            song.LockManualApproval();
            song.ResetLibraryBuild();
            approvedIds.Add(song.Id);
        }

        await db.SaveChangesAsync();

        return Results.Ok(new
        {
            MinConfidence = minConfidence,
            ApprovedCount = approvedIds.Count,
            ApprovedIds = approvedIds,
            SkippedCount = skippedIds.Count,
            SkippedIds = skippedIds,
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

    private static async Task<IResult> GetEnrichmentDetail(int id, MusicHoarderDbContext db)
    {
        var song = await db.Songs
            .AsNoTracking()
            .Include(s => s.ProviderAttempts)
            .FirstOrDefaultAsync(s => s.Id == id && s.DeletedAtUtc == null);

        if (song is null)
            return Results.NotFound(new { message = $"Song with id {id} not found." });

        // "Embedded" tags = the file's original tags. Once an enrichment match is applied we snapshot
        // them into Original*; before that the live row still holds the untouched embedded tags. When
        // nothing was captured, fall back to the current row so the review UI's EMBEDDED column shows
        // the same embedded values the AI grading dossier does (see QualityDossierFactory) instead of a
        // blank column. The separate `originalMetadataCaptured` flag still distinguishes the two cases.
        var captured = song.OriginalMetadataCaptured;
        var source = new
        {
            capturedAtUtc = captured ? song.OriginalMetadataCapturedAtUtc : null,
            title = captured ? song.OriginalTitle : song.Title,
            artist = captured ? song.OriginalArtist : song.Artist,
            albumArtist = captured ? song.OriginalAlbumArtist : song.AlbumArtist,
            album = captured ? song.OriginalAlbum : song.Album,
            year = captured ? song.OriginalYear : song.Year,
            trackNumber = captured ? song.OriginalTrackNumber : song.TrackNumber,
            isrc = captured ? song.OriginalIsrc : song.Isrc,
            musicBrainzId = captured ? song.OriginalMusicBrainzId : song.MusicBrainzId,
            spotifyId = captured ? song.OriginalSpotifyId : song.SpotifyId,
        };

        var current = new
        {
            title = song.Title,
            artist = song.Artist,
            albumArtist = song.AlbumArtist,
            album = song.Album,
            year = song.Year,
            trackNumber = song.TrackNumber,
            isrc = song.Isrc,
            musicBrainzId = song.MusicBrainzId,
            musicBrainzReleaseId = song.MusicBrainzReleaseId,
            spotifyId = song.SpotifyId,
            acoustIdTrackId = song.AcoustIdTrackId,
        };

        var diff = song.OriginalMetadataCaptured ? BuildMetadataDiff(song) : new List<object>();

        var providerAttempts = song.ProviderAttempts
            .OrderBy(a => a.Provider)
            .Select(a => new
            {
                provider = a.Provider.ToString(),
                status = a.Status.ToString(),
                attemptedAtUtc = a.AttemptedAtUtc,
                retryAfterUtc = a.RetryAfterUtc,
                nextRetryAfterUtc = a.NextRetryAfterUtc,
                error = a.Error,
                searchQuery = a.SearchQuery,
                candidate = DeserializeCandidate(a.MatchedDataJson),
            })
            .ToList();

        // Field-level change history: applied changes (undoable) and proposed changes (pending review).
        var changeLog = await db.SongMetadataChanges
            .AsNoTracking()
            .Where(c => c.SongId == id)
            .OrderByDescending(c => c.CreatedAtUtc)
            .Select(c => new
            {
                c.Id,
                field = c.FieldName,
                oldValue = c.OldValue,
                newValue = c.NewValue,
                source = c.Source,
                confidence = c.Confidence,
                createdAtUtc = c.CreatedAtUtc,
                appliedAtUtc = c.AppliedAtUtc,
                revertedAtUtc = c.RevertedAtUtc,
                applied = c.AppliedAtUtc != null && c.RevertedAtUtc == null,
                proposed = c.AppliedAtUtc == null && c.RevertedAtUtc == null,
            })
            .ToListAsync();

        return Results.Ok(new
        {
            id = song.Id,
            sourcePath = song.SourcePath,
            fileName = song.FileName,
            destinationPath = song.DestinationPath,
            enrichmentStatus = song.EnrichmentStatus.ToString(),
            isManuallyApproved = song.IsManuallyApproved,
            manuallyApprovedAtUtc = song.ManuallyApprovedAtUtc,
            matchedBy = song.MatchedBy,
            matchConfidence = song.MatchConfidence,
            matchWarnings = DeserializeWarnings(song.MatchWarnings),
            enrichmentError = song.EnrichmentError,
            originalMetadataCaptured = song.OriginalMetadataCaptured,
            source,
            current,
            diff,
            providerAttempts,
            changeLog,
            trackSync = await GetTrackSyncInfoAsync(db, id),
            upgrade = await GetLatestUpgradeInfoAsync(db, id),
        });
    }

    /// <summary>Push-side sync outbox state, folded into the detail so the UI needs no extra call.
    /// Null when this instance never synced the track (e.g. sync off / receive-side).</summary>
    private static async Task<object?> GetTrackSyncInfoAsync(MusicHoarderDbContext db, int songId)
    {
        return await db.TrackSyncStates
            .AsNoTracking()
            .Where(t => t.SongId == songId)
            .Select(t => new
            {
                status = t.Status.ToString(),
                t.Attempts,
                t.LastError,
                t.RemoteQualityScore,
                t.UpdatedAtUtc,
            })
            .FirstOrDefaultAsync();
    }

    /// <summary>Latest Soulseek upgrade request for the track, newest first. Null when none exist.</summary>
    private static async Task<object?> GetLatestUpgradeInfoAsync(MusicHoarderDbContext db, int songId)
    {
        return await db.UpgradeRequests
            .AsNoTracking()
            .Where(r => r.SongId == songId)
            .OrderByDescending(r => r.Id)
            .Select(r => new
            {
                r.Id,
                status = r.Status.ToString(),
                active = r.Status == UpgradeRequestStatus.Queued
                    || r.Status == UpgradeRequestStatus.Searching
                    || r.Status == UpgradeRequestStatus.Downloading
                    || r.Status == UpgradeRequestStatus.AwaitingIngest,
                r.CandidateInfoJson,
                r.Error,
                r.UpdatedAtUtc,
            })
            .FirstOrDefaultAsync();
    }

    internal static List<object> BuildMetadataDiff(SongMetadata s)
    {
        var diff = new List<object>();
        AddIfChanged(diff, "title", s.OriginalTitle, s.Title);
        AddIfChanged(diff, "artist", s.OriginalArtist, s.Artist);
        AddIfChanged(diff, "albumArtist", s.OriginalAlbumArtist, s.AlbumArtist);
        AddIfChanged(diff, "album", s.OriginalAlbum, s.Album);
        AddIfChangedInt(diff, "year", s.OriginalYear, s.Year);
        AddIfChangedInt(diff, "trackNumber", s.OriginalTrackNumber, s.TrackNumber);
        AddIfChanged(diff, "isrc", s.OriginalIsrc, s.Isrc);
        AddIfChanged(diff, "musicBrainzId", s.OriginalMusicBrainzId, s.MusicBrainzId);
        AddIfChanged(diff, "spotifyId", s.OriginalSpotifyId, s.SpotifyId);
        return diff;
    }

    private static void AddIfChanged(List<object> diff, string field, string? src, string? cur)
    {
        var srcN = string.IsNullOrWhiteSpace(src) ? null : src.Trim();
        var curN = string.IsNullOrWhiteSpace(cur) ? null : cur.Trim();
        if (!string.Equals(srcN, curN, StringComparison.Ordinal))
            diff.Add(new { field, source = (object?)src, current = (object?)cur });
    }

    private static void AddIfChangedInt(List<object> diff, string field, int? src, int? cur)
    {
        if (src != cur)
            diff.Add(new { field, source = (object?)src, current = (object?)cur });
    }

    internal static object? DeserializeCandidate(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var r = JsonSerializer.Deserialize<EnrichmentProviderResult>(json);
            if (r is null) return null;
            return new
            {
                title = r.Title,
                artist = r.Artist,
                albumArtist = r.AlbumArtist,
                album = r.Album,
                year = r.Year,
                trackNumber = r.TrackNumber,
                isrc = r.Isrc,
                musicBrainzId = r.MusicBrainzId,
                musicBrainzReleaseId = r.MusicBrainzReleaseId,
                spotifyId = r.SpotifyId,
                acoustIdTrackId = r.AcoustIdTrackId,
                matchedBy = r.MatchedBy,
                matchConfidence = r.MatchConfidence,
                matchWarnings = r.MatchWarnings,
                recommendedStatus = r.RecommendedStatus.ToString(),
            };
        }
        catch
        {
            return null;
        }
    }

    private static string[]? DeserializeWarnings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<string[]>(json); }
        catch { return null; }
    }
}

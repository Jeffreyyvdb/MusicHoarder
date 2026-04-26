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
        app.MapGet("/songs/{id:int}/enrichment-detail", GetEnrichmentDetail)
            .WithName("GetEnrichmentDetail")
            .WithSummary("Dev: source vs current metadata + every provider attempt for one song.")
            .WithTags("Tracks");
        app.MapGet("/api/tracks/{id:int}/lyrics", GetTrackLyrics).WithName("GetTrackLyrics");
        app.MapPost("/enrichment/reset", ResetEnrichmentBatch).WithName("ResetEnrichmentBatch");
        app.MapPost("/songs/{id:int}/reset-enrichment", ResetSongEnrichment).WithName("ResetSongEnrichment");
        app.MapGet("/songs/{id:int}/stream", StreamSong).WithName("StreamSong");

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
                s.Bitrate,
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
            })
            .ToListAsync();

        var projected = songs.Select(s => new
        {
            s.Id, s.SourcePath, s.FileName, s.Extension, s.FileSizeBytes,
            s.LastModifiedUtc, s.IndexedAtUtc, s.DeletedAtUtc,
            s.Artist, s.AlbumArtist, s.Album, s.Title, s.Year, s.TrackNumber,
            s.DurationSeconds, s.DurationMs,
            s.Bitrate,
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
            if (!TryApplyWinningCandidate(song))
            {
                skippedIds.Add(song.Id);
                continue;
            }

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
            SkippedCount = skippedIds.Count,
            SkippedIds = skippedIds,
        });
    }

    internal static bool TryApplyWinningCandidate(Persistence.SongMetadata song)
    {
        if (string.IsNullOrWhiteSpace(song.MatchedBy)) return false;

        var providerEnum = EnrichmentOrchestrator.MapProviderName(song.MatchedBy);
        if (providerEnum is null) return false;

        var attempt = song.ProviderAttempts.FirstOrDefault(a =>
            a.Provider == providerEnum.Value && a.Status == ProviderAttemptStatus.Matched);
        if (attempt is null || string.IsNullOrWhiteSpace(attempt.MatchedDataJson)) return false;

        EnrichmentProviderResult? candidate;
        try
        {
            candidate = JsonSerializer.Deserialize<EnrichmentProviderResult>(attempt.MatchedDataJson);
        }
        catch (JsonException)
        {
            return false;
        }
        if (candidate is null) return false;

        var warningsJson = candidate.MatchWarnings.Count > 0
            ? JsonSerializer.Serialize(candidate.MatchWarnings)
            : null;

        song.ApplyEnrichmentMatch(new EnrichmentMatchData(
            candidate.Artist,
            candidate.AlbumArtist,
            candidate.Title,
            candidate.Year,
            candidate.TrackNumber,
            candidate.MusicBrainzId,
            candidate.MusicBrainzReleaseId,
            candidate.SpotifyId,
            candidate.AcoustIdTrackId,
            candidate.Isrc,
            candidate.MatchedBy,
            candidate.MatchConfidence,
            warningsJson,
            EnrichmentStatus.Matched,
            candidate.Album));
        return true;
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

        object? source = song.OriginalMetadataCaptured
            ? new
            {
                capturedAtUtc = song.OriginalMetadataCapturedAtUtc,
                title = song.OriginalTitle,
                artist = song.OriginalArtist,
                albumArtist = song.OriginalAlbumArtist,
                album = song.OriginalAlbum,
                year = song.OriginalYear,
                trackNumber = song.OriginalTrackNumber,
                isrc = song.OriginalIsrc,
                musicBrainzId = song.OriginalMusicBrainzId,
                spotifyId = song.OriginalSpotifyId,
            }
            : null;

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
                error = a.Error,
                candidate = DeserializeCandidate(a.MatchedDataJson),
            })
            .ToList();

        return Results.Ok(new
        {
            id = song.Id,
            sourcePath = song.SourcePath,
            fileName = song.FileName,
            destinationPath = song.DestinationPath,
            enrichmentStatus = song.EnrichmentStatus.ToString(),
            matchedBy = song.MatchedBy,
            matchConfidence = song.MatchConfidence,
            matchWarnings = DeserializeWarnings(song.MatchWarnings),
            enrichmentError = song.EnrichmentError,
            originalMetadataCaptured = song.OriginalMetadataCaptured,
            source,
            current,
            diff,
            providerAttempts,
        });
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

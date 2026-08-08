using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Audio;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Logging;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Sync;

/// <summary>
/// Receive-side ingest. Trusts the pushing instance's enrichment (the payload IS the consensus):
/// new tracks become rows created directly as Matched + manually-approved (so this instance's own
/// enrichment sweeps never second-guess the pushed identity), and better-quality uploads replace an
/// existing row's source file IN PLACE — same int Id, so stream URLs and anything else addressing
/// the track survive the swap.
/// <para>
/// Matching ladder (most to least precise): exact Chromaprint fingerprint → AcoustID track id
/// (portable across encodings, unlike the raw fingerprint) → MusicBrainz recording id → normalized
/// artist+title within a duration tolerance. Rows are always owned by the owner tenant — never the
/// demo tenant (repo rule for all-tenant queries).
/// </para>
/// </summary>
public sealed class SyncIngestService(
    MusicHoarderDbContext db,
    IOwnerLookupService ownerLookup,
    JobManager jobManager,
    IOptionsMonitor<SyncOptions> options,
    ILogger<SyncIngestService> logger) : ISyncIngestService
{
    private const string IncomingSubdir = ".incoming";

    public async Task<SyncCheckResponse> CheckAsync(SyncCheckRequest request, CancellationToken ct)
    {
        var (existing, matchedBy) = await FindExistingAsync(
            request.Fingerprint, request.AcoustIdTrackId, request.MusicBrainzId,
            request.Artist, request.Title, request.DurationMs, ct);

        if (existing is null)
            return new SyncCheckResponse(SyncVerdict.NotPresent, null, null, null);

        var remoteScore = AudioQuality.Score(existing);

        // A duplicate-flagged row is never built or served (see SongMetadata.IsReadyForBuild), so
        // there is nothing here worth upgrading or byte-repairing — but the track IS present, and
        // saying so is what settles the pusher's outbox instead of re-offering the file forever.
        if (existing.IsDuplicate)
            return new SyncCheckResponse(SyncVerdict.PresentSameOrBetter, existing.Id, remoteScore, matchedBy);

        var candidateScore = AudioQuality.Score(request.Extension, request.Bitrate);
        var verdict = candidateScore > remoteScore
            ? SyncVerdict.PresentLowerQuality
            : NeedsByteRepair(existing, request.Fingerprint, request.FileSizeBytes)
                ? SyncVerdict.PresentDifferentBytes
                : SyncVerdict.PresentSameOrBetter;
        return new SyncCheckResponse(verdict, existing.Id, remoteScore, matchedBy);
    }

    public async Task<SyncUploadResponse> IngestAsync(SyncTrackPayload payload, Stream file, CancellationToken ct)
    {
        var opts = options.CurrentValue;
        var (existing, matchedBy) = await FindExistingAsync(
            payload.Fingerprint, payload.AcoustIdTrackId, payload.MusicBrainzId,
            payload.Artist, payload.Title, payload.DurationMs, ct);

        var candidateScore = AudioQuality.Score(payload.Extension, payload.Bitrate);

        if (existing is not null)
        {
            var existingScore = AudioQuality.Score(existing);

            // Mirror of the CheckAsync rule: a duplicate-flagged row is never built or served, so an
            // upgrade or byte-repair would change nothing — and writing a file anyway is precisely
            // what let one track accumulate 1,347 managed copies. Report it present and write nothing.
            if (existing.IsDuplicate)
            {
                logger.LogInformation(
                    "Sync upload skipped (duplicate row) for '{Artist} - {Title}' — existing song {SongId} is flagged a duplicate of {DuplicateOfId}",
                    LogSanitizer.ForLog(payload.Artist), LogSanitizer.ForLog(payload.Title),
                    existing.Id, existing.DuplicateOfId);
                return new SyncUploadResponse(SyncUploadOutcome.SkippedSameOrBetter, existing.Id, existingScore);
            }

            // A same-or-better local copy normally wins — EXCEPT when the local copy is a managed
            // synced file whose bytes no longer match the pusher's artifact for the same fingerprint
            // (stale/corrupted): then the pusher is authoritative and the upload replaces in place.
            if (candidateScore <= existingScore
                && !NeedsByteRepair(existing, payload.Fingerprint, payload.FileSizeBytes))
            {
                var identical = !string.IsNullOrEmpty(payload.Fingerprint)
                    && string.Equals(existing.Fingerprint, payload.Fingerprint, StringComparison.Ordinal);
                logger.LogInformation(
                    "Sync upload skipped ({Reason}) for '{Artist} - {Title}' — existing song {SongId} scores {Existing} vs candidate {Candidate}",
                    identical ? "identical" : "same-or-better", LogSanitizer.ForLog(payload.Artist),
                    LogSanitizer.ForLog(payload.Title), existing.Id, existingScore, candidateScore);
                return new SyncUploadResponse(
                    identical ? SyncUploadOutcome.SkippedIdentical : SyncUploadOutcome.SkippedSameOrBetter,
                    existing.Id, existingScore);
            }
        }

        var finalPath = await WriteFileAsync(payload, file, opts, ct);
        var fileInfo = new FileInfo(finalPath);

        if (existing is null)
        {
            var song = CreateSongRow(payload, finalPath, fileInfo);
            db.Songs.Add(song);
            await db.SaveChangesAsync(ct);
            TriggerBuild();
            logger.LogInformation("Sync created song {SongId} '{Artist} - {Title}' at {Path}",
                song.Id, LogSanitizer.ForLog(payload.Artist), LogSanitizer.ForLog(payload.Title),
                LogSanitizer.ForLog(finalPath));
            return new SyncUploadResponse(SyncUploadOutcome.Created, song.Id, candidateScore);
        }

        // Replace in place: same row/Id, new source file. The old synced file (if any) is removed
        // from disk; files outside the managed synced dir (scanned originals) are left alone.
        var oldPath = existing.SourcePath;
        existing.ApplySourceUpgrade(
            NormalizePath(finalPath),
            fileInfo.Length,
            Path.GetFileName(finalPath),
            payload.Extension.ToLowerInvariant(),
            fileInfo.LastWriteTimeUtc,
            payload.Bitrate,
            payload.Fingerprint,
            payload.DurationSeconds,
            payload.DurationMs);
        ApplyPayloadMetadata(existing, payload);
        existing.ResetLibraryBuild();
        await db.SaveChangesAsync(ct);

        DeleteManagedSourceFile(oldPath, opts);
        TriggerBuild();
        logger.LogInformation(
            "Sync replaced source for song {SongId} '{Artist} - {Title}' (matched by {MatchedBy}): {Old} → {New}",
            existing.Id, LogSanitizer.ForLog(payload.Artist), LogSanitizer.ForLog(payload.Title),
            matchedBy, LogSanitizer.ForLog(oldPath), LogSanitizer.ForLog(finalPath));
        return new SyncUploadResponse(SyncUploadOutcome.Replaced, existing.Id, candidateScore);
    }

    public async Task<SyncLikeResponse> ApplyLikeAsync(SyncLikeRequest request, CancellationToken ct)
    {
        var (existing, matchedBy) = await FindExistingAsync(
            request.Fingerprint, request.AcoustIdTrackId, request.MusicBrainzId,
            request.Artist, request.Title, request.DurationMs, ct);

        if (existing is null)
            return new SyncLikeResponse(Matched: false, SongId: null);

        // Pusher is authoritative (one-way sync): mirror its like verbatim, including a clear.
        existing.LikedAtUtc = request.LikedAtUtc;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Sync applied like ({Liked}) to song {SongId} '{Artist} - {Title}' (matched by {MatchedBy})",
            request.LikedAtUtc is not null, existing.Id, LogSanitizer.ForLog(request.Artist),
            LogSanitizer.ForLog(request.Title), matchedBy);
        return new SyncLikeResponse(Matched: true, SongId: existing.Id);
    }

    /// <summary>
    /// True when the local row holds the SAME track (equal fingerprint) but its managed synced file
    /// has different bytes than what the pusher would send — the historical shared-destination-path
    /// bug shipped one file's bytes under many songs' metadata, and this is how those rows heal:
    /// the pusher re-checks (see the push-side requeue), the size mismatch surfaces, and the upload
    /// replaces the file in place. Restricted to rows whose source lives under
    /// <see cref="SyncOptions.SyncedSourceDirectory"/> — locally-scanned originals are never
    /// repaired away by sync (a different encode of the same fingerprint legitimately differs in size).
    /// </summary>
    private bool NeedsByteRepair(SongMetadata existing, string? fingerprint, long? fileSizeBytes)
    {
        if (fileSizeBytes is not > 0 || string.IsNullOrEmpty(fingerprint))
            return false;
        if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
            return false;
        if (existing.FileSizeBytes == fileSizeBytes.Value)
            return false;

        var managedDir = options.CurrentValue.SyncedSourceDirectory;
        if (string.IsNullOrWhiteSpace(managedDir))
            return false;
        var managedRoot = NormalizePath(managedDir).TrimEnd('/') + "/";
        return NormalizePath(existing.SourcePath).StartsWith(managedRoot, StringComparison.Ordinal);
    }

    /// <summary>
    /// The matching ladder over live rows (owner tenant, non-synthetic, non-deleted). Sequential
    /// FirstOrDefaults — precision order matters more than round-trips at this call rate.
    /// <para>
    /// Duplicate-flagged rows are deliberately INCLUDED (canonical rows still win via the ordering).
    /// They used to be filtered out, which turned every duplicate into a permanent blind spot: once
    /// a track's only local copies were flagged — e.g. by the builder's destination-path collision
    /// rule, which flags each freshly-received copy against the already-built occupant — the ladder
    /// missed on every push, each miss wrote another file and row, and the builder promptly flagged
    /// that one too. One track ran to 1,347 copies that way. A duplicate row is still proof the
    /// track is here, which is exactly what the pusher needs to hear.
    /// </para>
    /// </summary>
    internal async Task<(SongMetadata? Song, string? MatchedBy)> FindExistingAsync(
        string? fingerprint, string? acoustIdTrackId, string? musicBrainzId,
        string? artist, string? title, int? durationMs, CancellationToken ct)
    {
        var live = db.Songs
            .IgnoreQueryFilters()
            .Where(s => s.OwnerUserId == ownerLookup.OwnerUserId
                && !s.IsSynthetic && s.DeletedAtUtc == null);

        if (!string.IsNullOrWhiteSpace(fingerprint))
        {
            var hit = await CanonicalFirst(live.Where(s => s.Fingerprint == fingerprint))
                .FirstOrDefaultAsync(ct);
            if (hit is not null) return (hit, "fingerprint");
        }

        if (!string.IsNullOrWhiteSpace(acoustIdTrackId))
        {
            var hit = await CanonicalFirst(live.Where(s => s.AcoustIdTrackId == acoustIdTrackId))
                .FirstOrDefaultAsync(ct);
            if (hit is not null) return (hit, "acoustid");
        }

        if (!string.IsNullOrWhiteSpace(musicBrainzId))
        {
            var hit = await CanonicalFirst(live.Where(s => s.MusicBrainzId == musicBrainzId))
                .FirstOrDefaultAsync(ct);
            if (hit is not null) return (hit, "mbid");
        }

        if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title) && durationMs is > 0)
        {
            var tolerance = options.CurrentValue.DurationToleranceMs;
            var lo = durationMs.Value - tolerance;
            var hi = durationMs.Value + tolerance;
            // SQL narrows by duration; the normalized text compare happens in memory (TitleNormalizer
            // isn't translatable). The duration window keeps the materialized set tiny.
            var candidates = await live
                .Where(s => s.DurationMs != null && s.DurationMs >= lo && s.DurationMs <= hi)
                .ToListAsync(ct);

            var wantArtist = TitleNormalizer.NormalizeForSearch(artist);
            var wantTitle = TitleNormalizer.NormalizeForSearch(title);
            var hit = candidates
                .Where(s => TitleNormalizer.NormalizeForSearch(s.Artist) == wantArtist
                    && TitleNormalizer.NormalizeForSearch(s.Title) == wantTitle)
                .OrderBy(s => s.IsDuplicate)
                .ThenBy(s => s.Id)
                .FirstOrDefault();
            if (hit is not null) return (hit, "fuzzy");
        }

        return (null, null);
    }

    /// <summary>Canonical rows before duplicate-flagged ones, oldest id first within each.</summary>
    private static IQueryable<SongMetadata> CanonicalFirst(IQueryable<SongMetadata> query) =>
        query.OrderBy(s => s.IsDuplicate).ThenBy(s => s.Id);

    /// <summary>
    /// Streams the upload into <c>.incoming/</c> (skipped by the scanner) then atomically moves it
    /// to its final artist-foldered path — a scan can never observe a half-written file.
    /// </summary>
    private static async Task<string> WriteFileAsync(
        SyncTrackPayload payload, Stream file, SyncOptions opts, CancellationToken ct)
    {
        var extension = payload.Extension.StartsWith('.') ? payload.Extension : "." + payload.Extension;
        extension = extension.ToLowerInvariant();

        var incomingDir = Path.Combine(opts.SyncedSourceDirectory, IncomingSubdir);
        Directory.CreateDirectory(incomingDir);
        var tempPath = Path.Combine(incomingDir, Guid.NewGuid().ToString("N") + extension);

        try
        {
            await using (var target = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 128 * 1024))
            {
                await file.CopyToAsync(target, ct);
            }

            var artistFolder = DestinationPathResolver.Sanitize(
                FirstNonBlank(payload.AlbumArtist, payload.Artist) ?? "Unknown Artist");
            var stem = DestinationPathResolver.Sanitize(
                Path.GetFileNameWithoutExtension(payload.FileName) is { Length: > 0 } s ? s
                    : FirstNonBlank(payload.Title, "track")!);
            var targetDir = Path.Combine(opts.SyncedSourceDirectory, artistFolder);
            Directory.CreateDirectory(targetDir);

            // Unique suffix sidesteps both disk collisions and the (OwnerUserId, SourcePath) unique
            // index — the on-disk name is internal; user-facing naming happens at build time.
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var finalPath = Path.Combine(targetDir, $"{stem} [{suffix}]{extension}");
            File.Move(tempPath, finalPath);
            return finalPath;
        }
        catch
        {
            try { File.Delete(tempPath); } catch (IOException) { /* best effort */ }
            throw;
        }
    }

    private SongMetadata CreateSongRow(SyncTrackPayload payload, string finalPath, FileInfo fileInfo)
    {
        var indexedAt = DateTime.UtcNow;
        var song = new SongMetadata
        {
            OwnerUserId = ownerLookup.OwnerUserId,
            SourcePath = NormalizePath(finalPath),
            FileSizeBytes = fileInfo.Length,
            FileName = Path.GetFileName(finalPath),
            Extension = Path.GetExtension(finalPath).ToLowerInvariant(),
            LastModifiedUtc = fileInfo.LastWriteTimeUtc,
            IndexedAtUtc = indexedAt,
            // A received track is acquired now — the file was just written locally, so its mtime is
            // "now" too; the min() guards against a receiving clock skewed ahead of the file's stamp.
            AcquiredAtUtc = SongMetadata.SeedAcquiredAt(fileInfo.LastWriteTimeUtc, indexedAt),
            Fingerprint = payload.Fingerprint,
            Bitrate = payload.Bitrate,
            DurationSeconds = payload.DurationSeconds,
            DurationMs = payload.DurationMs,
        };
        ApplyPayloadMetadata(song, payload);
        return song;
    }

    /// <summary>
    /// Stamps the pushed identity/tags/lyrics and marks the row Matched + manually-approved:
    /// the pushing instance already ran multi-provider consensus, so this instance's own sweeps
    /// must treat the identity as curated and never re-enrich or reset it.
    /// </summary>
    private static void ApplyPayloadMetadata(SongMetadata song, SyncTrackPayload payload)
    {
        song.Artist = payload.Artist;
        song.AlbumArtist = payload.AlbumArtist;
        song.Album = payload.Album;
        song.Title = payload.Title;
        song.Year = payload.Year;
        song.TrackNumber = payload.TrackNumber;
        song.DiscNumber = payload.DiscNumber;
        song.TotalDiscs = payload.TotalDiscs;
        song.TotalTracks = payload.TotalTracks;
        song.Artists = payload.Artists;
        song.ArtistMusicBrainzIds = payload.ArtistMusicBrainzIds;
        song.AlbumArtistMusicBrainzId = payload.AlbumArtistMusicBrainzId;
        song.IsCompilation = payload.IsCompilation;
        song.ReleaseTypePrimary = payload.ReleaseTypePrimary;
        song.ReleaseTypes = payload.ReleaseTypes;
        song.IsUnreleased = payload.IsUnreleased;

        song.Isrc = payload.Isrc;
        song.MusicBrainzId = payload.MusicBrainzId;
        song.MusicBrainzReleaseId = payload.MusicBrainzReleaseId;
        song.MusicBrainzReleaseGroupId = payload.MusicBrainzReleaseGroupId;
        song.SpotifyId = payload.SpotifyId;
        song.AcoustIdTrackId = payload.AcoustIdTrackId;
        song.LrclibId = payload.LrclibId;

        song.CaptureOriginalMetadata();
        song.EnrichmentStatus = EnrichmentStatus.Matched;
        song.MatchedBy = string.IsNullOrWhiteSpace(payload.MatchedBy) ? "sync" : payload.MatchedBy + "+sync";
        song.MatchConfidence = payload.MatchConfidence;
        song.EnrichedAtUtc = DateTime.UtcNow;
        song.EnrichmentError = null;
        song.LockManualApproval();

        song.PlainLyrics = payload.PlainLyrics;
        song.SyncedLyrics = payload.SyncedLyrics;
        song.IsInstrumental = payload.IsInstrumental;
        song.LyricsStatus = payload.LyricsStatus;

        // The pusher owns the like (one-way sync): mirror it, even clearing a stale local like.
        song.LikedAtUtc = payload.LikedAtUtc;

        // Wishlist rows don't travel, so the pusher's intent is the only way the receiver can tell an
        // album-fill track from one the owner asked for. An older pusher omits it and reads as Explicit.
        song.AcquisitionIntent = payload.AcquisitionIntent;
    }

    private void DeleteManagedSourceFile(string sourcePath, SyncOptions opts)
    {
        var managedRoot = NormalizePath(opts.SyncedSourceDirectory).TrimEnd('/') + "/";
        if (!NormalizePath(sourcePath).StartsWith(managedRoot, StringComparison.Ordinal))
            return; // scanned original outside the managed dir — never touch it
        try
        {
            if (File.Exists(sourcePath))
                File.Delete(sourcePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Failed to delete replaced synced source file {Path}", LogSanitizer.ForLog(sourcePath));
        }
    }

    private void TriggerBuild()
    {
        if (jobManager.TryStartJob(JobType.Build, out var jobId, out _))
            logger.LogInformation("Sync ingest triggered build job {JobId}", jobId);
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string NormalizePath(string path) => path.Replace('\\', '/');
}

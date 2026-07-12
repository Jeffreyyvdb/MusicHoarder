using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Logging;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Sync;

/// <summary>
/// The testable core of the sync push: the sweep predicate (which built tracks still owe the remote
/// a check/upload) and the per-song check → upload → outbox state machine. The
/// <see cref="TrackSyncBackgroundService"/> owns the loop/scoping and delegates here.
/// </summary>
public class TrackSyncProcessor(
    MusicHoarderDbContext db,
    ISyncPushClient pushClient,
    IOwnerLookupService ownerLookup,
    IOptionsMonitor<SyncOptions> options,
    ILogger<TrackSyncProcessor> logger)
{
    /// <summary>Uploading rows untouched this long are crash leftovers the sweep reclaims.</summary>
    private static readonly TimeSpan UploadingStaleAfter = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Song ids that owe the remote work: fully built, live, canonical (non-duplicate), owner-tenant
    /// songs with no outbox row, a re-armed one (fingerprint changed since last sync — i.e. a local
    /// quality upgrade), a crash-stale Pending/Uploading row, or a retryable failure.
    /// </summary>
    public async Task<List<int>> FindSweepCandidatesAsync(int limit, CancellationToken ct)
    {
        var opts = options.CurrentValue;
        var now = DateTime.UtcNow;
        var uploadingCutoff = now - UploadingStaleAfter;
        var ownerId = ownerLookup.OwnerUserId;

        return await (
            from s in db.Songs.IgnoreQueryFilters()
            where s.OwnerUserId == ownerId
                && !s.IsSynthetic
                && s.DeletedAtUtc == null
                && !s.IsDuplicate
                && s.LibraryBuildStatus == LibraryBuildStatus.Done
                && s.DestinationPath != null
            join st0 in db.TrackSyncStates on s.Id equals st0.SongId into states
            from st in states.DefaultIfEmpty()
            where st == null
                || st.Status == TrackSyncStatus.Pending
                || (st.Status == TrackSyncStatus.Uploading && st.UpdatedAtUtc < uploadingCutoff)
                || ((st.Status == TrackSyncStatus.Synced || st.Status == TrackSyncStatus.SkippedRemoteBetter)
                    && (st.SyncedFingerprint != s.Fingerprint
                        // A like toggle doesn't touch the fingerprint — re-arm to propagate it.
                        || st.SyncedLiked != (s.LikedAtUtc != null)))
                || (st.Status == TrackSyncStatus.Failed
                    && st.Attempts < opts.MaxAttempts
                    && (st.NextAttemptAtUtc == null || st.NextAttemptAtUtc <= now))
            orderby s.Id
            select s.Id)
            .Take(limit)
            .ToListAsync(ct);
    }

    /// <summary>Runs one song through check → (upload) → terminal outbox state. Never throws for
    /// per-song failures — they land in the outbox row as Failed with backoff.</summary>
    public async Task ProcessSongAsync(int songId, CancellationToken ct)
    {
        var song = await db.Songs
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == songId && s.OwnerUserId == ownerLookup.OwnerUserId, ct);

        // Re-check eligibility — the row may have changed between enqueue and processing.
        if (song is null || song.IsSynthetic || song.IsDeleted || song.IsDuplicate
            || song.LibraryBuildStatus != LibraryBuildStatus.Done)
            return;

        // IgnoreQueryFilters: the push worker runs in a background scope with no logged-in user, so
        // the owner-scoped TrackSyncState filter (Song.OwnerUserId == userId, userId = Guid.Empty
        // here) would hide every existing outbox row. Without this the lookup returns null even when
        // a row exists, and the insert below collides with the unique SongId index (23505) — which
        // also defeats the retry backoff, re-firing forever. Owner scoping is enforced explicitly by
        // the ownerLookup guard on the song above.
        var state = await db.TrackSyncStates.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.SongId == songId, ct);
        if (state is null)
        {
            state = new TrackSyncState { SongId = songId, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow };
            db.TrackSyncStates.Add(state);
        }
        state.MarkUploading();
        await db.SaveChangesAsync(ct);

        var opts = options.CurrentValue;
        try
        {
            var filePath = ResolveUploadFile(song);
            if (filePath is null)
            {
                state.MarkFailed("neither destination nor source file exists on disk", opts.RetryBaseDelaySeconds, opts.MaxAttempts);
                await db.SaveChangesAsync(ct);
                return;
            }

            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            var check = await pushClient.CheckAsync(new SyncCheckRequest(
                song.Fingerprint, song.AcoustIdTrackId, song.MusicBrainzId,
                song.Artist, song.Title, song.DurationMs, extension, song.Bitrate), ct);
            if (check is null)
                throw new InvalidOperationException("sync check returned no body");

            if (check.Verdict == SyncVerdict.PresentSameOrBetter)
            {
                // File is already there at >= quality — no re-upload. A like change still needs to
                // land, so send a metadata-only like push when it diverged from what we last synced.
                await PushLikeIfChangedAsync(song, state, ct);
                state.MarkSkippedRemoteBetter(song.Fingerprint, check.SongId, check.RemoteQualityScore);
                state.SyncedLiked = song.IsLiked;
                await db.SaveChangesAsync(ct);
                return;
            }

            var upload = await pushClient.UploadAsync(BuildPayload(song, filePath, extension), filePath, ct);
            if (upload is null)
                throw new InvalidOperationException("sync upload returned no body");

            state.MarkSynced(song.Fingerprint, upload.SongId, upload.QualityScore);
            state.SyncedLiked = song.IsLiked; // the payload carried LikedAtUtc, so the remote has it now
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Synced song {SongId} '{Artist} - {Title}' → remote {RemoteId} ({Outcome})",
                song.Id, LogSanitizer.ForLog(song.Artist), LogSanitizer.ForLog(song.Title),
                upload.SongId, upload.Outcome);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            state.MarkFailed(ex.Message, opts.RetryBaseDelaySeconds, opts.MaxAttempts);
            await db.SaveChangesAsync(CancellationToken.None);
            logger.LogWarning(ex, "Sync push failed for song {SongId} (attempt {Attempts})", songId, state.Attempts);
        }
    }

    /// <summary>
    /// Sends a metadata-only like update when the song's like diverged from what was last pushed.
    /// A throw propagates to <see cref="ProcessSongAsync"/>'s catch → the row fails with backoff and
    /// retries, so <see cref="TrackSyncState.SyncedLiked"/> is only advanced once the push succeeds.
    /// </summary>
    private async Task PushLikeIfChangedAsync(SongMetadata song, TrackSyncState state, CancellationToken ct)
    {
        if (state.SyncedLiked == song.IsLiked)
            return;

        var response = await pushClient.PushLikeAsync(new SyncLikeRequest(
            song.Fingerprint, song.AcoustIdTrackId, song.MusicBrainzId,
            song.Artist, song.Title, song.DurationMs, song.LikedAtUtc), ct);
        if (response is null)
            throw new InvalidOperationException("sync like returned no body");

        logger.LogInformation("Synced like ({Liked}) for song {SongId} '{Artist} - {Title}' → remote {RemoteId} (matched={Matched})",
            song.IsLiked, song.Id, LogSanitizer.ForLog(song.Artist), LogSanitizer.ForLog(song.Title),
            response.SongId, response.Matched);
    }

    /// <summary>
    /// The built destination file is THE sync artifact (canonical tags, embedded lyrics/cover). The
    /// source is a loud last resort so a vanished destination degrades instead of wedging the row.
    /// </summary>
    private string? ResolveUploadFile(SongMetadata song)
    {
        if (!string.IsNullOrEmpty(song.DestinationPath) && File.Exists(song.DestinationPath))
            return song.DestinationPath;
        if (File.Exists(song.SourcePath))
        {
            logger.LogWarning(
                "Destination file missing for song {SongId} ({Destination}) — uploading the SOURCE file instead",
                song.Id, LogSanitizer.ForLog(song.DestinationPath));
            return song.SourcePath;
        }
        return null;
    }

    private static SyncTrackPayload BuildPayload(SongMetadata song, string filePath, string extension)
    {
        var fileInfo = new FileInfo(filePath);
        return new SyncTrackPayload(
            FileName: Path.GetFileName(filePath),
            Extension: extension,
            FileSizeBytes: fileInfo.Length,
            Bitrate: song.Bitrate,
            DurationSeconds: song.DurationSeconds,
            DurationMs: song.DurationMs,
            Fingerprint: song.Fingerprint,
            Isrc: song.Isrc,
            MusicBrainzId: song.MusicBrainzId,
            MusicBrainzReleaseId: song.MusicBrainzReleaseId,
            MusicBrainzReleaseGroupId: song.MusicBrainzReleaseGroupId,
            SpotifyId: song.SpotifyId,
            AcoustIdTrackId: song.AcoustIdTrackId,
            LrclibId: song.LrclibId,
            Artist: song.Artist,
            AlbumArtist: song.AlbumArtist,
            Album: song.Album,
            Title: song.Title,
            Year: song.Year,
            TrackNumber: song.TrackNumber,
            DiscNumber: song.DiscNumber,
            TotalDiscs: song.TotalDiscs,
            TotalTracks: song.TotalTracks,
            Artists: song.Artists,
            ArtistMusicBrainzIds: song.ArtistMusicBrainzIds,
            AlbumArtistMusicBrainzId: song.AlbumArtistMusicBrainzId,
            IsCompilation: song.IsCompilation,
            ReleaseTypePrimary: song.ReleaseTypePrimary,
            ReleaseTypes: song.ReleaseTypes,
            IsUnreleased: song.IsUnreleased,
            MatchedBy: song.MatchedBy,
            MatchConfidence: song.MatchConfidence,
            PlainLyrics: song.EffectivePlainLyrics,
            SyncedLyrics: song.EffectiveSyncedLyrics,
            IsInstrumental: song.IsInstrumental,
            LyricsStatus: song.LyricsStatus,
            LikedAtUtc: song.LikedAtUtc);
    }
}

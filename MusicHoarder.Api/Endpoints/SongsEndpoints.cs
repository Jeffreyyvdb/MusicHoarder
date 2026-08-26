using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Artwork;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Contracts;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Navidrome;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Scanner;
using MusicHoarder.Api.Sharing;
using MusicHoarder.Api.Spotify;
using MusicHoarder.Api.Sync;

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
        app.MapPost("/songs/{id:int}/lyrics/recheck", RecheckLyrics)
            .WithName("RecheckSongLyrics")
            .WithSummary("Ask LRCLIB again for a song whose lyrics are missing or unsynced — LRCLIB gains lyrics over time. Ignores the automatic backoff; never clears lyrics already stored.")
            .WithTags("Lyrics");
        app.MapPost("/songs/{id:int}/lyrics/preferred", SetPreferredLyrics)
            .WithName("SetPreferredLyrics")
            .WithSummary("Choose which lyrics (lrclib | transcribed) the synced viewer shows when both exist.")
            .WithTags("Lyrics");
        app.MapPost("/songs/{id:int}/lyrics/translate", TranslateLyrics)
            .WithName("TranslateSongLyrics")
            .WithSummary("Generate a pronunciation guide (romanization) + English translation of this song's lyrics via LLM. Display-only; never written to file tags.")
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
            .WithSummary("List duplicate clusters (confirmed + suspected) with keeper election and per-member match evidence.")
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

        // Likes + play reporting. Ownership comes from the per-user query filter (a foreign song id
        // resolves to 404); demo sessions are already write-blocked by DemoReadOnlyMiddleware.
        app.MapPost("/songs/{id:int}/like", LikeSong)
            .WithName("LikeSong")
            .WithSummary("Mark a song as liked (idempotent; the timestamp is the recently-liked sort key).")
            .WithTags("Tracks");
        app.MapDelete("/songs/{id:int}/like", UnlikeSong)
            .WithName("UnlikeSong")
            .WithSummary("Remove a song from liked songs.")
            .WithTags("Tracks");
        app.MapPost("/songs/{id:int}/played", ReportPlayed)
            .WithName("ReportSongPlayed")
            .WithSummary("Record a playback start: bumps the play count and last-played timestamp.")
            .WithTags("Tracks");

        return app;
    }

    /// <summary>
    /// Like a song, whether the caller owns it or was granted it.
    ///
    /// <para>
    /// The whole rule in one branch: <b>own the row → write the row's own columns; do not own it →
    /// write a <see cref="UserSongState"/> row.</b> Branch on <c>slice.IsSelf</c>, never on the
    /// caller's role — an admin can hold a grant too, and treating their like on someone else's
    /// track as their own would corrupt the grantor's library.
    /// </para>
    ///
    /// <para>
    /// The Navidrome and instance-sync enqueues stay strictly inside the owns-it branch. Those
    /// mirror the library owner's own taste to their own servers; pushing a guest's like there
    /// would silently star tracks in the owner's Navidrome that the owner never liked.
    /// </para>
    /// </summary>
    internal static async Task<IResult> LikeSong(
        int id, MusicHoarderDbContext db, ILibraryScopeResolver scopeResolver,
        ICurrentUserAccessor currentUser, INavidromeLikeEnqueuer navidrome,
        ITrackSyncEnqueuer trackSync, CancellationToken ct)
    {
        var found = await scopeResolver.ResolveSongAsync(db, id, ct);
        if (found is null)
            return SongNotFound();

        if (!found.Value.Slice.IsSelf)
        {
            var state = await UserSongStateWriter.UpsertAsync(
                db, currentUser.UserId, id, s => s.LikedAtUtc ??= DateTime.UtcNow, ct);
            return Results.Ok(new { Id = id, state.LikedAtUtc });
        }

        // Tracked read: ResolveSongAsync returns a no-tracking entity, which cannot be saved.
        var song = await db.Songs.FirstOrDefaultAsync(s => s.Id == id && s.DeletedAtUtc == null, ct);
        if (song is null)
            return SongNotFound();

        var wasLiked = song.IsLiked;
        song.LikedAtUtc ??= DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        // Propagate on the flip only (idempotent re-likes needn't re-sync). Both enqueuers are inert
        // unless their feature is configured — Navidrome creds / sync Push mode respectively.
        if (!wasLiked)
        {
            navidrome.TryEnqueue(song.Id, song.OwnerUserId);
            trackSync.TryEnqueue(song.Id, song.OwnerUserId);
        }
        return Results.Ok(new { song.Id, song.LikedAtUtc });
    }

    /// <inheritdoc cref="LikeSong"/>
    internal static async Task<IResult> UnlikeSong(
        int id, MusicHoarderDbContext db, ILibraryScopeResolver scopeResolver,
        ICurrentUserAccessor currentUser, INavidromeLikeEnqueuer navidrome,
        ITrackSyncEnqueuer trackSync, CancellationToken ct)
    {
        var found = await scopeResolver.ResolveSongAsync(db, id, ct);
        if (found is null)
            return SongNotFound();

        if (!found.Value.Slice.IsSelf)
        {
            await UserSongStateWriter.UpsertAsync(
                db, currentUser.UserId, id, s => s.LikedAtUtc = null, ct);
            return Results.Ok(new { Id = id, LikedAtUtc = (DateTime?)null });
        }

        var song = await db.Songs.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (song is null)
            return SongNotFound();

        var wasLiked = song.IsLiked;
        song.LikedAtUtc = null;
        await db.SaveChangesAsync(ct);
        if (wasLiked)
        {
            navidrome.TryEnqueue(song.Id, song.OwnerUserId);
            trackSync.TryEnqueue(song.Id, song.OwnerUserId);
        }
        return Results.Ok(new { song.Id, LikedAtUtc = (DateTime?)null });
    }

    /// <inheritdoc cref="LikeSong"/>
    internal static async Task<IResult> ReportPlayed(
        int id, MusicHoarderDbContext db, ILibraryScopeResolver scopeResolver,
        ICurrentUserAccessor currentUser, CancellationToken ct)
    {
        var found = await scopeResolver.ResolveSongAsync(db, id, ct);
        if (found is null)
            return SongNotFound();

        if (!found.Value.Slice.IsSelf)
        {
            var state = await UserSongStateWriter.UpsertAsync(db, currentUser.UserId, id, s =>
            {
                s.PlayCount++;
                s.LastPlayedAtUtc = DateTime.UtcNow;
            }, ct);
            return Results.Ok(new { Id = id, state.PlayCount, state.LastPlayedAtUtc });
        }

        var song = await db.Songs.FirstOrDefaultAsync(s => s.Id == id && s.DeletedAtUtc == null, ct);
        if (song is null)
            return SongNotFound();

        song.PlayCount++;
        song.LastPlayedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { song.Id, song.PlayCount, song.LastPlayedAtUtc });
    }

    /// <summary>
    /// Every song the caller may see: their own rows, plus anything shared with them.
    ///
    /// <para>
    /// The two halves are deliberately asymmetric. Own rows run the original query and full
    /// projection untouched, still behind the ambient tenancy filter. Granted rows go through
    /// <see cref="ILibraryScopeResolver"/> and the redacted <see cref="SharedSongRowDto"/>. That
    /// asymmetry is the safety property: a member owns zero song rows, so the own-rows half is
    /// empty for them by construction, and an admin with no grants runs exactly the code path that
    /// shipped before this endpoint was unified.
    /// </para>
    /// </summary>
    internal static async Task<IResult> ListSongs(
        MusicHoarderDbContext db,
        ILibraryScopeResolver scopeResolver,
        IOptions<MusicEnricherOptions> enricherOptions,
        IOptions<SyncOptions> syncOptions,
        CancellationToken ct,
        bool includeDeleted = false,
        string? enrichmentStatus = null)
    {
        var query = db.Songs.AsNoTracking();
        if (!includeDeleted)
            query = query.Where(s => s.DeletedAtUtc == null);

        if (!string.IsNullOrWhiteSpace(enrichmentStatus) &&
            Enum.TryParse<EnrichmentStatus>(enrichmentStatus, ignoreCase: true, out var parsedStatus))
        {
            query = query.Where(s => s.EnrichmentStatus == parsedStatus);
        }

        // Provenance and Spotify save dates, loaded once and shared with GET /api/albums so the
        // track list and the album grid can never disagree about when a track arrived.
        var saveDates = await SpotifySaveDates.LoadAsync(db, ct);

        var downloadDirectory = enricherOptions.Value.DownloadDirectory;
        var syncedSourceDirectory = syncOptions.Value.SyncedSourceDirectory;

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
                s.AcquiredAtUtc,
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
                s.Genre,
                s.ReleaseDate,
                s.OriginalReleaseDate,
                s.Label,
                s.CatalogNumber,
                s.Upc,
                s.Composer,
                s.Copyright,
                s.ArtistSort,
                s.AlbumArtistSort,
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
                s.IsUnreleased,
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
                s.LikedAtUtc,
                s.PlayCount,
                s.LastPlayedAtUtc,
                s.AcquisitionIntent,
                // "There is a music video on disk for this track." Same predicate the stream endpoint
                // gates on and the share tracklist already uses, reached through the one-to-one nav so
                // EF emits a LEFT JOIN on the unique IX_SongMusicVideos_SongId — no extra round-trip.
                // Deliberately NOT VideoInfoDto.FileMissing: that needs a per-row File.Exists, which is
                // not something to do thousands of times in a full-library listing.
                HasMusicVideo = s.MusicVideo != null
                    && s.MusicVideo.Status == MusicVideoStatus.Ready
                    && s.MusicVideo.FilePath != null,
            })
            .ToListAsync();

        var projected = songs.Select(s =>
        {
            var origin = SongOriginResolver.Resolve(
                s.SourcePath,
                saveDates.LinkFor(s.Id),
                downloadDirectory,
                syncedSourceDirectory);
            var matchWarnings = DeserializeWarnings(s.MatchWarnings);
            return new
            {
            s.Id, s.SourcePath, s.FileName, s.Extension, s.FileSizeBytes,
            s.LastModifiedUtc, s.IndexedAtUtc, s.AcquiredAtUtc, s.DeletedAtUtc,
            s.Artist, s.Artists, s.AlbumArtist, s.Album, s.Title, s.Year, s.TrackNumber,
            s.DurationSeconds, s.DurationMs,
            s.Bitrate,
            s.HasCoverArt,
            s.Fingerprint,
                s.Isrc, s.MusicBrainzId, s.MusicBrainzReleaseId, s.SpotifyId, s.AcoustIdTrackId, s.LrclibId,
            s.Genre, s.ReleaseDate, s.OriginalReleaseDate, s.Label, s.CatalogNumber, s.Upc,
            s.Composer, s.Copyright, s.ArtistSort, s.AlbumArtistSort,
            s.EnrichmentStatus, s.MatchedBy, s.MatchConfidence,
            MatchWarnings = matchWarnings,
            // Released vs unreleased (leak/snippet/stem), derived from the tracker category the
            // enrichment match already recorded — see ReleaseClassifier.
            ReleaseClassification = ReleaseClassifier
                .Classify(s.IsUnreleased, s.EnrichmentStatus, s.MatchedBy, matchWarnings, s.Isrc, s.SpotifyId)
                .ToString(),
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
            PreferredLyricsSource = s.PreferredLyricsSource.ToString(),
            s.LikedAtUtc,
            s.PlayCount,
            s.LastPlayedAtUtc,
            // Provenance — how the file got here, which collection asked for it, and Spotify's own
            // save date (distinct from LikedAtUtc, which is the local heart). The save date takes
            // the earliest of the wishlist link and the liked-songs match cache: both are Spotify
            // timestamps, and the earliest is the real "when did I save this" moment.
            OriginKind = origin.Kind.ToString(),
            OriginSource = origin.Source.ToString(),
            OriginDetail = origin.Detail,
            SpotifyAddedAtUtc = saveDates.SaveDateFor(s.Id, s.SpotifyId, origin.SpotifyAddedAtUtc),
            // Spotify's save date for the Liked Songs collection *specifically* — what the library's
            // "Spotify Liked" filter tests and orders by. SpotifyAddedAtUtc above cannot answer that:
            // a playlist wishlist link also carries a date (when the track entered that playlist), so
            // a track you never saved would read as liked. Only the liked-songs link and the
            // liked_sync match cache (already filtered to that source) contribute here.
            SpotifyLikedAtUtc = saveDates.SaveDateFor(
                s.Id, s.SpotifyId,
                origin.Source == SongOriginSource.SpotifyLiked ? origin.SpotifyAddedAtUtc : null),
            s.HasMusicVideo,
            // Whether the owner asked for this track or album completion added it. Unlike the derived
            // Origin* fields above this is a stored column, so it's the one "My music" filters on.
            // Kept as a string alongside IsAlbumFill below: shipped Android builds read this name.
            AcquisitionIntent = s.AcquisitionIntent.ToString(),
            // Two facts both clients used to work out for themselves — from a stringly-typed enum and
            // from a status-plus-path pair, each with its own number-or-name handling. They are
            // decided here now, so "is this built" and "did album completion add this" have one
            // definition instead of one per client. Both were already computed server-side for rows
            // shared with you (SharedSongRowDto) and in the duplicates projection; emitting them for
            // your own rows too is what removes the asymmetry, not just the duplication.
            //
            // The clients keep their old derivations as a fallback, because a phone from the Play
            // Store can be newer than the self-hosted server it talks to. Those fallbacks can go once
            // no supported server predates this field.
            IsAlbumFill = s.AcquisitionIntent == SongAcquisitionIntent.AlbumFill,
            IsBuilt = s.LibraryBuildStatus == LibraryBuildStatus.Done && s.DestinationPath != null,
        };
        }).ToList();

        // Anything shared with the caller, appended after their own rows. `includeDeleted` and
        // `enrichmentStatus` deliberately do not apply here — they are pipeline-triage filters over
        // rows you own, and ScopeSongs already excludes deleted, duplicate, and synthetic rows.
        var scope = await scopeResolver.ResolveAsync(db, ct);
        var (sharedSongs, grantors) =
            await SharedSongProjection.BuildAsync(db, scope, scope.Slices[0].GrantorUserId, ct);

        // List<object> so both row shapes serialize by their runtime type. An admin with no grants
        // gets an empty `grantors` array and an otherwise byte-identical payload to before.
        var allRows = new List<object>(projected.Count + sharedSongs.Count);
        allRows.AddRange(projected);
        allRows.AddRange(sharedSongs);

        return Results.Ok(new
        {
            Count = allRows.Count,
            IncludeDeleted = includeDeleted,
            Songs = allRows,
            Grantors = grantors,
        });
    }

    private static async Task<IResult> GetTrackLyrics(
        int id, MusicHoarderDbContext db, ILibraryScopeResolver scopeResolver, CancellationToken ct)
    {
        // Full entity (not a projection) so the staleness check can use the Display*/hash computed props.
        var found = await scopeResolver.ResolveSongAsync(db, id, ct);
        if (found is null)
            return SongNotFound();

        var (song, slice) = found.Value;

        // A grantee gets the reader's view, not the editor's: what the in-app viewer would show,
        // with stale translations withheld and no transcription or pipeline state. Same shape and
        // staleness rules as the anonymous share lyrics in SharesEndpoints.
        if (!slice.IsSelf)
        {
            var translationFresh =
                song.LyricsTranslationStatus == LyricsTranslationStatus.Completed
                && !song.IsLyricsTranslationStale;
            return Results.Ok(new
            {
                song.Id,
                Synced = song.DisplaySyncedLyrics,
                Plain = song.DisplayPlainLyrics,
                IsInstrumental = song.IsInstrumental == true,
                RomanizedSynced = translationFresh ? song.RomanizedSyncedLyrics : null,
                RomanizedPlain = translationFresh ? song.RomanizedPlainLyrics : null,
                TranslatedSynced = translationFresh ? song.TranslatedSyncedLyrics : null,
                TranslatedPlain = translationFresh ? song.TranslatedPlainLyrics : null,
                DetectedLanguage = translationFresh ? song.DetectedLyricsLanguage : null,
            });
        }

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
            RomanizedSynced = song.RomanizedSyncedLyrics,
            RomanizedPlain = song.RomanizedPlainLyrics,
            TranslatedSynced = song.TranslatedSyncedLyrics,
            TranslatedPlain = song.TranslatedPlainLyrics,
            DetectedLanguage = song.DetectedLyricsLanguage,
            LyricsTranslationStatus = song.LyricsTranslationStatus.ToString(),
            song.LyricsTranslatedAtUtc,
            song.LyricsTranslationModel,
            LyricsTranslationStale = song.IsLyricsTranslationStale,
        });
    }

    /// <summary>
    /// Manual "look again" for a song LRCLIB had nothing (or only unsynced lyrics) for when it was
    /// enriched. The background sweep does this on a multi-day backoff; this is the escape hatch for
    /// when the user can see the lyrics on lrclib.net right now.
    /// </summary>
    private static async Task<IResult> RecheckLyrics(
        int id,
        MusicHoarderDbContext db,
        IEnrichmentOrchestrator orchestrator,
        CancellationToken ct)
    {
        var song = await db.Songs
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id && s.DeletedAtUtc == null, ct);

        if (song is null)
            return Results.NotFound(new { message = $"Song with id {id} not found." });

        // A song that never had its first fetch belongs to the normal fetch path, not the re-check one.
        var updated = song.IsReadyForLyricsFetch
            ? await orchestrator.FetchLyricsForSongAsync(id, ct)
            : song.IsLyricsRecheckCandidate
                ? await orchestrator.RecheckLyricsForSongAsync(id, force: true, ct)
                : false;

        if (!updated && !song.IsReadyForLyricsFetch && !song.IsLyricsRecheckCandidate)
        {
            var reason = song.LyricsStatus switch
            {
                LyricsStatus.Instrumental => "Track is marked instrumental — LRCLIB has nothing to add.",
                LyricsStatus.Fetched => "Track already has synced lyrics.",
                _ => "Track is not eligible for a lyrics fetch (it needs a title, an artist, and a resolved enrichment match).",
            };
            return Results.UnprocessableEntity(new { message = reason });
        }

        var refreshed = await db.Songs
            .AsNoTracking()
            .FirstAsync(s => s.Id == id, ct);

        return Results.Ok(new
        {
            refreshed.Id,
            Updated = updated,
            LyricsStatus = refreshed.LyricsStatus.ToString(),
            HasSyncedLyrics = !string.IsNullOrWhiteSpace(refreshed.SyncedLyrics),
            HasPlainLyrics = !string.IsNullOrWhiteSpace(refreshed.PlainLyrics),
            refreshed.LyricsLastAttemptedAtUtc,
            refreshed.LyricsNextRecheckAfterUtc,
        });
    }

    internal static async Task<IResult> TranscribeLyrics(
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

        var filePath = ResolveAudioFilePath(song);

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

            // A re-sync of the song's OWN official lyrics (forced alignment against the LRCLIB text)
            // is the same words with better timing, so it becomes the default automatically — that is
            // the entire point of asking for a re-sync on a track that already has lyrics. A
            // transcription with no reference text is the AI's guess at the words and is never
            // promoted; the user picks that one from the compare view.
            var promoted = false;
            if (result.AlignedToReference
                && !string.IsNullOrWhiteSpace(song.TranscribedSyncedLyrics)
                && song.PreferredLyricsSource != PreferredLyricsSource.Transcribed)
            {
                song.PreferredLyricsSource = PreferredLyricsSource.Transcribed;
                promoted = true;
            }

            // If the AI version is this song's chosen default, the file's effective lyrics just changed —
            // re-tag the built destination so it reflects the fresh transcription.
            var retagQueued = false;
            if (song.PreferredLyricsSource == PreferredLyricsSource.Transcribed
                && song.LibraryBuildStatus == LibraryBuildStatus.Done)
            {
                song.RequeueForRetag();
                retagQueued = true;
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
                // True when this run re-timed the official lyrics rather than inventing its own words.
                Resynced = result.AlignedToReference,
                // Lets the client show the new version in the viewer without a refetch.
                PreferredLyricsSource = song.PreferredLyricsSource.ToString(),
                Promoted = promoted,
                RetagQueued = retagQueued,
                // The fresh transcription may have changed the display lyrics out from under an
                // existing pronunciation/translation — the client regenerates when true.
                LyricsTranslationStale = song.IsLyricsTranslationStale,
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

    private static async Task<IResult> TranslateLyrics(
        int id,
        MusicHoarderDbContext db,
        ILyricsTranslationService translator,
        CancellationToken ct)
    {
        if (!translator.IsConfigured)
            return Results.Json(
                new { message = "Lyrics translation is not configured. Set QualityGrading:ApiKey/BaseUrl (and optionally LyricsTranslation:Model)." },
                statusCode: StatusCodes.Status503ServiceUnavailable);

        var song = await db.Songs.FirstOrDefaultAsync(s => s.Id == id && s.DeletedAtUtc == null, ct);
        if (song is null)
            return Results.NotFound(new { message = $"Song with id {id} not found." });

        if (song.IsInstrumental == true)
            return Results.UnprocessableEntity(new { message = "Track is marked instrumental — nothing to translate." });

        // Translate whatever the viewer shows (LRCLIB or the chosen/only AI transcription).
        var synced = song.DisplaySyncedLyrics;
        var plain = song.DisplayPlainLyrics;
        if (string.IsNullOrWhiteSpace(synced) && string.IsNullOrWhiteSpace(plain))
            return Results.UnprocessableEntity(new { message = "No lyrics to translate — fetch or transcribe lyrics first." });

        try
        {
            var result = await translator.TranslateAsync(synced, plain, song.Artist, song.Title, ct);
            // Display-only: stored apart from the real lyrics and never re-tags the destination file.
            // The source fingerprint makes the translation self-invalidating when the lyrics change.
            song.ApplyLyricsTranslationResult(
                result.RomanizedSynced, result.RomanizedPlain,
                result.TranslatedSynced, result.TranslatedPlain,
                result.LanguageCode, result.Model,
                SongMetadata.ComputeLyricsFingerprint(song.CurrentLyricsForTranslation));
            await db.SaveChangesAsync(ct);

            return Results.Ok(new
            {
                song.Id,
                RomanizedSynced = song.RomanizedSyncedLyrics,
                RomanizedPlain = song.RomanizedPlainLyrics,
                TranslatedSynced = song.TranslatedSyncedLyrics,
                TranslatedPlain = song.TranslatedPlainLyrics,
                DetectedLanguage = song.DetectedLyricsLanguage,
                LyricsTranslationStatus = song.LyricsTranslationStatus.ToString(),
                song.LyricsTranslatedAtUtc,
                Model = song.LyricsTranslationModel,
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            song.MarkLyricsTranslationFailed(ex.Message);
            await db.SaveChangesAsync(CancellationToken.None);
            return Results.Json(
                new { message = "Lyrics translation failed.", error = ex.Message },
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
            // Flipping the source changes the display lyrics — an existing pronunciation/translation
            // may now describe the other variant; the client auto-regenerates when true.
            LyricsTranslationStale = song.IsLyricsTranslationStale,
        });
    }

    internal static async Task<IResult> ResetEnrichmentBatch(
        EnrichmentResetRequest request,
        MusicHoarderDbContext db,
        EnrichmentPipelineChannel channel)
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

        // ProviderAttempts must be loaded: ResetEnrichment clears the collection, and on an
        // unloaded navigation that Clear() is a silent no-op that leaves every attempt row behind.
        var songs = await query.Include(s => s.ProviderAttempts).ToListAsync();
        foreach (var song in songs)
            song.ResetEnrichment(request.RestoreOriginalMetadata);

        await db.SaveChangesAsync();

        // Nothing else enqueues a Pending song: the retry sweep only picks up songs whose provider
        // attempts have come off cooldown, and the pending backfill runs on startup. Without this the
        // reset rows sit in Pending — out of the destination library and out of every review queue.
        channel.EnqueueRange(songs.Select(s => s.Id), $"Reset — {target}");

        return Results.Ok(new
        {
            request.Target,
            request.RestoreOriginalMetadata,
            ResetCount = songs.Count
        });
    }

    internal static async Task<IResult> ResetSongEnrichment(
        int id,
        MusicHoarderDbContext db,
        EnrichmentPipelineChannel channel,
        bool restoreOriginalMetadata = true,
        bool force = false)
    {
        // ProviderAttempts must be loaded — see ResetEnrichmentBatch.
        var song = await db.Songs
            .Include(s => s.ProviderAttempts)
            .FirstOrDefaultAsync(s => s.Id == id);
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

        // Queue it now — see ResetEnrichmentBatch for why nothing else will.
        channel.Enqueue(song.Id, $"Reset — {song.FileName}");

        return Results.Ok(new
        {
            song.Id,
            song.FileName,
            song.EnrichmentStatus,
            song.LibraryBuildStatus,
            song.IsManuallyApproved,
            RestoredOriginalMetadata = restoreOriginalMetadata && song.OriginalMetadataCaptured,
            Message = "Song enrichment has been reset and queued for re-enrichment."
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

        SongFieldReverter.Apply(song, change.FieldName, change.OldValue);
        change.RevertedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return Results.Ok(new { song.Id, change.FieldName, revertedTo = change.OldValue });
    }

    /// <summary>
    /// The uniform not-found body for every song lookup, owned or shared.
    ///
    /// <para>
    /// This is a security control, not cosmetics. If "no such song" and "not shared with me" read
    /// differently, a member can walk the id space and map out the size and shape of a library
    /// they were never granted.
    /// </para>
    /// </summary>
    internal static IResult SongNotFound() =>
        Results.NotFound(new { message = "Song not found." });

    internal static async Task<IResult> StreamSong(
        int id, MusicHoarderDbContext db, ILibraryScopeResolver scopeResolver, CancellationToken ct)
    {
        var found = await scopeResolver.ResolveSongAsync(db, id, ct);
        // Paths only for a song the caller owns — for a granted row they are the grantor's.
        return found is null
            ? SongNotFound()
            : StreamSongFile(found.Value.Song, includePaths: found.Value.Slice.IsSelf);
    }

    /// <summary>
    /// Prefers the built destination copy (identical audio, but carries the corrected tags,
    /// embedded cover and lyrics — what players should surface); falls back to the source file
    /// for songs that haven't been built yet.
    /// </summary>
    internal static string? ResolveAudioFilePath(SongMetadata song) =>
        (!string.IsNullOrEmpty(song.DestinationPath) && File.Exists(song.DestinationPath)) ? song.DestinationPath :
        (!string.IsNullOrEmpty(song.SourcePath) && File.Exists(song.SourcePath)) ? song.SourcePath :
        null;

    /// <summary>
    /// Range-enabled audio stream for a song row the caller has already loaded and authorized
    /// (also used by the anonymous share endpoints, which do their own token-based scoping).
    /// </summary>
    /// <param name="includePaths">
    /// Whether the "file missing" body may name the paths. Defaults to FALSE so every caller is
    /// safe by omission — only pass true for a song the requester actually owns.
    ///
    /// <para>
    /// This matters because the same helper serves three callers: the caller's own library, an
    /// anonymous share link, and a grantee reading someone else's library. For the latter two the
    /// paths are the file owner's private disk layout, and a missing file is entirely routine (an
    /// unmounted NAS, or an artist/album grant exposing a never-built row). Leaking them here
    /// would undo the redaction <see cref="Contracts.SharedSongRowDto"/> performs — and the
    /// reflection test that pins that DTO cannot see this code path.
    /// </para>
    /// </param>
    internal static IResult StreamSongFile(SongMetadata song, bool includePaths = false)
    {
        var filePath = ResolveAudioFilePath(song);

        if (filePath is null)
            return includePaths
                ? Results.NotFound(new
                {
                    message = "Audio file not found on disk.",
                    sourcePath = song.SourcePath,
                    destinationPath = song.DestinationPath
                })
                : Results.NotFound(new { message = "Audio file not found on disk." });

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
        ILibraryScopeResolver scopeResolver,
        ICoverArtResolver coverArtResolver,
        ICoverThumbnailService thumbnails,
        HttpContext http,
        int? size,
        CancellationToken ct)
    {
        var found = await scopeResolver.ResolveSongAsync(db, id, ct);
        var song = found?.Song;

        // Synthetic (demo) rows have no real file on disk — nothing to resolve.
        if (song is null || song.IsSynthetic)
            return Results.NotFound();

        // A song with an attached music video keeps its YouTube thumbnail next to the mp4
        // (<stem>.jpg) — the fallback cover for artless downloads until real art arrives.
        //
        // Filter bypassed deliberately: SongMusicVideo is scoped by its parent song's owner, so an
        // ambient-filtered read returns nothing for a grantee and every shared track silently loses
        // its thumbnail fallback. Safe because the song was already authorized above.
        string? videoThumbnail = null;
        var videoFilePath = await db.SongMusicVideos.IgnoreQueryFilters().AsNoTracking()
            .Where(v => v.SongId == song.Id)
            .Select(v => v.FilePath)
            .FirstOrDefaultAsync(ct);
        if (videoFilePath is not null)
        {
            var candidate = Path.ChangeExtension(videoFilePath, ".jpg");
            if (File.Exists(candidate))
                videoThumbnail = candidate;
        }

        return await ServeCoverAsync(song, coverArtResolver, thumbnails, http, size, videoThumbnail);
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
        int? size,
        string? musicVideoThumbnailPath = null)
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

        // Last resort: the music video's YouTube thumbnail (real art always wins when present).
        if (cover is null && musicVideoThumbnailPath is not null)
            cover = new ResolvedCover { FilePath = musicVideoThumbnailPath, ContentType = "image/jpeg" };

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

    internal static string[] DescribeReasons(DuplicateMatchReason reasons)
    {
        var names = new List<string>(3);
        if (reasons.HasFlag(DuplicateMatchReason.ExactFingerprint)) names.Add("exact-fingerprint");
        if (reasons.HasFlag(DuplicateMatchReason.FingerprintSimilarity)) names.Add("fingerprint-similarity");
        if (reasons.HasFlag(DuplicateMatchReason.AcoustIdTrack)) names.Add("acoustid");
        if (reasons.HasFlag(DuplicateMatchReason.Isrc)) names.Add("isrc");
        if (reasons.HasFlag(DuplicateMatchReason.Metadata)) names.Add("metadata");
        return [.. names];
    }

    private static async Task<IResult> ListDuplicates(MusicHoarderDbContext db)
    {
        // The per-user query filter scopes links to the caller; groups are derived here by
        // union-find over Active links (there is no group entity).
        var links = await db.SongDuplicateLinks
            .AsNoTracking()
            .Where(l => l.Status == DuplicateLinkStatus.Active)
            .ToListAsync();

        var songIds = links
            .SelectMany(l => new[] { l.SongIdLow, l.SongIdHigh })
            .Distinct()
            .ToList();

        var songs = await db.Songs
            .AsNoTracking()
            .Where(s => songIds.Contains(s.Id) && s.DeletedAtUtc == null)
            .ToDictionaryAsync(s => s.Id);

        // Links referencing a soft-deleted song are stale until the next detection run; skip them.
        links = links.Where(l => songs.ContainsKey(l.SongIdLow) && songs.ContainsKey(l.SongIdHigh)).ToList();

        // Union-find over all active links: suspected pairs join the cluster too, so a group shows
        // its confirmed core plus any lower-confidence hangers-on in one card.
        var parent = new Dictionary<int, int>();
        int Find(int x)
        {
            if (!parent.TryGetValue(x, out var p)) { parent[x] = x; return x; }
            if (p == x) return x;
            var root = Find(p);
            parent[x] = root;
            return root;
        }
        foreach (var link in links)
        {
            var (ra, rb) = (Find(link.SongIdLow), Find(link.SongIdHigh));
            if (ra != rb) parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
        }

        var linksByCluster = links.ToLookup(l => Find(l.SongIdLow));

        var groups = new List<object>();
        var totalDuplicates = 0;

        foreach (var cluster in parent.Keys.ToList().GroupBy(Find).OrderBy(g => g.Key))
        {
            var members = cluster.Select(id => songs[id]).ToList();
            if (members.Count < 2)
                continue;

            var clusterLinks = linksByCluster[cluster.Key].ToList();
            var confirmedIds = clusterLinks
                .Where(l => l.Confidence == DuplicateConfidence.Confirmed)
                .SelectMany(l => new[] { l.SongIdLow, l.SongIdHigh })
                .ToHashSet();

            var ranked = IDuplicateDetectionService.RankKeeperFirst(members);
            var keeper = ranked[0];
            totalDuplicates += members.Count(m => m.IsDuplicate);

            var memberDtos = ranked.Select(m =>
            {
                var memberLinks = clusterLinks
                    .Where(l => l.SongIdLow == m.Id || l.SongIdHigh == m.Id)
                    .ToList();
                var reasons = memberLinks.Aggregate(DuplicateMatchReason.None, (acc, l) => acc | l.Reasons);
                var similarity = memberLinks.Max(l => l.Similarity);
                return new
                {
                    m.Id,
                    m.SourcePath,
                    m.FileName,
                    m.Extension,
                    m.FileSizeBytes,
                    m.Artist,
                    m.AlbumArtist,
                    m.Album,
                    m.Title,
                    m.Year,
                    m.TrackNumber,
                    m.DurationSeconds,
                    m.Bitrate,
                    m.Fingerprint,
                    m.IsDuplicate,
                    m.DuplicateOfId,
                    m.EnrichmentStatus,
                    m.DestinationPath,
                    IsBuilt = m.LibraryBuildStatus == LibraryBuildStatus.Done && m.DestinationPath != null,
                    IsKeeper = m.Id == keeper.Id,
                    IsPinned = m.DuplicateKeeperPinnedAtUtc != null,
                    Confidence = confirmedIds.Contains(m.Id) ? "confirmed" : "suspected",
                    Reasons = DescribeReasons(reasons),
                    Similarity = similarity,
                    QualityScore = IDuplicateDetectionService.QualityScore(m),
                };
            }).ToList();

            groups.Add(new
            {
                GroupId = cluster.Key,
                Confidence = confirmedIds.Count > 0 ? "confirmed" : "suspected",
                Keeper = memberDtos[0],
                Members = memberDtos,
            });
        }

        return Results.Ok(new
        {
            TotalDuplicates = totalDuplicates,
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

    /// <summary>Internal so the albums projection classifies releases from the same warnings.</summary>
    internal static string[]? DeserializeWarnings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<string[]>(json); }
        catch { return null; }
    }
}

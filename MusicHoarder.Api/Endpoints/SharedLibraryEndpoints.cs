using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Artwork;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Sharing;

namespace MusicHoarder.Api.Endpoints;

/// <summary>
/// What a friend reads: the songs the owner's <see cref="LibraryShareGrant"/> rows expose to the
/// calling account, plus per-song stream/cover/lyrics/video. Authenticated (deliberately NOT
/// allowlisted in <c>RequireAuthMiddleware</c>) but role-agnostic: resolution starts from the
/// caller's own grants, so an Owner or Demo session simply gets an empty result — no role filter
/// needed. All reads bypass the tenancy filter only after a grant resolved, and re-scope to the
/// granting owner's rows via <see cref="ISharedLibraryGrantResolver"/> — the same
/// "capability first, then re-scope" posture as the anonymous <see cref="SharesEndpoints"/>.
///
/// <para>
/// The songs projection is a deliberate subset of <c>ListSongs</c>' shape (key-compatible with
/// the frontend's <c>ApiSong</c>): no filesystem paths (the owner's disk layout stays private),
/// no pipeline internals, and no like/play fields (those are the owner's columns; per-friend
/// state is a later feature).
/// </para>
/// </summary>
public static class SharedLibraryEndpoints
{
    public static IEndpointRouteBuilder MapSharedLibraryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/shared").WithTags("SharedLibrary");

        group.MapGet("/songs", ListSharedSongs)
            .WithName("ListSharedSongs")
            .WithSummary("Every song shared with the calling account, across all granting owners.");
        group.MapGet("/songs/{id:int}/stream", StreamSharedLibrarySong)
            .WithName("StreamSharedLibrarySong");
        group.MapGet("/songs/{id:int}/cover", GetSharedLibrarySongCover)
            .WithName("GetSharedLibrarySongCover");
        group.MapGet("/songs/{id:int}/lyrics", GetSharedLibrarySongLyrics)
            .WithName("GetSharedLibrarySongLyrics");
        group.MapGet("/songs/{id:int}/video", GetSharedLibrarySongVideoInfo)
            .WithName("GetSharedLibrarySongVideoInfo");
        group.MapGet("/songs/{id:int}/video/stream", StreamSharedLibrarySongVideo)
            .WithName("StreamSharedLibrarySongVideo");

        // Per-friend listening state. These are the friend's own rows (FriendSongState), never
        // the owner's like/play columns — and they only accept songs the caller's grants expose.
        // FriendReadOnlyMiddleware allowlists /api/shared/ writes for exactly this reason.
        group.MapPost("/songs/{id:int}/like", LikeSharedSong)
            .WithName("LikeSharedSong")
            .WithSummary("Mark a shared song as liked for the calling account (idempotent).");
        group.MapDelete("/songs/{id:int}/like", UnlikeSharedSong)
            .WithName("UnlikeSharedSong")
            .WithSummary("Remove a shared song from the calling account's liked songs.");
        group.MapPost("/songs/{id:int}/played", ReportSharedSongPlayed)
            .WithName("ReportSharedSongPlayed")
            .WithSummary("Record a playback start of a shared song for the calling account.");

        return app;
    }

    internal static async Task<IResult> LikeSharedSong(
        int id,
        MusicHoarderDbContext db,
        ICurrentUserAccessor currentUser,
        ISharedLibraryGrantResolver resolver,
        CancellationToken ct)
    {
        var song = await ResolveSongAsync(db, currentUser, resolver, id, ct);
        if (song is null)
            return SharedSongNotFound();

        var state = await UpsertStateAsync(db, currentUser.UserId, id, s => s.LikedAtUtc ??= DateTime.UtcNow, ct);
        return Results.Ok(new { Id = id, state.LikedAtUtc });
    }

    internal static async Task<IResult> UnlikeSharedSong(
        int id,
        MusicHoarderDbContext db,
        ICurrentUserAccessor currentUser,
        ISharedLibraryGrantResolver resolver,
        CancellationToken ct)
    {
        var song = await ResolveSongAsync(db, currentUser, resolver, id, ct);
        if (song is null)
            return SharedSongNotFound();

        var state = await db.FriendSongStates
            .FirstOrDefaultAsync(s => s.UserId == currentUser.UserId && s.SongId == id, ct);
        if (state is not null)
        {
            state.LikedAtUtc = null;
            await db.SaveChangesAsync(ct);
        }
        return Results.Ok(new { Id = id, LikedAtUtc = (DateTime?)null });
    }

    internal static async Task<IResult> ReportSharedSongPlayed(
        int id,
        MusicHoarderDbContext db,
        ICurrentUserAccessor currentUser,
        ISharedLibraryGrantResolver resolver,
        CancellationToken ct)
    {
        var song = await ResolveSongAsync(db, currentUser, resolver, id, ct);
        if (song is null)
            return SharedSongNotFound();

        var state = await UpsertStateAsync(db, currentUser.UserId, id, s =>
        {
            s.PlayCount++;
            s.LastPlayedAtUtc = DateTime.UtcNow;
        }, ct);
        return Results.Ok(new { Id = id, state.PlayCount, state.LastPlayedAtUtc });
    }

    /// <summary>
    /// Load-or-create the caller's state row for a song, retrying once if a concurrent first
    /// write wins the unique (UserId, SongId) race.
    /// </summary>
    private static async Task<FriendSongState> UpsertStateAsync(
        MusicHoarderDbContext db,
        Guid userId,
        int songId,
        Action<FriendSongState> mutate,
        CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var state = await db.FriendSongStates
                .FirstOrDefaultAsync(s => s.UserId == userId && s.SongId == songId, ct);
            var isNew = state is null;
            state ??= new FriendSongState { UserId = userId, SongId = songId };
            mutate(state);
            if (isNew) db.FriendSongStates.Add(state);

            try
            {
                await db.SaveChangesAsync(ct);
                return state;
            }
            catch (DbUpdateException) when (isNew && attempt == 0)
            {
                // Someone else inserted the row between our read and write; detach and re-read.
                db.Entry(state).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
            }
        }
    }

    internal static async Task<IResult> ListSharedSongs(
        MusicHoarderDbContext db,
        ICurrentUserAccessor currentUser,
        ISharedLibraryGrantResolver resolver,
        CancellationToken ct)
    {
        if (currentUser.User is null)
            return Results.Json(new { error = "unauthenticated" }, statusCode: 401);

        var sets = await resolver.ResolveAsync(db, currentUser.UserId, ct);

        // Materialized (not projected in SQL) because the lyric flags come from the computed
        // Display* properties, which have no column to translate to. Owners are disjoint, so
        // per-set queries can't produce cross-set duplicates.
        var rows = new List<SongMetadata>();
        foreach (var set in sets)
        {
            rows.AddRange(await resolver.ScopeSongs(db, set)
                .OrderBy(s => s.Artist ?? "")
                .ThenBy(s => s.Album ?? "")
                .ThenBy(s => s.DiscNumber ?? 1)
                .ThenBy(s => s.TrackNumber ?? 0)
                .ThenBy(s => s.Title ?? "")
                .ThenBy(s => s.FileName)
                .ToListAsync(ct));
        }

        var songIds = rows.Select(s => s.Id).ToList();
        var videoSongIds = songIds.Count == 0
            ? []
            : await db.SongMusicVideos.IgnoreQueryFilters().AsNoTracking()
                .Where(v => songIds.Contains(v.SongId))
                .Select(v => v.SongId)
                .ToHashSetAsync(ct);

        // The caller's own listening state (their FriendSongState rows — the query filter already
        // scopes to them). Owners/demo have none, matching their empty song list.
        var stateBySongId = songIds.Count == 0
            ? new Dictionary<int, FriendSongState>()
            : await db.FriendSongStates.AsNoTracking()
                .Where(f => f.UserId == currentUser.UserId && songIds.Contains(f.SongId))
                .ToDictionaryAsync(f => f.SongId, ct);

        var songs = rows.Select(s =>
        {
            var state = stateBySongId.GetValueOrDefault(s.Id);
            return new
            {
                s.Id,
                // ApiSong requires the field; the owner's real path is none of the friend's business.
                SourcePath = "",
                s.FileName,
                s.Extension,
                s.FileSizeBytes,
                s.Artist,
                s.Artists,
                s.AlbumArtist,
                s.Album,
                s.Title,
                s.Year,
                s.TrackNumber,
                s.DiscNumber,
                s.DurationSeconds,
                s.DurationMs,
                s.Bitrate,
                s.Genre,
                s.ReleaseDate,
                s.OriginalReleaseDate,
                s.Label,
                // Catalog identifiers so the metadata panel isn't a wall of dashes — public IDs,
                // not filesystem or pipeline internals.
                s.MusicBrainzId,
                s.MusicBrainzReleaseId,
                s.Isrc,
                s.SpotifyId,
                s.HasCoverArt,
                HasSyncedLyrics = !string.IsNullOrWhiteSpace(s.DisplaySyncedLyrics),
                HasPlainLyrics = !string.IsNullOrWhiteSpace(s.DisplayPlainLyrics),
                IsInstrumental = s.IsInstrumental == true,
                HasMusicVideo = videoSongIds.Contains(s.Id),
                s.IndexedAtUtc,
                s.AcquiredAtUtc,
                // Per-friend listening state, projected under the same keys the owner rows use so
                // the frontend's liked/recently-played features work unchanged.
                LikedAtUtc = state?.LikedAtUtc,
                PlayCount = state?.PlayCount ?? 0,
                LastPlayedAtUtc = state?.LastPlayedAtUtc,
            };
        }).ToList();

        return Results.Ok(new { Count = songs.Count, Songs = songs });
    }

    internal static async Task<IResult> StreamSharedLibrarySong(
        int id,
        MusicHoarderDbContext db,
        ICurrentUserAccessor currentUser,
        ISharedLibraryGrantResolver resolver,
        CancellationToken ct)
    {
        var song = await ResolveSongAsync(db, currentUser, resolver, id, ct);
        return song is null ? SharedSongNotFound() : SongsEndpoints.StreamSongFile(song);
    }

    internal static async Task<IResult> GetSharedLibrarySongCover(
        int id,
        MusicHoarderDbContext db,
        ICurrentUserAccessor currentUser,
        ISharedLibraryGrantResolver resolver,
        ICoverArtResolver coverArtResolver,
        ICoverThumbnailService thumbnails,
        HttpContext http,
        int? size,
        CancellationToken ct)
    {
        var song = await ResolveSongAsync(db, currentUser, resolver, id, ct);
        if (song is null)
            return SharedSongNotFound();

        return await SongsEndpoints.ServeCoverAsync(song, coverArtResolver, thumbnails, http, size);
    }

    internal static async Task<IResult> GetSharedLibrarySongLyrics(
        int id,
        MusicHoarderDbContext db,
        ICurrentUserAccessor currentUser,
        ISharedLibraryGrantResolver resolver,
        CancellationToken ct)
    {
        var song = await ResolveSongAsync(db, currentUser, resolver, id, ct);
        if (song is null)
            return SharedSongNotFound();

        // Same shape and staleness rules as the anonymous share lyrics (SharesEndpoints): the
        // friend sees what the in-app viewer shows, and stale translations don't ride along.
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

    internal static async Task<IResult> GetSharedLibrarySongVideoInfo(
        int id,
        MusicHoarderDbContext db,
        ICurrentUserAccessor currentUser,
        ISharedLibraryGrantResolver resolver,
        CancellationToken ct)
    {
        var song = await ResolveSongAsync(db, currentUser, resolver, id, ct);
        if (song is null)
            return SharedSongNotFound();

        var video = await db.SongMusicVideos.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(v => v.SongId == song.Id, ct);
        return video is null
            ? Results.NotFound(new { message = "No music video for this song." })
            : Results.Ok(MusicVideoEndpoints.ToDto(video));
    }

    internal static async Task<IResult> StreamSharedLibrarySongVideo(
        int id,
        MusicHoarderDbContext db,
        ICurrentUserAccessor currentUser,
        ISharedLibraryGrantResolver resolver,
        CancellationToken ct)
    {
        var song = await ResolveSongAsync(db, currentUser, resolver, id, ct);
        if (song is null)
            return SharedSongNotFound();

        var video = await db.SongMusicVideos.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(v => v.SongId == song.Id, ct);
        return MusicVideoEndpoints.StreamVideoFile(video);
    }

    private static async Task<SongMetadata?> ResolveSongAsync(
        MusicHoarderDbContext db,
        ICurrentUserAccessor currentUser,
        ISharedLibraryGrantResolver resolver,
        int songId,
        CancellationToken ct)
    {
        if (currentUser.User is null) return null;
        return await resolver.ResolveSongAsync(db, currentUser.UserId, songId, ct);
    }

    /// <summary>Uniform 404 for unknown, revoked-out-of-scope, and never-shared ids — no probing oracle.</summary>
    private static IResult SharedSongNotFound() =>
        Results.NotFound(new { message = "This song is not shared with you." });
}

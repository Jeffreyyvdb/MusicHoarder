using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Artwork;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Contracts;
using MusicHoarder.Api.Navidrome;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Sharing;
using MusicHoarder.Api.Sync;

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
/// <summary>Logger category for the deprecated <c>/api/shared</c> surface, so its remaining
/// traffic can be watched in one place and the routes deleted once it goes quiet.</summary>
public sealed class DeprecatedSharedApi;

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

        // Per-friend listening state. These are the friend's own rows (UserSongState), never
        // the owner's like/play columns — and they only accept songs the caller's grants expose.
        // MemberWriteGuardMiddleware allowlists /api/shared/ writes for exactly this reason.
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

    // DEPRECATED write aliases. They delegate to the unified handlers rather than reimplementing
    // the branch, so there is exactly one place that decides "own row or UserSongState row".
    //
    // The enqueuers are passed through for real, NOT because they are unreachable: an admin hitting
    // this route for a song they own takes the self branch and does enqueue to Navidrome and
    // instance sync. That is correct — it is the same behaviour as /songs/{id}/like — but it is a
    // behaviour change from `main`, where this route 404'd for an owned song. In practice only the
    // deprecated clients call it, and they are member sessions.

    internal static Task<IResult> LikeSharedSong(
        int id,
        MusicHoarderDbContext db,
        ILibraryScopeResolver scopeResolver,
        ICurrentUserAccessor currentUser,
        INavidromeLikeEnqueuer navidrome,
        ITrackSyncEnqueuer trackSync,
        CancellationToken ct) =>
        SongsEndpoints.LikeSong(id, db, scopeResolver, currentUser, navidrome, trackSync, ct);

    internal static Task<IResult> UnlikeSharedSong(
        int id,
        MusicHoarderDbContext db,
        ILibraryScopeResolver scopeResolver,
        ICurrentUserAccessor currentUser,
        INavidromeLikeEnqueuer navidrome,
        ITrackSyncEnqueuer trackSync,
        CancellationToken ct) =>
        SongsEndpoints.UnlikeSong(id, db, scopeResolver, currentUser, navidrome, trackSync, ct);

    internal static Task<IResult> ReportSharedSongPlayed(
        int id,
        MusicHoarderDbContext db,
        ILibraryScopeResolver scopeResolver,
        ICurrentUserAccessor currentUser,
        CancellationToken ct) =>
        SongsEndpoints.ReportPlayed(id, db, scopeResolver, currentUser, ct);


    /// <summary>
    /// DEPRECATED. A thin alias over the same projection the unified <c>GET /songs</c> uses.
    ///
    /// <para>
    /// Kept for one release because shipped Android builds call it, and their error handling
    /// deliberately does not treat 403 as "unpair" — dropping the route would leave those installs
    /// on a permanently empty library with no prompt to re-pair. Emits the OLD flat shape (no
    /// <c>grantors</c> array) so an old client sees exactly what it saw before.
    /// </para>
    /// </summary>
    internal static async Task<IResult> ListSharedSongs(
        MusicHoarderDbContext db,
        ICurrentUserAccessor currentUser,
        ILibraryScopeResolver scopeResolver,
        ILogger<DeprecatedSharedApi> log,
        HttpContext http,
        CancellationToken ct)
    {
        if (currentUser.User is null)
            return Results.Json(new { error = "unauthenticated" }, statusCode: 401);

        log.LogInformation(
            "Deprecated /api/shared/songs hit by {UserAgent}",
            http.Request.Headers.UserAgent.ToString());

        var scope = await scopeResolver.ResolveAsync(db, ct);
        var (songs, _) = await SharedSongProjection.BuildAsync(db, scope, currentUser.UserId, ct);

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
            // The AI disclosure is a property of the words, not of who is reading them, so a grantee
            // sees exactly the label the owner sees.
            LyricsProvenance = song.LyricsProvenance.ToString(),
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

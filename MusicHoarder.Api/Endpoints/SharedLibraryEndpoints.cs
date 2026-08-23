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
        group.MapGet("/songs/{id:int}/video/stream", StreamSharedLibrarySongVideo)
            .WithName("StreamSharedLibrarySongVideo");

        return app;
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

        var songs = rows.Select(s => new
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
            s.Genre,
            s.ReleaseDate,
            s.Label,
            s.HasCoverArt,
            HasSyncedLyrics = !string.IsNullOrWhiteSpace(s.DisplaySyncedLyrics),
            HasPlainLyrics = !string.IsNullOrWhiteSpace(s.DisplayPlainLyrics),
            IsInstrumental = s.IsInstrumental == true,
            HasMusicVideo = videoSongIds.Contains(s.Id),
            s.AcquiredAtUtc,
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

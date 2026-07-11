using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Artwork;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Endpoints;

/// <summary>
/// Shareable song/album links. Two surfaces with very different auth postures:
///
/// <c>/api/shares</c> (owner-only) — create, list, and revoke share links.
///
/// <c>/api/share/{token}</c> (anonymous — allowlisted in <c>RequireAuthMiddleware</c>) — what the
/// link recipient hits. The token is the whole capability: every handler resolves it to a
/// <see cref="SongShare"/> row via <c>IgnoreQueryFilters()</c> (an anonymous request's EF filter
/// would return zero rows) and then re-scopes every song read to the share's own
/// <c>OwnerUserId</c> + song/album membership, so a token can never reach beyond what was shared.
/// </summary>
public static class SharesEndpoints
{
    public static IEndpointRouteBuilder MapSharesEndpoints(this IEndpointRouteBuilder app)
    {
        var owner = app.MapGroup("/api/shares").WithTags("Shares").RequireOwner();
        owner.MapPost("", CreateShare)
            .WithName("CreateShare")
            .WithSummary("Create (or return the existing) share link for a song or its whole album.");
        owner.MapGet("", ListShares)
            .WithName("ListShares")
            .WithSummary("List the owner's active share links.");
        owner.MapDelete("/{id:int}", RevokeShare)
            .WithName("RevokeShare")
            .WithSummary("Revoke a share link; the URL stops working immediately.");

        var pub = app.MapGroup("/api/share").WithTags("Shares");
        pub.MapGet("/{token}", GetSharePayload)
            .WithName("GetSharePayload")
            .WithSummary("Anonymous: the shared song/album's playable tracklist and display metadata.");
        pub.MapGet("/{token}/songs/{id:int}/stream", StreamSharedSong)
            .WithName("StreamSharedSong");
        pub.MapGet("/{token}/songs/{id:int}/cover", GetSharedSongCover)
            .WithName("GetSharedSongCover");
        pub.MapGet("/{token}/songs/{id:int}/lyrics", GetSharedSongLyrics)
            .WithName("GetSharedSongLyrics");

        return app;
    }

    public sealed record CreateShareRequest(int SongId, string? Scope);

    internal static async Task<IResult> CreateShare(
        CreateShareRequest body,
        MusicHoarderDbContext db,
        ICurrentUserAccessor currentUser,
        CancellationToken ct)
    {
        var scope = string.Equals(body.Scope, "album", StringComparison.OrdinalIgnoreCase)
            ? ShareScope.Album
            : ShareScope.Song;

        // Query filter scopes to the current user, so this doubles as the ownership check.
        var song = await db.Songs.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == body.SongId && s.DeletedAtUtc == null, ct);
        if (song is null)
            return Results.NotFound(new { message = $"Song with id {body.SongId} not found." });

        // Re-sharing the same thing hands back the same link instead of minting token sprawl.
        var existing = await db.SongShares.AsNoTracking()
            .FirstOrDefaultAsync(s => s.SongId == body.SongId && s.Scope == scope && s.RevokedAtUtc == null, ct);
        if (existing is not null)
            return Results.Ok(ToShareView(existing, song));

        var share = new SongShare
        {
            OwnerUserId = currentUser.UserId,
            SongId = body.SongId,
            Token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(16)),
            Scope = scope,
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.SongShares.Add(share);
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToShareView(share, song));
    }

    internal static async Task<IResult> ListShares(MusicHoarderDbContext db, CancellationToken ct)
    {
        var shares = await db.SongShares.AsNoTracking()
            .Where(s => s.RevokedAtUtc == null)
            .OrderByDescending(s => s.CreatedAtUtc)
            .Select(s => new
            {
                s.Id,
                s.Token,
                Scope = s.Scope.ToString(),
                s.SongId,
                s.CreatedAtUtc,
                Title = s.Song!.Title ?? s.Song.FileName,
                s.Song.Artist,
                s.Song.Album,
            })
            .ToListAsync(ct);

        return Results.Ok(shares);
    }

    internal static async Task<IResult> RevokeShare(int id, MusicHoarderDbContext db, CancellationToken ct)
    {
        var share = await db.SongShares.FirstOrDefaultAsync(s => s.Id == id && s.RevokedAtUtc == null, ct);
        if (share is null)
            return Results.NotFound(new { message = $"Share with id {id} not found." });

        share.RevokedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static object ToShareView(SongShare share, SongMetadata song) => new
    {
        share.Id,
        share.Token,
        Scope = share.Scope.ToString(),
        share.SongId,
        share.CreatedAtUtc,
        Title = song.Title ?? song.FileName,
        song.Artist,
        song.Album,
    };

    // ── Anonymous surface ───────────────────────────────────────────────────────────────────

    internal static async Task<IResult> GetSharePayload(string token, MusicHoarderDbContext db, CancellationToken ct)
    {
        var share = await ResolveShareAsync(db, token, ct);
        if (share is null)
            return ShareNotFound();

        var songs = await LoadSongsInScopeAsync(db, share, ct);
        if (songs.Count == 0)
            return ShareNotFound();

        var shared = songs.First(s => s.Id == share.SongId);

        return Results.Ok(new
        {
            Scope = share.Scope.ToString(),
            SharedSongId = share.SongId,
            Album = new
            {
                Title = shared.Album,
                Artist = shared.AlbumArtist ?? shared.Artist,
                shared.Year,
            },
            Tracks = songs.Select(s => new
            {
                s.Id,
                Title = s.Title ?? s.FileName,
                s.Artist,
                s.TrackNumber,
                s.DiscNumber,
                DurationMs = s.DurationMs ?? s.DurationSeconds * 1000,
                s.HasCoverArt,
                HasSyncedLyrics = !string.IsNullOrWhiteSpace(s.EffectiveSyncedLyrics),
                HasPlainLyrics = !string.IsNullOrWhiteSpace(s.EffectivePlainLyrics),
                IsInstrumental = s.IsInstrumental == true,
            }),
        });
    }

    internal static async Task<IResult> StreamSharedSong(string token, int id, MusicHoarderDbContext db, CancellationToken ct)
    {
        var song = await ResolveSongInScopeAsync(db, token, id, ct);
        return song is null ? ShareNotFound() : SongsEndpoints.StreamSongFile(song);
    }

    internal static async Task<IResult> GetSharedSongCover(
        string token,
        int id,
        MusicHoarderDbContext db,
        ICoverArtResolver coverArtResolver,
        ICoverThumbnailService thumbnails,
        HttpContext http,
        int? size,
        CancellationToken ct)
    {
        var song = await ResolveSongInScopeAsync(db, token, id, ct);
        if (song is null)
            return ShareNotFound();

        return await SongsEndpoints.ServeCoverAsync(song, coverArtResolver, thumbnails, http, size);
    }

    internal static async Task<IResult> GetSharedSongLyrics(string token, int id, MusicHoarderDbContext db, CancellationToken ct)
    {
        var song = await ResolveSongInScopeAsync(db, token, id, ct);
        if (song is null)
            return ShareNotFound();

        return Results.Ok(new
        {
            song.Id,
            Synced = song.EffectiveSyncedLyrics,
            Plain = song.EffectivePlainLyrics,
            IsInstrumental = song.IsInstrumental == true,
        });
    }

    /// <summary>Uniform 404 for unknown, revoked, and out-of-scope requests — no oracle for probing.</summary>
    private static IResult ShareNotFound() =>
        Results.NotFound(new { message = "This share link does not exist or has been revoked." });

    private static async Task<SongShare?> ResolveShareAsync(MusicHoarderDbContext db, string token, CancellationToken ct) =>
        await db.SongShares.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(s => s.Token == token && s.RevokedAtUtc == null, ct);

    /// <summary>
    /// Every song the share's token may touch, in tracklist order. Song scope is just the shared
    /// row; Album scope is the owner's tracks carrying the same (album-artist, album) tags — the
    /// same grouping the album pages use. Year is deliberately not matched: per-track enrichment
    /// can leave one album's tracks with inconsistent years (see CLAUDE.md).
    /// </summary>
    private static async Task<List<SongMetadata>> LoadSongsInScopeAsync(
        MusicHoarderDbContext db,
        SongShare share,
        CancellationToken ct)
    {
        var shared = await db.Songs.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.Id == share.SongId && s.OwnerUserId == share.OwnerUserId && s.DeletedAtUtc == null,
                ct);
        if (shared is null)
            return [];

        if (share.Scope == ShareScope.Song || string.IsNullOrWhiteSpace(shared.Album))
            return [shared];

        var albumLower = shared.Album!.ToLower();
        var artistLower = ((shared.AlbumArtist ?? shared.Artist) ?? "").ToLower();

        var tracks = await db.Songs.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.OwnerUserId == share.OwnerUserId
                && s.DeletedAtUtc == null
                && !s.IsDuplicate
                && s.Album != null && s.Album.ToLower() == albumLower
                && ((s.AlbumArtist ?? s.Artist) ?? "").ToLower() == artistLower)
            .ToListAsync(ct);

        // The shared song must stay reachable even if it is itself flagged a duplicate.
        if (tracks.All(s => s.Id != shared.Id))
            tracks.Add(shared);

        return tracks
            .OrderBy(s => s.DiscNumber ?? 1)
            .ThenBy(s => s.TrackNumber ?? int.MaxValue)
            .ThenBy(s => s.Title)
            .ToList();
    }

    private static async Task<SongMetadata?> ResolveSongInScopeAsync(
        MusicHoarderDbContext db,
        string token,
        int songId,
        CancellationToken ct)
    {
        var share = await ResolveShareAsync(db, token, ct);
        if (share is null)
            return null;

        var songs = await LoadSongsInScopeAsync(db, share, ct);
        return songs.FirstOrDefault(s => s.Id == songId);
    }
}

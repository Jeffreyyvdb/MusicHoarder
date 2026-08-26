using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Contracts;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Sharing;

namespace MusicHoarder.Api.Endpoints;

public static class RadioEndpoints
{
    /// <summary>Ids the caller sends per request, capped so the query string stays sane.</summary>
    private const int MaxExcludedIds = 400;

    private const int DefaultLimit = 20;
    private const int MaxLimit = 50;

    public static IEndpointRouteBuilder MapRadioEndpoints(this IEndpointRouteBuilder app)
    {
        // A GET, and that is load-bearing rather than RESTful taste: MemberWriteGuardMiddleware
        // rejects every unsafe verb that is not explicitly allowlisted, and a member is exactly the
        // account this has to keep working. Reads need no allowlist entry.
        //
        // No RequireAdmin, matching the other library GETs: the demo account browses too, auth is
        // still mandatory via RequireAuthMiddleware, and every row is scoped below.
        app.MapGet("/api/radio", GetRadio)
            .WithName("GetRadio")
            .WithSummary("What to play after the queue runs dry: song ids ordered by similarity to a seed track, so playback continues instead of stopping.")
            .WithTags("Library");

        return app;
    }

    /// <summary>
    /// The playback continuation. Returns ids rather than rows because both clients already hold
    /// the whole <c>GET /songs</c> dump and join against it — the same contract
    /// <c>GET /api/albums</c> uses for its track ids, and one that cannot widen a grantee's
    /// published surface by accident.
    /// </summary>
    /// <param name="seedSongId">The track the station is built from — normally the one that just ended.</param>
    /// <param name="exclude">
    /// Comma-separated ids already queued or just played, so a station does not repeat itself.
    /// Only the first <see cref="MaxExcludedIds"/> are honoured.
    /// </param>
    /// <param name="limit">How many ids to return (1..<see cref="MaxLimit"/>).</param>
    internal static async Task<IResult> GetRadio(
        MusicHoarderDbContext db,
        ILibraryScopeResolver scopeResolver,
        CancellationToken ct,
        int seedSongId,
        string? exclude = null,
        int limit = DefaultLimit)
    {
        limit = Math.Clamp(limit, 1, MaxLimit);

        var scope = await scopeResolver.ResolveAsync(db, ct);

        var candidates = new List<RadioTrackRow>();
        candidates.AddRange(await OwnRowsAsync(db, scope, ct));

        var (sharedSongs, _) =
            await SharedSongProjection.BuildAsync(db, scope, scope.Slices[0].GrantorUserId, ct);
        candidates.AddRange(sharedSongs.Select(FromSharedRow));

        // The seed is taken from the candidate list rather than looked up separately, so a seed the
        // caller only holds through a grant is read through the shared surface too. A seed they may
        // not read is indistinguishable from one that does not exist — song ids must not enumerate.
        var seed = candidates.FirstOrDefault(c => c.Id == seedSongId);
        if (seed is null)
            return Results.NotFound(new { error = "Song not found." });

        var excluded = ParseExcluded(exclude);
        var songIds = RadioRanker.Rank(seed, candidates, excluded, limit, DateTime.UtcNow);

        return Results.Ok(new { SeedSongId = seedSongId, Count = songIds.Count, SongIds = songIds });
    }

    private static HashSet<int> ParseExcluded(string? exclude)
    {
        var ids = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(exclude)) return ids;

        foreach (var part in exclude.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (ids.Count >= MaxExcludedIds) break;
            if (int.TryParse(part, out var id)) ids.Add(id);
        }
        return ids;
    }

    /// <summary>
    /// The caller's own rows. Duplicates are dropped — a confirmed duplicate is the same recording
    /// the keeper already offers, and a station that plays both sounds broken.
    /// </summary>
    private static async Task<List<RadioTrackRow>> OwnRowsAsync(
        MusicHoarderDbContext db, ILibraryScope scope, CancellationToken ct) =>
        await scope.SongsFor(db, scope.Slices[0])
            .AsNoTracking()
            .Where(s => s.DeletedAtUtc == null && !s.IsDuplicate)
            .Select(s => new RadioTrackRow(
                s.Id,
                s.Artist,
                s.AlbumArtist,
                s.Artists,
                s.ArtistMusicBrainzIds,
                s.Album,
                s.Genre,
                s.Label,
                s.Year,
                s.DurationSeconds,
                s.PlayCount,
                s.LikedAtUtc,
                s.LastPlayedAtUtc,
                s.LibraryBuildStatus == LibraryBuildStatus.Done && s.DestinationPath != null))
            .ToListAsync(ct);

    /// <summary>
    /// A row shared with the caller, read through nothing but what <see cref="SharedSongRowDto"/>
    /// publishes — the same discipline <c>GET /api/albums</c> applies. The null is the point:
    /// artist MBIDs are not on that surface, so a granted row is matched on names alone.
    /// The like and play fields are already the caller's own state, not the grantor's.
    /// </summary>
    private static RadioTrackRow FromSharedRow(SharedSongRowDto row) => new(
        row.Id,
        row.Artist,
        row.AlbumArtist,
        row.Artists,
        ArtistMusicBrainzIds: null,
        row.Album,
        row.Genre,
        row.Label,
        row.Year,
        row.DurationSeconds,
        row.PlayCount,
        row.LikedAtUtc,
        row.LastPlayedAtUtc,
        row.IsBuilt);
}

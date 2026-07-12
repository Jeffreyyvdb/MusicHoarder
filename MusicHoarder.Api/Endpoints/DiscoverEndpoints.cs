using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Discover;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Spotify;

namespace MusicHoarder.Api.Endpoints;

/// <summary>
/// Editorial "Discover" browse over Deezer's free public API (Spotify permanently 404s editorial playlists
/// for personal API apps). Reads are authenticated but demo-allowed (no <c>RequireOwner</c>); only the
/// subscribe-by-URL resolve is owner-only.
/// </summary>
public static class DiscoverEndpoints
{
    public static IEndpointRouteBuilder MapDiscoverEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/discover").WithTags("Discover");

        group.MapGet("/genres", async (IDeezerCatalogService deezer, CancellationToken ct) =>
            {
                var genres = await deezer.GetGenresAsync(ct);
                var dtos = genres.Select(g => new DiscoverGenreDto(g.Id, g.Name, g.PictureUrl)).ToList();
                return Results.Ok(new { genres = dtos });
            })
            .WithName("DiscoverGenres")
            .WithSummary("Deezer editorial genres for the discover browser.");

        group.MapGet("/playlists", async (
                int? genreId,
                string? search,
                int? limit,
                IDeezerCatalogService deezer,
                IOptions<MusicEnricherOptions> options,
                MusicHoarderDbContext db,
                CancellationToken ct) =>
            {
                var take = Math.Clamp(limit ?? options.Value.DeezerBrowsePlaylistLimit, 1, 100);

                IReadOnlyList<DeezerPlaylistSummary> playlists;
                if (!string.IsNullOrWhiteSpace(search))
                    playlists = await deezer.SearchPlaylistsAsync(search, take, ct);
                else
                    playlists = await deezer.GetChartPlaylistsAsync(genreId, take, ct);

                var subs = await LoadDeezerSubscriptionsAsync(db, playlists.Select(p => p.Id).ToList(), ct);
                var dtos = playlists.Select(p => ToSummaryDto(p, subs)).ToList();
                return Results.Ok(new { playlists = dtos });
            })
            .WithName("DiscoverPlaylists")
            .WithSummary("Chart or search playlists from Deezer, annotated with the owner's subscriptions.");

        group.MapGet("/playlists/{id}", async (
                string id,
                IDeezerCatalogService deezer,
                IOptions<MusicEnricherOptions> options,
                IMemoryCache cache,
                ICurrentUserAccessor currentUser,
                MusicHoarderDbContext db,
                CancellationToken ct) =>
            {
                var playlist = await deezer.GetPlaylistAsync(id, ct);
                if (playlist is null)
                    return Results.NotFound(new { error = "not_found", message = $"Deezer playlist '{id}' was not found." });

                // Cap the tracklist paged in the request path; the playlist's real total stays in trackCount.
                var tracks = (await deezer.GetPlaylistTracksAsync(id, options.Value.DiscoverDetailTrackLimit, ct)).Tracks;

                var subs = await LoadDeezerSubscriptionsAsync(db, [playlist.Id], ct);

                // inLibrary: cheap normalized artist+title membership over the caller's non-deleted songs
                // (browse tracks carry no ISRC, so ISRC matching doesn't apply here). Cached ~60s per owner
                // so repeated detail views don't rescan the whole library each time.
                var libraryKeys = await GetLibraryMembershipAsync(db, cache, currentUser.UserId, ct);

                // inWishlist: the caller's wishlist items keyed by Deezer track id.
                var trackIds = tracks.Select(t => t.Id).ToList();
                var wishlisted = new HashSet<string>(
                    await db.WishlistItems
                        .AsNoTracking()
                        .Where(w => w.DeezerTrackId != null && trackIds.Contains(w.DeezerTrackId))
                        .Select(w => w.DeezerTrackId!)
                        .ToListAsync(ct),
                    StringComparer.Ordinal);

                var trackDtos = tracks.Select(t => new DiscoverTrackDto(
                    t.Id,
                    t.Title,
                    t.Artist,
                    t.Album,
                    t.DurationMs == 0 ? null : t.DurationMs,
                    t.CoverUrl,
                    libraryKeys.Contains(LibraryKey(t.Artist, t.Title)),
                    wishlisted.Contains(t.Id))).ToList();

                return Results.Ok(new
                {
                    playlist = ToSummaryDto(playlist, subs),
                    tracks = trackDtos,
                });
            })
            .WithName("DiscoverPlaylistDetail")
            .WithSummary("A discover playlist's metadata and tracklist, annotated with library/wishlist state.");

        group.MapPost("/resolve", async (
                ResolvePlaylistRequest body,
                IDeezerCatalogService deezer,
                ISpotifyApiService spotifyApi,
                MusicHoarderDbContext db,
                CancellationToken ct) =>
            {
                if (!PlaylistUrlParser.TryParse(body.Url, out var provider, out var playlistId))
                    return Results.BadRequest(new
                    {
                        error = "invalid_url",
                        message = "Could not recognize a Spotify or Deezer playlist link. Paste a playlist URL or id.",
                    });

                if (provider == PlaylistUrlParser.Spotify)
                {
                    var lookup = await spotifyApi.GetPlaylistAsync(playlistId, ct);
                    if (!lookup.Found || lookup.Playlist is null)
                        return Results.UnprocessableEntity(new
                        {
                            error = "spotify_editorial_blocked",
                            message = "Spotify blocks editorial and algorithmic playlists for personal API apps. Search for an equivalent playlist on the Discover page instead.",
                        });

                    var subscribed = await db.WishlistSources
                        .AnyAsync(s => s.SourceType == WishlistSourceType.Playlist && s.SpotifyPlaylistId == playlistId, ct);
                    var p = lookup.Playlist;
                    return Results.Ok(new ResolvePlaylistResponse(
                        PlaylistUrlParser.Spotify, p.SpotifyId, p.Name, p.ImageUrl, p.TrackCount, subscribed));
                }

                var deezerPlaylist = await deezer.GetPlaylistAsync(playlistId, ct);
                if (deezerPlaylist is null)
                    return Results.NotFound(new
                    {
                        error = "not_found",
                        message = $"Deezer playlist '{playlistId}' was not found.",
                    });

                var deezerSubscribed = await db.WishlistSources
                    .AnyAsync(s => s.SourceType == WishlistSourceType.DeezerPlaylist && s.DeezerPlaylistId == playlistId, ct);
                return Results.Ok(new ResolvePlaylistResponse(
                    PlaylistUrlParser.Deezer, deezerPlaylist.Id, deezerPlaylist.Title,
                    deezerPlaylist.CoverUrl, deezerPlaylist.TrackCount, deezerSubscribed));
            })
            .WithName("DiscoverResolve")
            .WithSummary("Resolve a pasted Spotify/Deezer playlist URL or id into subscribe-ready metadata.")
            .RequireOwner();

        return app;
    }

    /// <summary>
    /// The caller's normalized (artist,title) library membership set, cached ~60s per owner so repeated
    /// detail views don't rescan the whole Songs table. The query is owner-scoped by the multi-tenant
    /// filter, so the cache is keyed by owner id (demo included). Only the two columns are selected.
    /// </summary>
    private static async Task<HashSet<string>> GetLibraryMembershipAsync(
        MusicHoarderDbContext db, IMemoryCache cache, Guid ownerId, CancellationToken ct)
    {
        var cacheKey = $"discover_library_membership:{ownerId}";
        if (cache.TryGetValue(cacheKey, out HashSet<string>? cached) && cached is not null)
            return cached;

        var librarySongs = await db.Songs
            .AsNoTracking()
            .Where(s => s.DeletedAtUtc == null)
            .Select(s => new { s.Artist, s.Title })
            .ToListAsync(ct);
        var keys = new HashSet<string>(
            librarySongs.Select(s => LibraryKey(s.Artist, s.Title)), StringComparer.Ordinal);

        cache.Set(cacheKey, keys, TimeSpan.FromSeconds(60));
        return keys;
    }

    /// <summary>The caller's Deezer subscriptions keyed by Deezer playlist id (query-filter scoped to the caller).</summary>
    private static async Task<Dictionary<string, (int SourceId, bool AutoSync)>> LoadDeezerSubscriptionsAsync(
        MusicHoarderDbContext db, IReadOnlyList<string> playlistIds, CancellationToken ct)
    {
        if (playlistIds.Count == 0)
            return new Dictionary<string, (int, bool)>(StringComparer.Ordinal);

        var rows = await db.WishlistSources
            .AsNoTracking()
            .Where(s => s.SourceType == WishlistSourceType.DeezerPlaylist
                && s.DeezerPlaylistId != null
                && playlistIds.Contains(s.DeezerPlaylistId))
            .Select(s => new { s.DeezerPlaylistId, s.Id, s.AutoSync })
            .ToListAsync(ct);

        var map = new Dictionary<string, (int, bool)>(StringComparer.Ordinal);
        foreach (var r in rows)
            map[r.DeezerPlaylistId!] = (r.Id, r.AutoSync);
        return map;
    }

    private static DiscoverPlaylistDto ToSummaryDto(
        DeezerPlaylistSummary p, IReadOnlyDictionary<string, (int SourceId, bool AutoSync)> subs)
    {
        var subscribed = subs.TryGetValue(p.Id, out var sub);
        return new DiscoverPlaylistDto(
            p.Id,
            p.Title,
            p.Description,
            p.CoverUrl,
            p.TrackCount,
            p.CreatorName,
            subscribed,
            subscribed ? sub.SourceId : null,
            subscribed ? sub.AutoSync : null);
    }

    private static string LibraryKey(string? artist, string? title) =>
        $"{TitleNormalizer.NormalizeForSearch(artist)}\0{TitleNormalizer.NormalizeForSearch(title)}";
}

public sealed record DiscoverGenreDto(long Id, string Name, string? PictureUrl);

public sealed record DiscoverPlaylistDto(
    string Id,
    string Title,
    string? Description,
    string? CoverUrl,
    int TrackCount,
    string? CreatorName,
    bool Subscribed,
    int? SourceId,
    bool? AutoSync);

public sealed record DiscoverTrackDto(
    string DeezerTrackId,
    string Title,
    string Artist,
    string? Album,
    int? DurationMs,
    string? CoverUrl,
    bool InLibrary,
    bool InWishlist);

public sealed record ResolvePlaylistRequest(string? Url);

public sealed record ResolvePlaylistResponse(
    string Provider,
    string PlaylistId,
    string Title,
    string? CoverUrl,
    int TrackCount,
    bool Subscribed);

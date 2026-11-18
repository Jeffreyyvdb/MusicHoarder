using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Discover;
using MusicHoarder.Api.Import;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Spotify;

namespace MusicHoarder.Api.Endpoints;

/// <summary>
/// "Discover": Spotify's own playlists, browsed from the public Spotify Playlist Archive (Spotify's Web API
/// permanently 404s editorial playlists for personal apps, the archive snapshots them daily). Reads are
/// authenticated but demo-allowed (no <c>RequireAdmin</c>); only the subscribe-by-URL resolve is owner-only.
/// Existing Deezer subscriptions still sync, and a pasted Deezer link still resolves.
/// </summary>
public static class DiscoverEndpoints
{
    /// <summary>The <c>provider</c> a resolved link answers with when the playlist came from the archive.</summary>
    public const string ArchiveProvider = "spotifyArchive";

    private static readonly TimeSpan GridCoverDeadline = TimeSpan.FromSeconds(2);

    public static IEndpointRouteBuilder MapDiscoverEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/discover").WithTags("Discover");

        group.MapGet("/playlists", async (
                string? search,
                int? limit,
                ISpotifyPlaylistArchive archive,
                IOptions<MusicEnricherOptions> options,
                MusicHoarderDbContext db,
                CancellationToken ct) =>
            {
                var take = Math.Clamp(limit ?? options.Value.DiscoverPlaylistLimit, 1, 100);

                var playlists = await archive.BrowseAsync(search, take, ct);
                if (playlists is null)
                    return ArchiveUnavailable();

                var subs = await LoadArchiveSubscriptionsAsync(db, playlists.Select(p => p.Id).ToList(), ct);
                var covers = await CoversWithinAsync(archive, playlists, GridCoverDeadline);
                var dtos = playlists.Select((p, i) => ToSummaryDto(p, covers[i], trackCount: null, subs)).ToList();
                return Results.Ok(new { playlists = dtos });
            })
            .WithName("DiscoverPlaylists")
            .WithSummary("Most followed Spotify playlists from the Spotify Playlist Archive, optionally searched, annotated with the owner's subscriptions.");

        group.MapGet("/playlists/{id}", async (
                string id,
                ISpotifyPlaylistArchive archive,
                IOptions<MusicEnricherOptions> options,
                IMemoryCache cache,
                ICurrentUserAccessor currentUser,
                MusicHoarderDbContext db,
                CancellationToken ct) =>
            {
                var archived = await archive.GetTracksAsync(id, ct);
                if (archived is null)
                    return Results.NotFound(new
                    {
                        error = "not_found",
                        message = $"Playlist '{id}' is not in the Spotify Playlist Archive, or the archive could not be read.",
                    });

                var playlist = archived.Playlist;
                // Cap the tracklist sent back; the playlist's real total stays in trackCount.
                var tracks = archived.Tracks.Take(options.Value.DiscoverDetailTrackLimit).ToList();

                var subs = await LoadArchiveSubscriptionsAsync(db, [playlist.Id], ct);
                var cover = await archive.GetCoverUrlAsync(playlist.Id, ct);

                // inLibrary: cheap normalized artist+title membership over the caller's non-deleted songs
                // (archived tracks carry no ISRC, so ISRC matching doesn't apply here). Cached ~60s per owner
                // so repeated detail views don't rescan the whole library each time.
                var libraryKeys = await GetLibraryMembershipAsync(db, cache, currentUser.UserId, ct);

                // inWishlist: the caller's wishlist items keyed by Spotify track id.
                var trackIds = tracks.Select(t => t.SpotifyTrackId).ToList();
                var wishlisted = new HashSet<string>(
                    await db.WishlistItems
                        .AsNoTracking()
                        .Where(w => w.SpotifyTrackId != null && trackIds.Contains(w.SpotifyTrackId))
                        .Select(w => w.SpotifyTrackId!)
                        .ToListAsync(ct),
                    StringComparer.Ordinal);

                var trackDtos = tracks.Select(t => new DiscoverTrackDto(
                    t.SpotifyTrackId,
                    t.Title,
                    ArchiveFormat.ArtistCredit(t.Artists),
                    t.Album,
                    t.DurationMs == 0 ? null : t.DurationMs,
                    IsInLibrary(libraryKeys, t),
                    wishlisted.Contains(t.SpotifyTrackId))).ToList();

                return Results.Ok(new
                {
                    playlist = ToSummaryDto(playlist, cover, archived.Tracks.Count, subs),
                    tracks = trackDtos,
                });
            })
            .WithName("DiscoverPlaylistDetail")
            .WithSummary("An archived Spotify playlist's metadata and tracklist, annotated with library/wishlist state.");

        group.MapPost("/resolve", async (
                ResolvePlaylistRequest body,
                IDeezerCatalogService deezer,
                ISpotifyApiService spotifyApi,
                ISpotifyPlaylistArchive archive,
                ILoggerFactory loggerFactory,
                IYouTubePlaylistReader youTubePlaylists,
                MusicHoarderDbContext db,
                CancellationToken ct) =>
            {
                if (!PlaylistUrlParser.TryParse(body.Url, out var provider, out var playlistId))
                    return Results.BadRequest(new
                    {
                        error = "invalid_url",
                        message = "Could not recognize a Spotify, Deezer or YouTube playlist link. Paste a playlist URL or id.",
                    });

                if (provider == PlaylistUrlParser.YouTube)
                {
                    // One page of the listing: the name, cover and video count, not every video.
                    var read = await youTubePlaylists.ReadAsync(playlistId, maxEntries: 1, ct);
                    if (!read.Ok)
                        return Results.UnprocessableEntity(new
                        {
                            error = "youtube_playlist_unreadable",
                            message = read.Hint ?? "Could not read that YouTube playlist. It may be private, or yt-dlp is unavailable.",
                            detail = read.Detail,
                        });

                    var youTube = read.Playlist!;
                    var youTubeSubscribed = await db.WishlistSources
                        .AnyAsync(s => s.SourceType == WishlistSourceType.YouTubePlaylist && s.YouTubePlaylistId == playlistId, ct);
                    return Results.Ok(new ResolvePlaylistResponse(
                        PlaylistUrlParser.YouTube, playlistId, youTube.Title, youTube.ThumbnailUrl,
                        youTube.VideoCount, youTubeSubscribed));
                }

                if (provider == PlaylistUrlParser.Spotify)
                    return await ResolveSpotifyAsync(
                        playlistId, spotifyApi, archive, db, loggerFactory.CreateLogger("Discover"), ct);

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
            .WithSummary("Resolve a pasted Spotify/Deezer playlist URL or id, or a YouTube playlist URL, into subscribe-ready metadata. A Spotify playlist the API cannot read resolves through the Spotify Playlist Archive.")
            .RequireAdmin();

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

    /// <summary>
    /// The grid's covers, as many as arrive before <paramref name="deadline"/>; the rest are null this time.
    /// The lookups run without the request's token so a late one still lands in the cover cache for the next load.
    /// </summary>
    private static async Task<string?[]> CoversWithinAsync(
        ISpotifyPlaylistArchive archive, IReadOnlyList<ArchivedPlaylist> playlists, TimeSpan deadline)
    {
        var lookups = playlists.Select(p => archive.GetCoverUrlAsync(p.Id, CancellationToken.None)).ToArray();
        await Task.WhenAny(Task.WhenAll(lookups), Task.Delay(deadline));
        return lookups.Select(t => t.IsCompletedSuccessfully ? t.Result : null).ToArray();
    }

    /// <summary>
    /// The caller's subscriptions to these Spotify playlists, keyed by playlist id (query-filter scoped to the
    /// caller). One subscribed through the live Spotify API counts too, so the grid never offers a second one.
    /// </summary>
    private static async Task<Dictionary<string, (int SourceId, bool AutoSync)>> LoadArchiveSubscriptionsAsync(
        MusicHoarderDbContext db, IReadOnlyList<string> playlistIds, CancellationToken ct)
    {
        if (playlistIds.Count == 0)
            return new Dictionary<string, (int, bool)>(StringComparer.Ordinal);

        var rows = await db.WishlistSources
            .AsNoTracking()
            .Where(s => (s.SourceType == WishlistSourceType.SpotifyArchivePlaylist || s.SourceType == WishlistSourceType.Playlist)
                && s.SpotifyPlaylistId != null
                && playlistIds.Contains(s.SpotifyPlaylistId))
            .Select(s => new { s.SpotifyPlaylistId, s.Id, s.AutoSync })
            .ToListAsync(ct);

        var map = new Dictionary<string, (int, bool)>(StringComparer.Ordinal);
        foreach (var r in rows)
            map[r.SpotifyPlaylistId!] = (r.Id, r.AutoSync);
        return map;
    }

    /// <summary>
    /// A pasted Spotify link: the live API first (it reads the caller's own playlists, which the archive does
    /// not hold), then the archive for the editorial playlists Spotify refuses to a personal app.
    /// </summary>
    internal static async Task<IResult> ResolveSpotifyAsync(
        string playlistId,
        ISpotifyApiService spotifyApi,
        ISpotifyPlaylistArchive archive,
        MusicHoarderDbContext db,
        ILogger logger,
        CancellationToken ct)
    {
        var live = await TryLookupSpotifyPlaylistAsync(spotifyApi, playlistId, logger, ct);
        if (live is { Found: true, Playlist: { } p })
        {
            var subscribed = await db.WishlistSources
                .AnyAsync(s => s.SourceType == WishlistSourceType.Playlist && s.SpotifyPlaylistId == playlistId, ct);
            return Results.Ok(new ResolvePlaylistResponse(
                PlaylistUrlParser.Spotify, p.SpotifyId, p.Name, p.ImageUrl, p.TrackCount, subscribed));
        }

        var archived = await archive.GetTracksAsync(playlistId, ct);
        if (archived is null)
            return live is null
                ? Results.UnprocessableEntity(new
                {
                    error = "spotify_unavailable",
                    message = "Spotify could not be asked about that playlist (connect Spotify, or try again shortly), and it is not in the Spotify Playlist Archive.",
                })
                : Results.UnprocessableEntity(new
                {
                    error = "spotify_editorial_blocked",
                    message = "Spotify does not let this app read that playlist, and it is not in the Spotify Playlist Archive. Search for it on the Discover page instead.",
                });

        var archiveSubscribed = await db.WishlistSources
            .AnyAsync(s => (s.SourceType == WishlistSourceType.SpotifyArchivePlaylist || s.SourceType == WishlistSourceType.Playlist)
                && s.SpotifyPlaylistId == archived.Playlist.Id, ct);
        return Results.Ok(new ResolvePlaylistResponse(
            ArchiveProvider, archived.Playlist.Id, archived.Playlist.Name,
            await archive.GetCoverUrlAsync(archived.Playlist.Id, ct), archived.Tracks.Count, archiveSubscribed));
    }

    /// <summary>The live Spotify lookup, or null when Spotify cannot be asked (not connected, rate limited, unreachable).</summary>
    private static async Task<SpotifyPlaylistLookupResult?> TryLookupSpotifyPlaylistAsync(
        ISpotifyApiService spotifyApi, string playlistId, ILogger logger, CancellationToken ct)
    {
        try
        {
            return await spotifyApi.GetPlaylistAsync(playlistId, ct);
        }
        catch (Exception ex) when (ex is SpotifyNotConnectedException or SpotifyRateLimitException or HttpRequestException
            or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogInformation(ex, "Spotify could not be asked about playlist {PlaylistId}; trying the archive", playlistId);
            return null;
        }
    }

    private static IResult ArchiveUnavailable() =>
        Results.Json(
            new { error = "archive_unavailable", message = "The Spotify Playlist Archive could not be reached. Try again in a minute." },
            statusCode: StatusCodes.Status503ServiceUnavailable);

    private static DiscoverPlaylistDto ToSummaryDto(
        ArchivedPlaylist p, string? coverUrl, int? trackCount, IReadOnlyDictionary<string, (int SourceId, bool AutoSync)> subs)
    {
        var subscribed = subs.TryGetValue(p.Id, out var sub);
        return new DiscoverPlaylistDto(
            p.Id,
            p.Name,
            p.Description,
            coverUrl,
            trackCount,
            p.Followers,
            p.OwnerName,
            subscribed,
            subscribed ? sub.SourceId : null,
            subscribed ? sub.AutoSync : null);
    }

    /// <summary>
    /// A library row's artist is either a track's lead artist or its whole credit depending on how the file
    /// was tagged, so a track counts as owned under either spelling.
    /// </summary>
    private static bool IsInLibrary(HashSet<string> libraryKeys, ArchivedTrack t) =>
        libraryKeys.Contains(LibraryKey(ArchiveFormat.ArtistCredit(t.Artists), t.Title))
        || (t.Artists.Count > 0 && libraryKeys.Contains(LibraryKey(t.Artists[0], t.Title)));

    private static string LibraryKey(string? artist, string? title) =>
        $"{TitleNormalizer.NormalizeForSearch(artist)}\0{TitleNormalizer.NormalizeForSearch(title)}";
}

public sealed record DiscoverPlaylistDto(
    string Id,
    string Title,
    string? Description,
    string? CoverUrl,
    int? TrackCount,
    long Followers,
    string? CreatorName,
    bool Subscribed,
    int? SourceId,
    bool? AutoSync);

public sealed record DiscoverTrackDto(
    string SpotifyTrackId,
    string Title,
    string Artist,
    string? Album,
    int? DurationMs,
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

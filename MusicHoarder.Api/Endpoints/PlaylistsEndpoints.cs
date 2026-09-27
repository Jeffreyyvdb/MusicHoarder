using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Playlists;
using MusicHoarder.Api.Sharing;

namespace MusicHoarder.Api.Endpoints;

/// <summary>
/// MusicHoarder's own playlists (<see cref="Playlist"/>): the ones made here, and one per collected
/// Spotify / Deezer / YouTube playlist, to which songs can be added here too.
///
/// <para>
/// Open to every signed-in account, members included — a playlist is personal listening state, like
/// a like. Rows are filtered to their owner, so another account's playlist id is a 404; every song id
/// written is first checked against what the caller may read (<see cref="ILibraryScopeResolver"/>),
/// and every read checks them again, so a revoked grant empties a member's playlist of those songs.
/// Writes are allowlisted one by one in <c>MemberWriteGuardMiddleware</c>; the demo account stays
/// read-only.
/// </para>
///
/// <para>
/// Like <c>/api/albums</c> and <c>/api/radio</c>, a playlist carries song ids, which both clients
/// join against the <c>GET /songs</c> list they already hold.
/// </para>
/// </summary>
public static class PlaylistsEndpoints
{
    /// <summary>At most this many songs per request — a whole album fits, a whole library does not.</summary>
    internal const int MaxSongsPerRequest = 1000;

    internal const int MaxNameLength = 200;

    public static IEndpointRouteBuilder MapPlaylistsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/playlists").WithTags("Playlists");

        group.MapGet("", ListPlaylists)
            .WithName("ListPlaylists")
            .WithSummary("The caller's playlists — made here, and one per collected Spotify/Deezer/YouTube playlist — each with its playable song ids in order.");
        group.MapPost("", CreatePlaylist)
            .WithName("CreatePlaylist")
            .WithSummary("Make a playlist, optionally with its first songs.");
        group.MapGet("/{id:int}", GetPlaylist)
            .WithName("GetPlaylist")
            .WithSummary("One playlist with its playable song ids in order.");
        group.MapPatch("/{id:int}", UpdatePlaylist)
            .WithName("UpdatePlaylist")
            .WithSummary("Rename a playlist, or switch its export to the library's playlists folder.");
        group.MapDelete("/{id:int}", DeletePlaylist)
            .WithName("DeletePlaylist")
            .WithSummary("Delete a playlist made here (and its exported file).");
        group.MapPost("/{id:int}/songs", AddSongs)
            .WithName("AddPlaylistSongs")
            .WithSummary("Add songs to the end of a playlist; songs already on it are skipped.");
        group.MapPut("/{id:int}/songs", ReorderSongs)
            .WithName("ReorderPlaylistSongs")
            .WithSummary("Reorder the songs added in MusicHoarder (a synced playlist's own tracks keep the remote order).");
        group.MapDelete("/{id:int}/songs/{songId:int}", RemoveSong)
            .WithName("RemovePlaylistSong")
            .WithSummary("Remove a song added in MusicHoarder from a playlist.");

        return app;
    }

    public sealed record CreatePlaylistRequest(string? Name, int[]? SongIds);

    public sealed record UpdatePlaylistRequest(string? Name, bool? ExportToLibrary);

    public sealed record PlaylistSongsRequest(int[]? SongIds);

    /// <param name="Type"><c>spotifyLiked</c>, <c>spotifyPlaylist</c>, <c>deezer</c> or <c>youtube</c>.</param>
    public sealed record PlaylistSourceDto(
        int Id, string Type, string Name, string? ImageUrl, string? Url, bool AutoSync, DateTime? LastSyncedAtUtc);

    /// <param name="SongIds">What plays, in order: a synced playlist's tracks, then the ones added here.</param>
    /// <param name="AddedSongIds">The songs added in MusicHoarder — the ones that can be removed or reordered here.</param>
    /// <param name="MissingCount">A synced playlist's tracks that are not in the library yet.</param>
    public sealed record PlaylistDto(
        int Id,
        string Name,
        PlaylistSourceDto? Source,
        IReadOnlyList<int> SongIds,
        IReadOnlyList<int> AddedSongIds,
        int MissingCount,
        bool ExportToLibrary,
        DateTime? ExportedAtUtc,
        DateTime CreatedAtUtc,
        DateTime UpdatedAtUtc);

    /// <param name="CanExport">Whether the caller's playlists can be written into the library (the library owner's only).</param>
    public sealed record PlaylistsResponse(IReadOnlyList<PlaylistDto> Playlists, bool CanExport);

    public sealed record PlaylistSongsResult(int Added, int AlreadyPresent, PlaylistDto Playlist);

    internal static async Task<IResult> ListPlaylists(
        MusicHoarderDbContext db,
        ICurrentUserAccessor currentUser,
        ILibraryScopeResolver scopeResolver,
        IOwnerLookupService ownerLookup,
        CancellationToken ct)
    {
        await EnsureSourcePlaylistsAsync(db, currentUser.UserId, ct);

        var playlists = await db.Playlists.AsNoTracking().Include(p => p.Entries).ToListAsync(ct);
        var dtos = await BuildAsync(db, scopeResolver, playlists, ct);
        return Results.Ok(new PlaylistsResponse(
            dtos.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.Id).ToList(),
            IsLibraryOwner(currentUser, ownerLookup)));
    }

    internal static async Task<IResult> GetPlaylist(
        int id,
        MusicHoarderDbContext db,
        ILibraryScopeResolver scopeResolver,
        CancellationToken ct)
    {
        var playlist = await db.Playlists.AsNoTracking().Include(p => p.Entries).FirstOrDefaultAsync(p => p.Id == id, ct);
        if (playlist is null)
            return NotFound(id);
        return Results.Ok((await BuildAsync(db, scopeResolver, [playlist], ct))[0]);
    }

    internal static async Task<IResult> CreatePlaylist(
        CreatePlaylistRequest body,
        MusicHoarderDbContext db,
        ICurrentUserAccessor currentUser,
        ILibraryScopeResolver scopeResolver,
        IOwnerLookupService ownerLookup,
        LibraryPlaylistExportQueue exportQueue,
        CancellationToken ct)
    {
        var name = NormalizeName(body.Name);
        if (name is null)
            return Results.BadRequest(new { message = "A playlist needs a name." });
        var requested = body.SongIds ?? [];
        if (requested.Length > MaxSongsPerRequest)
            return TooManySongs();

        var scope = await scopeResolver.ResolveAsync(db, ct);
        var playable = await PlaylistTracks.PlayableForCallerAsync(db, scope, requested, ct);

        var now = DateTime.UtcNow;
        var playlist = new Playlist
        {
            OwnerUserId = currentUser.UserId,
            Name = name,
            ExportToLibrary = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var position = 0;
        foreach (var songId in requested.Where(playable.ContainsKey).Select(id => playable[id]).Distinct())
            playlist.Entries.Add(new PlaylistEntry { SongId = songId, Position = position++, AddedAtUtc = now });

        db.Playlists.Add(playlist);
        await db.SaveChangesAsync(ct);
        if (IsLibraryOwner(currentUser, ownerLookup))
            exportQueue.Request();

        var dto = (await BuildAsync(db, scopeResolver, [playlist], ct))[0];
        return Results.Created($"/api/playlists/{playlist.Id}", dto);
    }

    internal static async Task<IResult> UpdatePlaylist(
        int id,
        UpdatePlaylistRequest body,
        MusicHoarderDbContext db,
        ICurrentUserAccessor currentUser,
        ILibraryScopeResolver scopeResolver,
        IOwnerLookupService ownerLookup,
        ILibraryPlaylistExporter exporter,
        LibraryPlaylistExportQueue exportQueue,
        CancellationToken ct)
    {
        var playlist = await db.Playlists.Include(p => p.Entries).FirstOrDefaultAsync(p => p.Id == id, ct);
        if (playlist is null)
            return NotFound(id);

        if (body.Name is not null)
        {
            if (playlist.WishlistSourceId is not null)
                return Results.BadRequest(new { message = "A synced playlist keeps the name of the playlist it follows." });
            var name = NormalizeName(body.Name);
            if (name is null)
                return Results.BadRequest(new { message = "A playlist needs a name." });
            playlist.Name = name;
        }

        string? fileToRemove = null;
        if (body.ExportToLibrary is { } export)
        {
            if (!IsLibraryOwner(currentUser, ownerLookup))
                return Results.Json(
                    new { message = "Only the library owner's playlists are written into the library." },
                    statusCode: StatusCodes.Status403Forbidden);
            playlist.ExportToLibrary = export;
            if (!export)
            {
                fileToRemove = playlist.ExportFilePath;
                playlist.ExportFilePath = null;
                playlist.ExportedAtUtc = null;
            }
        }

        playlist.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await exporter.RemoveFileAsync(fileToRemove, ct);
        if (IsLibraryOwner(currentUser, ownerLookup))
            exportQueue.Request();

        return Results.Ok((await BuildAsync(db, scopeResolver, [playlist], ct))[0]);
    }

    internal static async Task<IResult> DeletePlaylist(
        int id,
        MusicHoarderDbContext db,
        ILibraryPlaylistExporter exporter,
        CancellationToken ct)
    {
        var playlist = await db.Playlists.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (playlist is null)
            return NotFound(id);
        if (playlist.WishlistSourceId is not null)
            return Results.BadRequest(new
            {
                message = "A synced playlist goes when its source does: remove the source under Wishlist.",
            });

        var file = playlist.ExportFilePath;
        db.Playlists.Remove(playlist);
        await db.SaveChangesAsync(ct);
        await exporter.RemoveFileAsync(file, ct);
        return Results.Ok(new { message = "Playlist deleted." });
    }

    internal static async Task<IResult> AddSongs(
        int id,
        PlaylistSongsRequest body,
        MusicHoarderDbContext db,
        ICurrentUserAccessor currentUser,
        ILibraryScopeResolver scopeResolver,
        IOwnerLookupService ownerLookup,
        LibraryPlaylistExportQueue exportQueue,
        CancellationToken ct)
    {
        var requested = body.SongIds ?? [];
        if (requested.Length == 0)
            return Results.BadRequest(new { message = "No songs to add." });
        if (requested.Length > MaxSongsPerRequest)
            return TooManySongs();

        var playlist = await db.Playlists.Include(p => p.Entries).FirstOrDefaultAsync(p => p.Id == id, ct);
        if (playlist is null)
            return NotFound(id);

        var scope = await scopeResolver.ResolveAsync(db, ct);
        var playable = await PlaylistTracks.PlayableForCallerAsync(db, scope, requested, ct);
        if (playable.Count == 0)
            return Results.NotFound(new { message = "Those songs are not in your library." });

        // What already plays, synced tracks included, so a song is never on a playlist twice.
        var current = (await ResolveAsync(db, scope, [playlist], ct))[playlist.Id];
        var present = current.SongIds.ToHashSet();
        present.UnionWith(playlist.Entries.Select(e => e.SongId));

        var now = DateTime.UtcNow;
        var position = playlist.Entries.Count == 0 ? 0 : playlist.Entries.Max(e => e.Position) + 1;
        int added = 0, alreadyPresent = 0;
        foreach (var requestedId in requested)
        {
            if (!playable.TryGetValue(requestedId, out var songId))
                continue;
            if (!present.Add(songId))
            {
                alreadyPresent++;
                continue;
            }
            playlist.Entries.Add(new PlaylistEntry { SongId = songId, Position = position++, AddedAtUtc = now });
            added++;
        }

        if (added > 0)
        {
            playlist.UpdatedAtUtc = now;
            await db.SaveChangesAsync(ct);
            if (IsLibraryOwner(currentUser, ownerLookup))
                exportQueue.Request();
        }

        var dto = (await BuildAsync(db, scopeResolver, [playlist], ct))[0];
        return Results.Ok(new PlaylistSongsResult(added, alreadyPresent, dto));
    }

    internal static async Task<IResult> RemoveSong(
        int id,
        int songId,
        MusicHoarderDbContext db,
        ICurrentUserAccessor currentUser,
        ILibraryScopeResolver scopeResolver,
        IOwnerLookupService ownerLookup,
        LibraryPlaylistExportQueue exportQueue,
        CancellationToken ct)
    {
        var playlist = await db.Playlists.Include(p => p.Entries).FirstOrDefaultAsync(p => p.Id == id, ct);
        if (playlist is null)
            return NotFound(id);

        // The client names the song it plays, which for an entry pointing at a since-merged duplicate
        // is the keeper — so match on both.
        var scope = await scopeResolver.ResolveAsync(db, ct);
        var playable = await PlaylistTracks.PlayableForCallerAsync(db, scope, playlist.Entries.Select(e => e.SongId).ToList(), ct);
        var entries = playlist.Entries
            .Where(e => e.SongId == songId || (playable.TryGetValue(e.SongId, out var target) && target == songId))
            .ToList();

        if (entries.Count == 0)
        {
            var current = (await ResolveAsync(db, scope, [playlist], ct))[playlist.Id];
            return current.SongIds.Contains(songId)
                ? Results.BadRequest(new { message = "This song comes from the playlist this one follows; remove it there." })
                : Results.NotFound(new { message = "That song is not on this playlist." });
        }

        foreach (var entry in entries)
            playlist.Entries.Remove(entry);
        playlist.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        if (IsLibraryOwner(currentUser, ownerLookup))
            exportQueue.Request();

        return Results.Ok((await BuildAsync(db, scopeResolver, [playlist], ct))[0]);
    }

    internal static async Task<IResult> ReorderSongs(
        int id,
        PlaylistSongsRequest body,
        MusicHoarderDbContext db,
        ICurrentUserAccessor currentUser,
        ILibraryScopeResolver scopeResolver,
        IOwnerLookupService ownerLookup,
        LibraryPlaylistExportQueue exportQueue,
        CancellationToken ct)
    {
        var order = body.SongIds ?? [];
        var playlist = await db.Playlists.Include(p => p.Entries).FirstOrDefaultAsync(p => p.Id == id, ct);
        if (playlist is null)
            return NotFound(id);

        var scope = await scopeResolver.ResolveAsync(db, ct);
        var playable = await PlaylistTracks.PlayableForCallerAsync(db, scope, playlist.Entries.Select(e => e.SongId).ToList(), ct);
        var rank = new Dictionary<int, int>();
        for (var i = 0; i < order.Length; i++)
            rank.TryAdd(order[i], i);

        // Songs named in the body take its order; anything it left out (a song no longer visible,
        // or one added meanwhile on another device) keeps its relative place after them.
        var reordered = playlist.Entries
            .OrderBy(e => rank.TryGetValue(playable.GetValueOrDefault(e.SongId, e.SongId), out var r) ? r : int.MaxValue)
            .ThenBy(e => e.Position)
            .ThenBy(e => e.Id)
            .ToList();
        for (var i = 0; i < reordered.Count; i++)
            reordered[i].Position = i;

        playlist.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        if (IsLibraryOwner(currentUser, ownerLookup))
            exportQueue.Request();

        return Results.Ok((await BuildAsync(db, scopeResolver, [playlist], ct))[0]);
    }

    /// <summary>
    /// Gives each of the caller's collected sources its playlist row, so a source-backed playlist has
    /// an id from the moment it can be listed. Idempotent; a concurrent listing that got there first is
    /// fine (the unique index turns the second insert away).
    /// </summary>
    internal static async Task EnsureSourcePlaylistsAsync(MusicHoarderDbContext db, Guid userId, CancellationToken ct)
    {
        if (userId == Guid.Empty)
            return;
        var sources = await db.WishlistSources.AsNoTracking()
            .Select(s => new { s.Id, s.Name, s.CreatedAtUtc })
            .ToListAsync(ct);
        if (sources.Count == 0)
            return;
        var linked = (await db.Playlists
                .Where(p => p.WishlistSourceId != null)
                .Select(p => p.WishlistSourceId!.Value)
                .ToListAsync(ct))
            .ToHashSet();
        var missing = sources.Where(s => !linked.Contains(s.Id)).ToList();
        if (missing.Count == 0)
            return;

        var now = DateTime.UtcNow;
        var rows = missing.Select(s => new Playlist
        {
            OwnerUserId = userId,
            Name = s.Name,
            WishlistSourceId = s.Id,
            ExportToLibrary = false,
            CreatedAtUtc = s.CreatedAtUtc == default ? now : s.CreatedAtUtc,
            UpdatedAtUtc = now,
        }).ToList();
        db.Playlists.AddRange(rows);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another request created them first.
        }
        finally
        {
            foreach (var row in rows)
                db.Entry(row).State = EntityState.Detached;
        }
    }

    internal static async Task<List<PlaylistDto>> BuildAsync(
        MusicHoarderDbContext db, ILibraryScopeResolver scopeResolver, IReadOnlyList<Playlist> playlists, CancellationToken ct)
    {
        if (playlists.Count == 0)
            return [];
        var scope = await scopeResolver.ResolveAsync(db, ct);
        var sources = await LoadSourcesAsync(db, playlists, ct);
        var resolved = await ResolveAsync(db, scope, playlists, ct, sources);
        return playlists
            .Select(p => ToDto(p, p.WishlistSourceId is { } sid ? sources.GetValueOrDefault(sid) : null, resolved[p.Id]))
            .ToList();
    }

    private static async Task<Dictionary<int, ResolvedPlaylistTracks>> ResolveAsync(
        MusicHoarderDbContext db,
        ILibraryScope scope,
        IReadOnlyList<Playlist> playlists,
        CancellationToken ct,
        Dictionary<int, WishlistSource>? sources = null)
    {
        sources ??= await LoadSourcesAsync(db, playlists, ct);
        var refs = await PlaylistTracks.LoadRefsAsync(db, playlists, sources, ct);
        var candidates = refs.Values.SelectMany(r => r).Where(r => r.SongId != null).Select(r => r.SongId!.Value).ToList();
        var playable = await PlaylistTracks.PlayableForCallerAsync(db, scope, candidates, ct);
        return refs.ToDictionary(kv => kv.Key, kv => PlaylistTracks.Resolve(kv.Value, playable));
    }

    /// <summary>The playlists' sources, through the ambient filter — a playlist only ever follows its owner's own source.</summary>
    private static async Task<Dictionary<int, WishlistSource>> LoadSourcesAsync(
        MusicHoarderDbContext db, IReadOnlyList<Playlist> playlists, CancellationToken ct)
    {
        var ids = playlists.Where(p => p.WishlistSourceId != null).Select(p => p.WishlistSourceId!.Value).ToList();
        if (ids.Count == 0)
            return [];
        return await db.WishlistSources.AsNoTracking().Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
    }

    private static PlaylistDto ToDto(Playlist playlist, WishlistSource? source, ResolvedPlaylistTracks tracks) =>
        new(
            playlist.Id,
            source is not null && !string.IsNullOrWhiteSpace(source.Name) ? source.Name : playlist.Name,
            source is null ? null : ToSourceDto(source),
            tracks.SongIds,
            tracks.AddedSongIds,
            tracks.MissingCount,
            playlist.ExportToLibrary,
            playlist.ExportedAtUtc,
            playlist.CreatedAtUtc,
            playlist.UpdatedAtUtc);

    private static PlaylistSourceDto ToSourceDto(WishlistSource source)
    {
        var (type, url) = source.SourceType switch
        {
            WishlistSourceType.LikedSongs => ("spotifyLiked", "https://open.spotify.com/collection/tracks"),
            WishlistSourceType.Playlist => ("spotifyPlaylist", UrlOf("https://open.spotify.com/playlist/", source.SpotifyPlaylistId)),
            WishlistSourceType.DeezerPlaylist => ("deezer", UrlOf("https://www.deezer.com/playlist/", source.DeezerPlaylistId)),
            WishlistSourceType.YouTubePlaylist => ("youtube", UrlOf("https://www.youtube.com/playlist?list=", source.YouTubePlaylistId)),
            _ => ("unknown", null),
        };
        return new PlaylistSourceDto(source.Id, type, source.Name, source.ImageUrl, url, source.AutoSync, source.LastSyncedAtUtc);
    }

    private static string? UrlOf(string prefix, string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : prefix + Uri.EscapeDataString(id);

    private static string? NormalizeName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;
        return trimmed.Length > MaxNameLength ? trimmed[..MaxNameLength].Trim() : trimmed;
    }

    /// <summary>
    /// Whether the caller's playlists go into the destination library: only the library owner's, whose
    /// library it is (<see cref="LibraryPlaylistExporter"/> exports nobody else's).
    /// </summary>
    private static bool IsLibraryOwner(ICurrentUserAccessor currentUser, IOwnerLookupService ownerLookup) =>
        currentUser.User is { IsAdmin: true } && currentUser.UserId == ownerLookup.OwnerUserId;

    private static IResult NotFound(int id) =>
        Results.NotFound(new { message = $"Playlist {id} not found." });

    private static IResult TooManySongs() =>
        Results.BadRequest(new { message = $"At most {MaxSongsPerRequest} songs at a time." });
}

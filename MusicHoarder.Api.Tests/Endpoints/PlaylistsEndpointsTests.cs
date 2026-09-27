using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Playlists;
using MusicHoarder.Api.Spotify;
using MusicHoarder.Api.Tests.Auth;
using MusicHoarder.Api.Tests.Sharing;
using static MusicHoarder.Api.Endpoints.PlaylistsEndpoints;

namespace MusicHoarder.Api.Tests.Endpoints;

/// <summary>
/// Playlists made here, and the ones that follow a collected Spotify/Deezer/YouTube playlist. What is
/// under test is mostly what a playlist PLAYS: the synced tracks in the remote order (including ones
/// another source wishlisted first), the additions after them, merges and deletions followed, and a
/// member's playlist never reaching past their grants.
/// </summary>
public class PlaylistsEndpointsTests
{
    // --- Playlists made here ---------------------------------------------------------------------

    [Fact]
    public async Task Create_then_list_plays_the_songs_in_the_order_given()
    {
        var options = NewOptions();
        await SeedSongs(options, Song(1, "Daft Punk", "Discovery", "One More Time"), Song(2, "Justice", "Cross", "Genesis"));

        await using var db = Context(options, Admin);
        var created = Value<PlaylistDto>(await Create(db, Admin, "Road trip", 2, 1, 99));

        Assert.Equal("Road trip", created.Name);
        Assert.Null(created.Source);
        Assert.Equal([2, 1], created.SongIds);   // 99 is nobody's song and is dropped
        Assert.Equal([2, 1], created.AddedSongIds);
        Assert.True(created.ExportToLibrary);

        var list = Value<PlaylistsResponse>(await List(db, Admin));
        Assert.True(list.CanExport);
        Assert.Equal([2, 1], Assert.Single(list.Playlists).SongIds);
    }

    [Fact]
    public async Task Create_needs_a_name()
    {
        var options = NewOptions();
        await using var db = Context(options, Admin);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(await Create(db, Admin, "   ")));
    }

    [Fact]
    public async Task Add_appends_and_skips_songs_already_on_the_playlist()
    {
        var options = NewOptions();
        await SeedSongs(options, Song(1, "A", "X", "One"), Song(2, "A", "X", "Two"), Song(3, "A", "X", "Three"));
        await using var db = Context(options, Admin);
        var id = Value<PlaylistDto>(await Create(db, Admin, "Mix", 1)).Id;

        var result = Value<PlaylistSongsResult>(await Add(db, Admin, id, 3, 1, 2));

        Assert.Equal(2, result.Added);
        Assert.Equal(1, result.AlreadyPresent);
        Assert.Equal([1, 3, 2], result.Playlist.SongIds);
    }

    [Fact]
    public async Task Remove_and_reorder_change_only_the_additions()
    {
        var options = NewOptions();
        await SeedSongs(options, Song(1, "A", "X", "One"), Song(2, "A", "X", "Two"), Song(3, "A", "X", "Three"));
        await using var db = Context(options, Admin);
        var id = Value<PlaylistDto>(await Create(db, Admin, "Mix", 1, 2, 3)).Id;

        var reordered = Value<PlaylistDto>(await PlaylistsEndpoints.ReorderSongs(
            id, new PlaylistSongsRequest([3, 1, 2]), db, Accessor(Admin), Scope(Admin), Owner, new LibraryPlaylistExportQueue(), default));
        Assert.Equal([3, 1, 2], reordered.SongIds);

        var removed = Value<PlaylistDto>(await PlaylistsEndpoints.RemoveSong(
            id, 1, db, Accessor(Admin), Scope(Admin), Owner, new LibraryPlaylistExportQueue(), default));
        Assert.Equal([3, 2], removed.SongIds);
    }

    [Fact]
    public async Task A_deleted_song_drops_out_and_a_merged_duplicate_plays_its_keeper()
    {
        var options = NewOptions();
        await SeedSongs(options, Song(1, "A", "X", "One"), Song(2, "A", "X", "Two"), Song(3, "A", "X", "Two (dup)"));
        await using (var setup = Context(options, Admin))
        {
            Value<PlaylistDto>(await Create(setup, Admin, "Mix", 1, 3));
        }
        await using (var raw = new MusicHoarderDbContext(options))
        {
            (await raw.Songs.SingleAsync(s => s.Id == 1)).SoftDelete();
            (await raw.Songs.SingleAsync(s => s.Id == 3)).MarkAsDuplicate(2);
            await raw.SaveChangesAsync();
        }

        await using var db = Context(options, Admin);
        var playlist = Assert.Single(Value<PlaylistsResponse>(await List(db, Admin)).Playlists);
        Assert.Equal([2], playlist.SongIds);

        // The client removes what it plays — the keeper — and that removes the entry for the duplicate.
        var removed = Value<PlaylistDto>(await PlaylistsEndpoints.RemoveSong(
            playlist.Id, 2, db, Accessor(Admin), Scope(Admin), Owner, new LibraryPlaylistExportQueue(), default));
        Assert.Empty(removed.SongIds);
    }

    [Fact]
    public async Task Delete_removes_the_playlist_and_its_exported_file()
    {
        var options = NewOptions();
        await using var db = Context(options, Admin);
        var id = Value<PlaylistDto>(await Create(db, Admin, "Mix")).Id;
        var row = await db.Playlists.SingleAsync();
        row.ExportFilePath = "/music/Playlists/Mix.m3u8";
        await db.SaveChangesAsync();
        var exporter = new RecordingExporter();

        var result = await PlaylistsEndpoints.DeletePlaylist(id, db, exporter, default);

        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
        Assert.Empty(await db.Playlists.ToListAsync());
        Assert.Equal(["/music/Playlists/Mix.m3u8"], exporter.Removed);
    }

    [Fact]
    public async Task Switching_export_off_removes_the_file()
    {
        var options = NewOptions();
        await using var db = Context(options, Admin);
        var id = Value<PlaylistDto>(await Create(db, Admin, "Mix")).Id;
        (await db.Playlists.SingleAsync()).ExportFilePath = "/music/Playlists/Mix.m3u8";
        await db.SaveChangesAsync();
        var exporter = new RecordingExporter();

        var dto = Value<PlaylistDto>(await PlaylistsEndpoints.UpdatePlaylist(
            id, new UpdatePlaylistRequest(null, false), db, Accessor(Admin), Scope(Admin), Owner, exporter,
            new LibraryPlaylistExportQueue(), default));

        Assert.False(dto.ExportToLibrary);
        Assert.Equal(["/music/Playlists/Mix.m3u8"], exporter.Removed);
        Assert.Null((await db.Playlists.SingleAsync()).ExportFilePath);
    }

    // --- Playlists that follow a collected source ------------------------------------------------

    [Fact]
    public async Task A_collected_playlist_plays_its_tracks_in_remote_order_then_the_additions()
    {
        var options = NewOptions();
        await SeedSongs(options,
            Song(10, "A", "X", "Owned via Liked Songs"),
            Song(11, "A", "X", "Downloaded"),
            Song(12, "A", "X", "Owned, not processed yet"),
            Song(20, "B", "Y", "Added here"));
        int sourceId;
        await using (var seed = new MusicHoarderDbContext(options))
        {
            var liked = Source(WishlistSourceType.LikedSongs, "Liked Songs", recorded: false);
            var roadSource = Source(WishlistSourceType.Playlist, "Road trip", recorded: true, spotifyPlaylistId: "pl");
            seed.WishlistSources.AddRange(liked, roadSource);
            await seed.SaveChangesAsync();
            sourceId = roadSource.Id;

            // "a" came in through Liked Songs first — the playlist still plays it.
            var a = Item(liked.Id, "a", downloadedSongId: 10);
            var b = Item(roadSource.Id, "b", downloadedSongId: 11);
            var c = Item(roadSource.Id, "c", downloadedSongId: null);  // still on the wishlist
            var d = Item(roadSource.Id, "d", downloadedSongId: null);  // owned; the worker has not reached it
            seed.WishlistItems.AddRange(a, b, c, d);
            await seed.SaveChangesAsync();
            seed.SpotifyTrackLibraryMatches.Add(new SpotifyTrackLibraryMatch
            {
                OwnerUserId = TestUsers.OwnerId,
                SpotifyTrackId = "d",
                MatchStatus = (int)ComparisonMatchStatus.InLibrary,
                MatchedSongId = 12,
                UpdatedAtUtc = DateTime.UtcNow,
            });
            var order = new[] { b, a, c, d };
            for (var i = 0; i < order.Length; i++)
                seed.WishlistSourceTracks.Add(new WishlistSourceTrack { WishlistSourceId = roadSource.Id, Position = i, WishlistItemId = order[i].Id });
            await seed.SaveChangesAsync();
        }

        await using var db = Context(options, Admin);
        var road = Value<PlaylistsResponse>(await List(db, Admin)).Playlists.Single(p => p.Source?.Id == sourceId);
        Assert.Equal("Road trip", road.Name);
        Assert.Equal("spotifyPlaylist", road.Source!.Type);
        Assert.Equal("https://open.spotify.com/playlist/pl", road.Source.Url);
        Assert.False(road.ExportToLibrary);
        Assert.Equal([11, 10, 12], road.SongIds);
        Assert.Equal(1, road.MissingCount);

        var added = Value<PlaylistSongsResult>(await Add(db, Admin, road.Id, 20, 10));
        Assert.Equal(1, added.Added);
        Assert.Equal(1, added.AlreadyPresent);  // 10 is already a synced track
        Assert.Equal([11, 10, 12, 20], added.Playlist.SongIds);
        Assert.Equal([20], added.Playlist.AddedSongIds);

        // A synced track is the remote playlist's to remove.
        var removeSynced = await PlaylistsEndpoints.RemoveSong(
            road.Id, 11, db, Accessor(Admin), Scope(Admin), Owner, new LibraryPlaylistExportQueue(), default);
        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(removeSynced));

        // It is named after its source, and goes with it.
        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(await PlaylistsEndpoints.UpdatePlaylist(
            road.Id, new UpdatePlaylistRequest("Renamed", null), db, Accessor(Admin), Scope(Admin), Owner,
            new RecordingExporter(), new LibraryPlaylistExportQueue(), default)));
        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(await PlaylistsEndpoints.DeletePlaylist(
            road.Id, db, new RecordingExporter(), default)));
    }

    [Fact]
    public async Task A_source_whose_tracklist_was_never_recorded_plays_the_items_it_introduced()
    {
        var options = NewOptions();
        await SeedSongs(options, Song(10, "A", "X", "Older like"), Song(11, "A", "X", "Newer like"));
        await using (var seed = new MusicHoarderDbContext(options))
        {
            var likedSource = Source(WishlistSourceType.LikedSongs, "Liked Songs", recorded: false);
            seed.WishlistSources.Add(likedSource);
            await seed.SaveChangesAsync();
            seed.WishlistItems.AddRange(
                Item(likedSource.Id, "old", downloadedSongId: 10, addedAt: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
                Item(likedSource.Id, "new", downloadedSongId: 11, addedAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
            await seed.SaveChangesAsync();
        }

        await using var db = Context(options, Admin);
        var liked = Assert.Single(Value<PlaylistsResponse>(await List(db, Admin)).Playlists);

        // Liked Songs reads newest first, as on Spotify.
        Assert.Equal([11, 10], liked.SongIds);
        Assert.Equal("spotifyLiked", liked.Source!.Type);
    }

    [Fact]
    public async Task Listing_twice_gives_a_source_one_playlist()
    {
        var options = NewOptions();
        await using (var seed = new MusicHoarderDbContext(options))
        {
            seed.WishlistSources.Add(Source(WishlistSourceType.YouTubePlaylist, "Cool music", recorded: true, youTubePlaylistId: "PL1"));
            await seed.SaveChangesAsync();
        }

        await using var db = Context(options, Admin);
        await List(db, Admin);
        var second = Value<PlaylistsResponse>(await List(db, Admin));

        var playlist = Assert.Single(second.Playlists);
        Assert.Equal("https://www.youtube.com/playlist?list=PL1", playlist.Source!.Url);
        Assert.Single(await db.Playlists.ToListAsync());
    }

    [Fact]
    public async Task Removing_a_source_keeps_its_playlist_only_if_songs_were_added_here()
    {
        var options = NewOptions();
        await SeedSongs(options, Song(10, "A", "X", "Synced"), Song(20, "B", "Y", "Added here"));
        int keptSourceId, droppedSourceId;
        await using (var seed = new MusicHoarderDbContext(options))
        {
            var kept = Source(WishlistSourceType.Playlist, "Kept", recorded: true, spotifyPlaylistId: "k");
            var dropped = Source(WishlistSourceType.Playlist, "Dropped", recorded: true, spotifyPlaylistId: "d");
            seed.WishlistSources.AddRange(kept, dropped);
            await seed.SaveChangesAsync();
            keptSourceId = kept.Id;
            droppedSourceId = dropped.Id;
            var item = Item(kept.Id, "s", downloadedSongId: 10);
            seed.WishlistItems.Add(item);
            await seed.SaveChangesAsync();
            seed.WishlistSourceTracks.Add(new WishlistSourceTrack { WishlistSourceId = kept.Id, Position = 0, WishlistItemId = item.Id });
            await seed.SaveChangesAsync();
        }
        await using (var db = Context(options, Admin))
        {
            var lists = Value<PlaylistsResponse>(await List(db, Admin)).Playlists;
            await Add(db, Admin, lists.Single(p => p.Source?.Id == keptSourceId).Id, 20);
        }

        await using (var db = new MusicHoarderDbContext(options))
        {
            foreach (var id in new[] { keptSourceId, droppedSourceId })
            {
                var source = await db.WishlistSources.SingleAsync(s => s.Id == id);
                await PlaylistSources.DetachAsync(db, source, default);
                db.WishlistSources.Remove(source);
                await db.SaveChangesAsync();
            }
        }

        await using var verify = Context(options, Admin);
        var remaining = Assert.Single(Value<PlaylistsResponse>(await List(verify, Admin)).Playlists);
        Assert.Equal("Kept", remaining.Name);
        Assert.Null(remaining.Source);
        // It plays what it played: the synced track, frozen in, then the addition.
        Assert.Equal([10, 20], remaining.SongIds);
    }

    // --- Members -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_member_playlist_holds_only_songs_shared_with_them_and_loses_them_when_the_grant_goes()
    {
        var options = NewOptions();
        await SeedSongs(options, Song(1, "Daft Punk", "Discovery", "One More Time"), Song(2, "Justice", "Cross", "Genesis"));
        await using (var seed = new MusicHoarderDbContext(options))
        {
            seed.LibraryShareGrants.Add(new LibraryShareGrant
            {
                OwnerUserId = TestUsers.OwnerId,
                GranteeUserId = TestUsers.FriendId,
                Scope = ShareGrantScope.Album,
                ArtistKey = "daft punk",
                AlbumKey = "discovery",
                CreatedAtUtc = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        await using (var db = Context(options, Member))
        {
            var created = Value<PlaylistDto>(await Create(db, Member, "Mine", 1, 2));
            Assert.Equal([1], created.SongIds);  // 2 was never shared with them

            var list = Value<PlaylistsResponse>(await List(db, Member));
            Assert.False(list.CanExport);
            Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(await PlaylistsEndpoints.UpdatePlaylist(
                created.Id, new UpdatePlaylistRequest(null, true), db, Accessor(Member), Scope(Member), Owner,
                new RecordingExporter(), new LibraryPlaylistExportQueue(), default)));
        }

        // The admin cannot see a member's playlist, nor a member the admin's.
        await using (var admin = Context(options, Admin))
        {
            Assert.Empty(Value<PlaylistsResponse>(await List(admin, Admin)).Playlists);
            var adminsOwn = Value<PlaylistDto>(await Create(admin, Admin, "Admin's", 1));
            await using var member = Context(options, Member);
            Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await PlaylistsEndpoints.GetPlaylist(adminsOwn.Id, member, Scope(Member), default)));
        }

        await using (var revoke = new MusicHoarderDbContext(options))
        {
            revoke.LibraryShareGrants.RemoveRange(revoke.LibraryShareGrants);
            await revoke.SaveChangesAsync();
        }

        await using var after = Context(options, Member);
        Assert.Empty(Assert.Single(Value<PlaylistsResponse>(await List(after, Member)).Playlists).SongIds);
    }

    // --- helpers -----------------------------------------------------------------------------------

    private static CurrentUser Admin => new(TestUsers.OwnerId, "admin@test.local", UserRole.Admin, "Admin");

    private static CurrentUser Member =>
        new(TestUsers.FriendId, "member@test.local", UserRole.Member, "Member", Capability.TrackListening);

    private static readonly TestOwnerLookupService Owner = new();

    private static TestCurrentUserAccessor Accessor(CurrentUser user) => new(user);

    private static Api.Sharing.ILibraryScopeResolver Scope(CurrentUser user) => TestLibraryScope.For(user);

    private static Task<IResult> Create(MusicHoarderDbContext db, CurrentUser user, string name, params int[] songIds) =>
        PlaylistsEndpoints.CreatePlaylist(
            new CreatePlaylistRequest(name, songIds), db, Accessor(user), Scope(user), Owner, new LibraryPlaylistExportQueue(), default);

    private static Task<IResult> List(MusicHoarderDbContext db, CurrentUser user) =>
        PlaylistsEndpoints.ListPlaylists(db, Accessor(user), Scope(user), Owner, default);

    private static Task<IResult> Add(MusicHoarderDbContext db, CurrentUser user, int id, params int[] songIds) =>
        PlaylistsEndpoints.AddSongs(
            id, new PlaylistSongsRequest(songIds), db, Accessor(user), Scope(user), Owner, new LibraryPlaylistExportQueue(), default);

    private static DbContextOptions<MusicHoarderDbContext> NewOptions() =>
        new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static MusicHoarderDbContext Context(DbContextOptions<MusicHoarderDbContext> options, CurrentUser caller) =>
        new(options, new TestCurrentUserAccessor(caller));

    private static T Value<T>(IResult result) =>
        Assert.IsType<T>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);

    private static int StatusOf(IResult result) =>
        (result as IStatusCodeHttpResult)?.StatusCode ?? StatusCodes.Status200OK;

    private static async Task SeedSongs(DbContextOptions<MusicHoarderDbContext> options, params SongMetadata[] songs)
    {
        await using var db = new MusicHoarderDbContext(options);
        db.Songs.AddRange(songs);
        await db.SaveChangesAsync();
    }

    private static SongMetadata Song(int id, string artist, string album, string title) => new()
    {
        Id = id,
        OwnerUserId = TestUsers.OwnerId,
        SourcePath = $"/music/{id}.mp3",
        FileName = $"{title}.mp3",
        Extension = ".mp3",
        FileSizeBytes = 1,
        Artist = artist,
        AlbumArtist = artist,
        Album = album,
        Title = title,
        LibraryBuildStatus = LibraryBuildStatus.Done,
        DestinationPath = $"/dest/{id}.mp3",
        LastModifiedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        IndexedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static WishlistSource Source(
        WishlistSourceType type, string name, bool recorded,
        string? spotifyPlaylistId = null, string? youTubePlaylistId = null) => new()
    {
        OwnerUserId = TestUsers.OwnerId,
        SourceType = type,
        SpotifyPlaylistId = spotifyPlaylistId,
        YouTubePlaylistId = youTubePlaylistId,
        Name = name,
        CreatedAtUtc = DateTime.UtcNow,
        TracksRecordedAtUtc = recorded ? DateTime.UtcNow : null,
    };

    private static WishlistItem Item(int sourceId, string spotifyId, int? downloadedSongId, DateTime? addedAt = null) => new()
    {
        OwnerUserId = TestUsers.OwnerId,
        WishlistSourceId = sourceId,
        SpotifyTrackId = spotifyId,
        Title = spotifyId,
        Artist = "Artist",
        SpotifyAddedAtUtc = addedAt,
        DownloadedSongId = downloadedSongId,
        Status = downloadedSongId is null ? WishlistItemStatus.Pending : WishlistItemStatus.Downloaded,
        CreatedAtUtc = DateTime.UtcNow,
        UpdatedAtUtc = DateTime.UtcNow,
    };

    private sealed class RecordingExporter : ILibraryPlaylistExporter
    {
        public List<string> Removed { get; } = [];

        public Task<LibraryPlaylistExportResult> RunAsync(CancellationToken ct = default) =>
            Task.FromResult(new LibraryPlaylistExportResult(0, 0, 0));

        public Task RemoveFileAsync(string? filePath, CancellationToken ct = default)
        {
            if (filePath is not null) Removed.Add(filePath);
            return Task.CompletedTask;
        }
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Spotify;
using MusicHoarder.Api.Tests.Auth;

namespace MusicHoarder.Api.Tests.Spotify;

public class PlaylistExportServiceTests
{
    [Fact]
    public async Task RunExport_WritesLikedSongsM3uWithOnlyBuiltMatches_InOrder()
    {
        using var temp = new TempDir();
        await using var db = CreateDb();

        // Two built library tracks (matched by Spotify id) + one matched-but-not-built song.
        SeedBuilt(db, id: 1, spotifyId: "sp:1", artist: "Artist A", title: "First", dest: Path.Combine(temp.Root, "Artist A", "2020 - Album", "01 - First.flac"));
        SeedBuilt(db, id: 2, spotifyId: "sp:2", artist: "Artist B", title: "Second", dest: Path.Combine(temp.Root, "Artist B", "2021 - Album", "02 - Second.flac"));
        SeedUnbuilt(db, id: 3, spotifyId: "sp:3", artist: "Artist C", title: "Third");
        Subscribe(db, ExportedPlaylistKind.LikedSongs, null, "Liked Songs");
        await db.SaveChangesAsync();

        // Liked songs in Spotify order: sp:1, then sp:3 (not built → skipped), then sp:2, then sp:miss (no match).
        var liked = new SpotifyLikedSongsResponse(4, 0, 50, new[]
        {
            Track("sp:1", "Artist A", "First"),
            Track("sp:3", "Artist C", "Third"),
            Track("sp:2", "Artist B", "Second"),
            Track("sp:miss", "Nobody", "Nothing"),
        });

        var service = CreateService(db, new StubApi(liked), temp.Root);
        var result = await service.RunExportAsync();

        Assert.True(result.Ran);
        Assert.Equal(1, result.PlaylistsWritten);

        var liledFile = Path.Combine(temp.Root, "Playlists", "Liked Songs.m3u8");
        Assert.True(File.Exists(liledFile));
        var content = await File.ReadAllTextAsync(liledFile);
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Header + 2 built tracks (sp:1 then sp:2), with sp:3 (unbuilt) and sp:miss (unmatched) skipped.
        Assert.Equal("#EXTM3U", lines[0]);
        Assert.Equal("#EXTINF:200,Artist A - First", lines[1]);
        Assert.Equal("../Artist A/2020 - Album/01 - First.flac", lines[2]);
        Assert.Equal("#EXTINF:200,Artist B - Second", lines[3]);
        Assert.Equal("../Artist B/2021 - Album/02 - Second.flac", lines[4]);
        Assert.DoesNotContain("Third", content);
        Assert.DoesNotContain("Nothing", content);

        // Coverage row: 4 Spotify tracks, 2 written.
        var row = await db.ExportedPlaylists.IgnoreQueryFilters().SingleAsync(e => e.Kind == ExportedPlaylistKind.LikedSongs);
        Assert.Equal(4, row.SpotifyTrackTotal);
        Assert.Equal(2, row.MatchedTrackCount);
        Assert.Equal(liledFile, row.FilePath);
    }

    [Fact]
    public async Task RunExport_RemovesOrphanedFileAndRow_WhenPlaylistGone()
    {
        using var temp = new TempDir();
        await using var db = CreateDb();
        await db.SaveChangesAsync();

        // Pre-existing export row + file for a playlist that no longer exists on Spotify.
        var playlistsDir = Path.Combine(temp.Root, "Playlists");
        Directory.CreateDirectory(playlistsDir);
        var orphanFile = Path.Combine(playlistsDir, "Old Playlist.m3u8");
        await File.WriteAllTextAsync(orphanFile, "#EXTM3U\n");
        db.ExportedPlaylists.Add(new ExportedPlaylist
        {
            OwnerUserId = WellKnownUsers.OwnerId,
            Kind = ExportedPlaylistKind.Playlist,
            SpotifyPlaylistId = "gone",
            Name = "Old Playlist",
            FilePath = orphanFile,
            UpdatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        // Spotify now returns no playlists and no liked songs.
        var service = CreateService(db, new StubApi(new SpotifyLikedSongsResponse(0, 0, 50, [])), temp.Root);
        await service.RunExportAsync();

        Assert.False(File.Exists(orphanFile));
        Assert.False(await db.ExportedPlaylists.IgnoreQueryFilters().AnyAsync(e => e.SpotifyPlaylistId == "gone"));
    }

    [Fact]
    public async Task RunExport_ExportsPlaylistsInPlaylistOrder()
    {
        using var temp = new TempDir();
        await using var db = CreateDb();
        SeedBuilt(db, 1, "sp:a", "A", "Aaa", Path.Combine(temp.Root, "A", "Album", "01 - Aaa.flac"));
        SeedBuilt(db, 2, "sp:b", "B", "Bbb", Path.Combine(temp.Root, "B", "Album", "01 - Bbb.flac"));
        Subscribe(db, ExportedPlaylistKind.Playlist, "pl1", "My Mix");
        await db.SaveChangesAsync();

        var playlistTracks = new SpotifyPlaylistTracksResponse(2, 0, 50, new[]
        {
            Track("sp:b", "B", "Bbb"),
            Track("sp:a", "A", "Aaa"),
        });
        var api = new StubApi(
            new SpotifyLikedSongsResponse(0, 0, 50, []),
            playlists: new SpotifyPlaylistsResponse(new[] { new SpotifyPlaylistItem("pl1", "My Mix", null, null, 2, "me") }),
            playlistTracks: playlistTracks);

        var service = CreateService(db, api, temp.Root);
        await service.RunExportAsync();

        var file = Path.Combine(temp.Root, "Playlists", "My Mix.m3u8");
        Assert.True(File.Exists(file));
        var lines = (await File.ReadAllTextAsync(file)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // Order preserved: sp:b before sp:a.
        Assert.Equal("../B/Album/01 - Bbb.flac", lines[2]);
        Assert.Equal("../A/Album/01 - Aaa.flac", lines[4]);
    }

    [Fact]
    public async Task RunExport_MatchesBuiltTracks_WhenAmbientUserFilterWouldExcludeThem()
    {
        // Regression: the export runs from a Task.Run continuation where the request's HttpContext is
        // gone, so the DbContext's global query filter resolves to a user that does NOT own the songs
        // and would exclude every one of them (empty library index → 0 matches). Build the context
        // with a non-owner accessor to reproduce a filter that hides the owner's songs, and assert the
        // match still lands (the matcher must bypass the ambient filter).
        using var temp = new TempDir();
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        var nonOwner = new CurrentUser(new Guid("11111111-1111-1111-1111-111111111111"), "other@test.local", UserRole.Owner, "Other");
        await using var db = new MusicHoarderDbContext(options, new TestCurrentUserAccessor(nonOwner));

        SeedBuilt(db, 1, "sp:x", "DaBaby", "POP DAT THANG", Path.Combine(temp.Root, "DaBaby", "Album", "01 - POP DAT THANG.flac"));
        Subscribe(db, ExportedPlaylistKind.LikedSongs, null, "Liked Songs");
        await db.SaveChangesAsync();

        // Sanity: with the empty-user ambient filter, a filtered read sees nothing.
        Assert.Empty(await db.Songs.ToListAsync());

        var liked = new SpotifyLikedSongsResponse(1, 0, 50, new[] { Track("sp:liked", "DaBaby", "POP DAT THANG") });
        var service = CreateService(db, new StubApi(liked), temp.Root);
        var result = await service.RunExportAsync();

        Assert.Equal(1, result.MatchedTracks);
        var content = await File.ReadAllTextAsync(Path.Combine(temp.Root, "Playlists", "Liked Songs.m3u8"));
        Assert.Contains("../DaBaby/Album/01 - POP DAT THANG.flac", content);
    }

    [Fact]
    public async Task RunExport_WritesNothing_WhenNoSubscriptions()
    {
        using var temp = new TempDir();
        await using var db = CreateDb();
        SeedBuilt(db, 1, "sp:1", "Artist A", "First", Path.Combine(temp.Root, "Artist A", "Album", "01 - First.flac"));
        await db.SaveChangesAsync();

        // Liked Songs available on Spotify, but the owner hasn't subscribed → opt-in means no export.
        var liked = new SpotifyLikedSongsResponse(1, 0, 50, new[] { Track("sp:1", "Artist A", "First") });
        var service = CreateService(db, new StubApi(liked), temp.Root);
        var result = await service.RunExportAsync();

        Assert.True(result.Ran);
        Assert.Equal(0, result.PlaylistsWritten);
        Assert.False(File.Exists(Path.Combine(temp.Root, "Playlists", "Liked Songs.m3u8")));
        Assert.False(await db.ExportedPlaylists.IgnoreQueryFilters().AnyAsync());
    }

    [Fact]
    public async Task RunExport_SkipsUnsubscribedPlaylist()
    {
        using var temp = new TempDir();
        await using var db = CreateDb();
        SeedBuilt(db, 1, "sp:a", "A", "Aaa", Path.Combine(temp.Root, "A", "Album", "01 - Aaa.flac"));
        await db.SaveChangesAsync();

        // Spotify exposes a playlist, but it is not subscribed, so it must not be written.
        var api = new StubApi(
            new SpotifyLikedSongsResponse(0, 0, 50, []),
            playlists: new SpotifyPlaylistsResponse(new[] { new SpotifyPlaylistItem("pl1", "My Mix", null, null, 1, "me") }),
            playlistTracks: new SpotifyPlaylistTracksResponse(1, 0, 50, new[] { Track("sp:a", "A", "Aaa") }));

        var service = CreateService(db, api, temp.Root);
        var result = await service.RunExportAsync();

        Assert.Equal(0, result.PlaylistsWritten);
        Assert.False(File.Exists(Path.Combine(temp.Root, "Playlists", "My Mix.m3u8")));
        Assert.False(await db.ExportedPlaylists.IgnoreQueryFilters().AnyAsync());
    }

    [Fact]
    public async Task Subscribe_ThenRunExport_WritesTheSubscribedCollection()
    {
        using var temp = new TempDir();
        await using var db = CreateDb();
        SeedBuilt(db, 1, "sp:1", "Artist A", "First", Path.Combine(temp.Root, "Artist A", "Album", "01 - First.flac"));
        await db.SaveChangesAsync();

        var liked = new SpotifyLikedSongsResponse(1, 0, 50, new[] { Track("sp:1", "Artist A", "First") });
        var service = CreateService(db, new StubApi(liked), temp.Root);

        var row = await service.SubscribeAsync(ExportedPlaylistKind.LikedSongs, null, "Liked Songs");
        Assert.True(row.Id > 0);

        var result = await service.RunExportAsync();
        Assert.Equal(1, result.PlaylistsWritten);
        Assert.True(File.Exists(Path.Combine(temp.Root, "Playlists", "Liked Songs.m3u8")));
    }

    [Fact]
    public async Task Unsubscribe_DeletesFileAndRow()
    {
        using var temp = new TempDir();
        await using var db = CreateDb();
        SeedBuilt(db, 1, "sp:1", "Artist A", "First", Path.Combine(temp.Root, "Artist A", "Album", "01 - First.flac"));
        Subscribe(db, ExportedPlaylistKind.LikedSongs, null, "Liked Songs");
        await db.SaveChangesAsync();

        var liked = new SpotifyLikedSongsResponse(1, 0, 50, new[] { Track("sp:1", "Artist A", "First") });
        var service = CreateService(db, new StubApi(liked), temp.Root);

        await service.RunExportAsync();
        var file = Path.Combine(temp.Root, "Playlists", "Liked Songs.m3u8");
        Assert.True(File.Exists(file));

        var id = (await db.ExportedPlaylists.IgnoreQueryFilters().SingleAsync()).Id;
        var removed = await service.UnsubscribeAsync(id);

        Assert.True(removed);
        Assert.False(File.Exists(file));
        Assert.False(await db.ExportedPlaylists.IgnoreQueryFilters().AnyAsync());
    }

    #region Helpers

    private static SpotifyTrackItem Track(string id, string artist, string title) =>
        new(id, title, artist, "Album", null, 200_000, DateTime.UtcNow);

    private static MusicHoarderDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MusicHoarderDbContext(options);
    }

    private static void SeedBuilt(MusicHoarderDbContext db, int id, string spotifyId, string artist, string title, string dest)
    {
        db.Songs.Add(new SongMetadata
        {
            Id = id,
            OwnerUserId = WellKnownUsers.OwnerId,
            SourcePath = $"/src/{id}.flac",
            FileSizeBytes = 1000 + id,
            FileName = $"{id}.flac",
            Extension = ".flac",
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            SpotifyId = spotifyId,
            Artist = artist,
            Title = title,
            DurationSeconds = 200,
            LibraryBuildStatus = LibraryBuildStatus.Done,
            DestinationPath = dest,
        });
    }

    private static void SeedUnbuilt(MusicHoarderDbContext db, int id, string spotifyId, string artist, string title)
    {
        db.Songs.Add(new SongMetadata
        {
            Id = id,
            OwnerUserId = WellKnownUsers.OwnerId,
            SourcePath = $"/src/{id}.flac",
            FileSizeBytes = 1000 + id,
            FileName = $"{id}.flac",
            Extension = ".flac",
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            SpotifyId = spotifyId,
            Artist = artist,
            Title = title,
            DurationSeconds = 200,
            LibraryBuildStatus = LibraryBuildStatus.Pending,
            DestinationPath = null,
        });
    }

    private static void Subscribe(MusicHoarderDbContext db, ExportedPlaylistKind kind, string? spotifyPlaylistId, string name)
    {
        // The presence of an ExportedPlaylist row IS the subscription (export is opt-in).
        db.ExportedPlaylists.Add(new ExportedPlaylist
        {
            OwnerUserId = WellKnownUsers.OwnerId,
            Kind = kind,
            SpotifyPlaylistId = spotifyPlaylistId,
            Name = name,
            FilePath = string.Empty,
            UpdatedAtUtc = DateTime.UtcNow,
        });
    }

    private static PlaylistExportService CreateService(MusicHoarderDbContext db, ISpotifyApiService api, string destinationRoot)
    {
        var scopeFactory = new TestScopeFactory(db);
        var owner = new TestOwnerLookupService();
        var comparison = new SpotifyLibraryComparisonService(api, scopeFactory, owner, NullLogger<SpotifyLibraryComparisonService>.Instance);
        var options = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            DestinationDirectory = destinationRoot,
            PlaylistsFolderName = "Playlists",
        });
        return new PlaylistExportService(
            api, comparison, scopeFactory, owner, new M3uPlaylistWriter(), options,
            NullLogger<PlaylistExportService>.Instance);
    }

    private sealed class StubApi(
        SpotifyLikedSongsResponse liked,
        SpotifyPlaylistsResponse? playlists = null,
        SpotifyPlaylistTracksResponse? playlistTracks = null) : ISpotifyApiService
    {
        public Task<SpotifyLikedSongsResponse> GetLikedSongsAsync(int offset = 0, int limit = 50, CancellationToken ct = default)
        {
            var items = liked.Items.Skip(offset).Take(limit).ToList();
            return Task.FromResult(new SpotifyLikedSongsResponse(liked.Total, offset, limit, items));
        }

        public Task<SpotifyPlaylistsResponse> GetPlaylistsAsync(CancellationToken ct = default) =>
            Task.FromResult(playlists ?? new SpotifyPlaylistsResponse(Array.Empty<SpotifyPlaylistItem>()));

        public Task<SpotifyPlaylistTracksResponse> GetPlaylistTracksAsync(string playlistId, int offset = 0, int limit = 50, CancellationToken ct = default)
        {
            var src = playlistTracks ?? new SpotifyPlaylistTracksResponse(0, 0, 50, Array.Empty<SpotifyTrackItem>());
            var items = src.Items.Skip(offset).Take(limit).ToList();
            return Task.FromResult(new SpotifyPlaylistTracksResponse(src.Total, offset, limit, items));
        }
    }

    private sealed class TestScopeFactory(MusicHoarderDbContext db) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new TestScope(new TestServiceProvider(db));
    }

    private sealed class TestScope(IServiceProvider provider) : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = provider;
        public void Dispose() { }
    }

    private sealed class TestServiceProvider(MusicHoarderDbContext db) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(MusicHoarderDbContext) ? db : null;
    }

    private sealed class TempDir : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "mh-export-test-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Root);
        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }

    #endregion
}

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

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Playlists;
using MusicHoarder.Api.Tests.Auth;

namespace MusicHoarder.Api.Tests.Playlists;

/// <summary>
/// MusicHoarder's own playlists as <c>.m3u8</c> files in the destination library: built tracks only,
/// in playlist order, the library owner's playlists only, sharing the folder with Playlist sync
/// without ever taking one of its file names.
/// </summary>
public sealed class LibraryPlaylistExporterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mh-library-playlists-" + Guid.NewGuid().ToString("N"));

    public LibraryPlaylistExporterTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string PlaylistsDir => Path.Combine(_root, "Playlists");

    [Fact]
    public async Task Writes_the_built_tracks_in_playlist_order_and_leaves_an_unchanged_file_alone()
    {
        await using var db = CreateDb();
        db.Songs.AddRange(
            Built(1, "Artist A", "First", "Artist A/Album/01 - First.flac"),
            Built(2, "Artist B", "Second", "Artist B/Album/02 - Second.flac"),
            Unbuilt(3, "Artist C", "Third"));
        db.Playlists.Add(Playlist("Road trip", TestUsers.OwnerId, 2, 3, 1));
        await db.SaveChangesAsync();
        var exporter = CreateExporter(db);

        var first = await exporter.RunAsync();

        var file = Path.Combine(PlaylistsDir, "Road trip.m3u8");
        Assert.Equal(1, first.Written);
        Assert.Equal(
            "#EXTM3U\n#EXTINF:200,Artist B - Second\n../Artist B/Album/02 - Second.flac\n#EXTINF:200,Artist A - First\n../Artist A/Album/01 - First.flac\n",
            await File.ReadAllTextAsync(file));
        Assert.Equal(file, (await db.Playlists.IgnoreQueryFilters().SingleAsync()).ExportFilePath);

        var second = await exporter.RunAsync();
        Assert.Equal(0, second.Written);
        Assert.Equal(1, second.Unchanged);
    }

    [Fact]
    public async Task A_rename_moves_the_file()
    {
        await using var db = CreateDb();
        db.Songs.Add(Built(1, "Artist A", "First", "Artist A/Album/01 - First.flac"));
        var playlist = Playlist("Old name", TestUsers.OwnerId, 1);
        db.Playlists.Add(playlist);
        await db.SaveChangesAsync();
        var exporter = CreateExporter(db);
        await exporter.RunAsync();

        playlist.Name = "New name";
        await db.SaveChangesAsync();
        var result = await exporter.RunAsync();

        Assert.Equal(1, result.Removed);
        Assert.False(File.Exists(Path.Combine(PlaylistsDir, "Old name.m3u8")));
        Assert.True(File.Exists(Path.Combine(PlaylistsDir, "New name.m3u8")));
    }

    [Fact]
    public async Task Never_takes_a_file_name_Playlist_sync_holds()
    {
        await using var db = CreateDb();
        db.Songs.Add(Built(1, "Artist A", "First", "Artist A/Album/01 - First.flac"));
        db.ExportedPlaylists.Add(new ExportedPlaylist
        {
            OwnerUserId = TestUsers.OwnerId,
            Kind = ExportedPlaylistKind.Playlist,
            SpotifyPlaylistId = "sp",
            Name = "Chill",
            FilePath = Path.Combine(PlaylistsDir, "Chill.m3u8"),
            UpdatedAtUtc = DateTime.UtcNow,
        });
        db.Playlists.Add(Playlist("Chill", TestUsers.OwnerId, 1));
        await db.SaveChangesAsync();

        await CreateExporter(db).RunAsync();

        Assert.False(File.Exists(Path.Combine(PlaylistsDir, "Chill.m3u8")));
        Assert.True(File.Exists(Path.Combine(PlaylistsDir, "Chill (2).m3u8")));
    }

    [Fact]
    public async Task Exports_only_the_library_owners_playlists_that_ask_for_it()
    {
        await using var db = CreateDb();
        db.Songs.Add(Built(1, "Artist A", "First", "Artist A/Album/01 - First.flac"));
        db.Playlists.Add(Playlist("A member's", TestUsers.FriendId, 1));
        var off = Playlist("Not exported", TestUsers.OwnerId, 1);
        off.ExportToLibrary = false;
        db.Playlists.Add(off);
        await db.SaveChangesAsync();

        var result = await CreateExporter(db).RunAsync();

        Assert.Equal(0, result.Written);
        Assert.False(Directory.Exists(PlaylistsDir) && Directory.EnumerateFiles(PlaylistsDir).Any());
    }

    [Theory]
    [InlineData(null, "Mix.m3u8")]
    [InlineData("/x/Playlists/Mix (3).m3u8", "Mix (3).m3u8")]  // keeps its numbered file
    [InlineData("/x/Playlists/Old.m3u8", "Mix.m3u8")]          // renamed
    [InlineData("/x/Playlists/mix.m3u8", "Mix.m3u8")]          // a change of case is a rename too
    public void File_names_are_stable_per_playlist(string? current, string expected)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(expected, LibraryPlaylistExporter.UniqueFileName("Mix", current, used));
    }

    [Fact]
    public void A_taken_name_gets_the_next_number()
    {
        var used = new HashSet<string>(["Mix.m3u8", "Mix (2).m3u8"], StringComparer.OrdinalIgnoreCase);
        Assert.Equal("Mix (3).m3u8", LibraryPlaylistExporter.UniqueFileName("Mix", null, used));
    }

    // --- helpers -----------------------------------------------------------------------------------

    private LibraryPlaylistExporter CreateExporter(MusicHoarderDbContext db) =>
        new(
            new SingleContextScopeFactory(db),
            new TestOwnerLookupService(),
            new M3uPlaylistWriter(),
            Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
            {
                DestinationDirectory = _root,
                PlaylistsFolderName = "Playlists",
            }),
            NullLogger<LibraryPlaylistExporter>.Instance);

    private static MusicHoarderDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private static Playlist Playlist(string name, Guid owner, params int[] songIds)
    {
        var playlist = new Playlist
        {
            OwnerUserId = owner,
            Name = name,
            ExportToLibrary = true,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        };
        for (var i = 0; i < songIds.Length; i++)
            playlist.Entries.Add(new PlaylistEntry { SongId = songIds[i], Position = i, AddedAtUtc = DateTime.UtcNow });
        return playlist;
    }

    private SongMetadata Built(int id, string artist, string title, string relativeDestination)
    {
        var song = Unbuilt(id, artist, title);
        song.LibraryBuildStatus = LibraryBuildStatus.Done;
        song.DestinationPath = Path.Combine(_root, relativeDestination);
        return song;
    }

    private static SongMetadata Unbuilt(int id, string artist, string title) => new()
    {
        Id = id,
        OwnerUserId = TestUsers.OwnerId,
        SourcePath = $"/music/{id}.flac",
        FileName = $"{title}.flac",
        Extension = ".flac",
        FileSizeBytes = 1,
        Artist = artist,
        Title = title,
        DurationSeconds = 200,
        LastModifiedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        IndexedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private sealed class SingleContextScopeFactory(MusicHoarderDbContext db) : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        public IServiceScope CreateScope() => this;
        public IServiceProvider ServiceProvider => this;
        public object? GetService(Type serviceType) => serviceType == typeof(MusicHoarderDbContext) ? db : null;
        public void Dispose() { }
    }
}

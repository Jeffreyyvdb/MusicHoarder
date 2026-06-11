using System.IO.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Artwork;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Scanner;

namespace MusicHoarder.Api.Tests.Auth;

/// <summary>
/// When <see cref="MusicEnricherOptions.DemoMediaDirectory"/> points at a folder of real audio, the
/// demo seeder ingests it into the demo account as terminal, playable rows (and skips the synthetic
/// seed). When it's unset/empty, the synthetic seed runs instead — the self-host / PR-preview path.
/// </summary>
public class DemoSeederRealMediaTests : IDisposable
{
    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    private readonly List<string> _tempDirs = [];

    [Fact]
    public async Task seeds_real_playable_songs_from_media_directory_and_skips_synthetic()
    {
        var mediaDir = NewTempDir();
        var file = Path.Combine(mediaDir, "Demo Band", "Demo LP", "01 Demo Song.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.Copy(Path.Combine(FixtureDir, "silence.mp3"), file);
        using (var tag = TagLib.File.Create(file))
        {
            tag.Tag.Performers = ["Demo Band"];
            tag.Tag.Album = "Demo LP";
            tag.Tag.Title = "Demo Song";
            tag.Tag.Track = 1;
            tag.Save();
        }

        var (svc, ctx) = MakeSeeder(mediaDir);
        await svc.StartAsync(default);

        await using var db = ctx();
        var demoSongs = await db.Songs.IgnoreQueryFilters()
            .Where(s => s.OwnerUserId == WellKnownUsers.DemoId).ToListAsync();
        var song = Assert.Single(demoSongs);

        Assert.False(song.IsSynthetic);                                  // real → /stream + /cover work
        Assert.Equal(EnrichmentStatus.Matched, song.EnrichmentStatus);
        Assert.Equal(LibraryBuildStatus.Done, song.LibraryBuildStatus);
        Assert.Equal(file, song.SourcePath);
        Assert.Equal(file, song.DestinationPath);
        Assert.Equal("Demo Band", song.Artist);
        Assert.Equal("Demo LP", song.Album);
        Assert.Equal("Demo Song", song.Title);
        Assert.False(string.IsNullOrEmpty(song.Fingerprint));            // keeps it out of the fingerprint sweep
        Assert.Equal(LyricsStatus.NotFound, song.LyricsStatus);          // keeps it out of the lyrics sweep
    }

    [Fact]
    public async Task inconsistent_album_artist_tags_unify_to_folder_artist()
    {
        // Two tracks in one album folder tagged with different album-artists ("Lauryn Hill" vs
        // "Ms. Lauryn Hill") must both adopt the folder's artist so the Albums page shows one album.
        var mediaDir = NewTempDir();
        var albumDir = Path.Combine(mediaDir, "Lauryn Hill", "1998 - The Miseducation of Lauryn Hill");
        Directory.CreateDirectory(albumDir);
        foreach (var (n, performer) in new[] { (1, "Lauryn Hill"), (2, "Ms. Lauryn Hill, D'Angelo") })
        {
            var f = Path.Combine(albumDir, $"0{n} Track.mp3");
            File.Copy(Path.Combine(FixtureDir, "silence.mp3"), f);
            using var tag = TagLib.File.Create(f);
            tag.Tag.Performers = [performer];
            tag.Tag.AlbumArtists = [performer];           // intentionally inconsistent across the album
            tag.Tag.Album = "The Miseducation of Lauryn Hill";
            tag.Tag.Title = $"Track {n}";
            tag.Tag.Track = (uint)n;
            tag.Save();
        }

        var (svc, ctx) = MakeSeeder(mediaDir);
        await svc.StartAsync(default);

        await using var db = ctx();
        var songs = await db.Songs.IgnoreQueryFilters()
            .Where(s => s.OwnerUserId == WellKnownUsers.DemoId).ToListAsync();
        Assert.Equal(2, songs.Count);
        Assert.All(songs, s => Assert.Equal("Lauryn Hill", s.AlbumArtist)); // unified → one album group
        // Per-track artist (incl. the feature) is preserved.
        Assert.Contains(songs, s => s.Artist == "Ms. Lauryn Hill, D'Angelo");
    }

    [Fact]
    public async Task untagged_file_falls_back_to_folder_and_filename()
    {
        var mediaDir = NewTempDir();
        var file = Path.Combine(mediaDir, "Folder Artist", "Folder Album", "untitled.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.Copy(Path.Combine(FixtureDir, "silence.mp3"), file);

        var (svc, ctx) = MakeSeeder(mediaDir);
        await svc.StartAsync(default);

        await using var db = ctx();
        var song = await db.Songs.IgnoreQueryFilters()
            .SingleAsync(s => s.OwnerUserId == WellKnownUsers.DemoId);
        Assert.Equal("Folder Artist", song.Artist);
        Assert.Equal("Folder Album", song.Album);
        Assert.Equal("untitled", song.Title);
    }

    [Fact]
    public async Task reboot_heals_polluted_demo_rows_and_deletes_stray_destination_files()
    {
        // Simulates what the pre-exclusion all-tenant sweeps did to the demo library: provider
        // attempts + a flipped status on one row, a builder-re-pointed DestinationPath on another.
        // The next boot must wipe + re-seed and remove the stray copy from the owner's library.
        var mediaDir = NewTempDir();
        AddMediaFile(mediaDir, "Demo Band", "Demo LP", 1, "Song One");
        AddMediaFile(mediaDir, "Demo Band", "Demo LP", 2, "Song Two");

        var cleaner = new RecordingCleaner();
        var (svc, ctx) = MakeSeeder(mediaDir, cleaner);
        await svc.StartAsync(default);

        const string strayPath = "/dest/Demo Band/Demo LP/01 Song One.mp3";
        await using (var db = ctx())
        {
            var rows = await db.Songs.IgnoreQueryFilters().Include(s => s.ProviderAttempts)
                .Where(s => s.OwnerUserId == WellKnownUsers.DemoId).OrderBy(s => s.Id).ToListAsync();
            rows[0].ProviderAttempts.Add(new SongProviderAttempt
            {
                SongId = rows[0].Id,
                Provider = EnrichmentProvider.AcoustID,
                Status = ProviderAttemptStatus.Matched,
                AttemptedAtUtc = DateTime.UtcNow,
            });
            rows[0].EnrichmentStatus = EnrichmentStatus.NeedsReview;
            rows[1].PreviousDestinationPath = rows[1].DestinationPath;
            rows[1].DestinationPath = strayPath;
            await db.SaveChangesAsync();
        }

        await svc.StartAsync(default); // next boot: heal + re-seed

        await using var db2 = ctx();
        var healed = await db2.Songs.IgnoreQueryFilters().Include(s => s.ProviderAttempts)
            .Where(s => s.OwnerUserId == WellKnownUsers.DemoId).ToListAsync();
        Assert.Equal(2, healed.Count);
        Assert.All(healed, s =>
        {
            Assert.Equal(EnrichmentStatus.Matched, s.EnrichmentStatus);
            Assert.Equal(LibraryBuildStatus.Done, s.LibraryBuildStatus);
            Assert.Equal(s.SourcePath, s.DestinationPath);
            Assert.Null(s.PreviousDestinationPath);
            Assert.Empty(s.ProviderAttempts);
        });
        Assert.Equal(new[] { strayPath }, cleaner.Deleted);
    }

    [Fact]
    public async Task heal_keeps_destination_file_claimed_by_a_non_demo_row()
    {
        var mediaDir = NewTempDir();
        AddMediaFile(mediaDir, "Demo Band", "Demo LP", 1, "Song One");

        var cleaner = new RecordingCleaner();
        var (svc, ctx) = MakeSeeder(mediaDir, cleaner);
        await svc.StartAsync(default);

        const string sharedPath = "/dest/Demo Band/Demo LP/01 Song One.mp3";
        await using (var db = ctx())
        {
            var demoRow = await db.Songs.IgnoreQueryFilters()
                .SingleAsync(s => s.OwnerUserId == WellKnownUsers.DemoId);
            demoRow.DestinationPath = sharedPath;
            // The owner's own build output landed at the same path — the heal must not delete it.
            db.Songs.Add(new SongMetadata
            {
                OwnerUserId = WellKnownUsers.OwnerId,
                SourcePath = "/source/owner.mp3",
                FileName = "owner.mp3",
                Extension = ".mp3",
                FileSizeBytes = 1,
                LastModifiedUtc = DateTime.UtcNow,
                IndexedAtUtc = DateTime.UtcNow,
                LibraryBuildStatus = LibraryBuildStatus.Done,
                DestinationPath = sharedPath,
            });
            await db.SaveChangesAsync();
        }

        await svc.StartAsync(default);

        Assert.Empty(cleaner.Deleted);
        await using var db2 = ctx();
        var healed = await db2.Songs.IgnoreQueryFilters()
            .Where(s => s.OwnerUserId == WellKnownUsers.DemoId).ToListAsync();
        var song = Assert.Single(healed);
        Assert.Equal(song.SourcePath, song.DestinationPath); // re-seeded fresh all the same
    }

    [Fact]
    public async Task clean_demo_rows_are_left_untouched_on_reboot()
    {
        var mediaDir = NewTempDir();
        AddMediaFile(mediaDir, "Demo Band", "Demo LP", 1, "Song One");

        var cleaner = new RecordingCleaner();
        var (svc, ctx) = MakeSeeder(mediaDir, cleaner);
        await svc.StartAsync(default);

        int seededId;
        await using (var db = ctx())
            seededId = (await db.Songs.IgnoreQueryFilters()
                .SingleAsync(s => s.OwnerUserId == WellKnownUsers.DemoId)).Id;

        await svc.StartAsync(default);

        await using var db2 = ctx();
        var song = await db2.Songs.IgnoreQueryFilters()
            .SingleAsync(s => s.OwnerUserId == WellKnownUsers.DemoId);
        Assert.Equal(seededId, song.Id); // no wipe/re-seed happened
        Assert.Empty(cleaner.Deleted);
    }

    [Fact]
    public async Task empty_media_directory_falls_back_to_synthetic_seed()
    {
        var mediaDir = NewTempDir(); // exists but has no audio

        var (svc, ctx) = MakeSeeder(mediaDir);
        await svc.StartAsync(default);

        await using var db = ctx();
        var demoSongs = await db.Songs.IgnoreQueryFilters()
            .Where(s => s.OwnerUserId == WellKnownUsers.DemoId).ToListAsync();
        Assert.NotEmpty(demoSongs);
        Assert.All(demoSongs, s => Assert.True(s.IsSynthetic));
    }

    [Fact]
    public async Task unset_media_directory_uses_synthetic_seed()
    {
        var (svc, ctx) = MakeSeeder(demoMediaDir: null);
        await svc.StartAsync(default);

        await using var db = ctx();
        var demoSongs = await db.Songs.IgnoreQueryFilters()
            .Where(s => s.OwnerUserId == WellKnownUsers.DemoId).ToListAsync();
        Assert.NotEmpty(demoSongs);
        Assert.All(demoSongs, s => Assert.True(s.IsSynthetic));
    }

    private (DemoSeederHostedService Service, Func<MusicHoarderDbContext> CreateCtx) MakeSeeder(
        string? demoMediaDir, ILibraryDestinationCleaner? cleaner = null)
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var enricher = new MusicEnricherOptions { DemoMediaDirectory = demoMediaDir, DestinationDirectory = "/dest" };
        var scopeFactory = new StubScopeFactory(
            () => new MusicHoarderDbContext(options),
            new FileScanner(new FileSystem(), new NullFpcalcService(), NullLogger<FileScanner>.Instance),
            new StubCoverResolver(),
            new StubLrcLibService(),
            cleaner ?? new RecordingCleaner());

        var svc = new DemoSeederHostedService(
            scopeFactory,
            new TestOptionsMonitor<AuthOptions>(new AuthOptions()),
            new TestOptionsMonitor<MusicEnricherOptions>(enricher),
            NullLogger<DemoSeederHostedService>.Instance);

        return (svc, () => new MusicHoarderDbContext(options));
    }

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mh-demo-media-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static void AddMediaFile(string mediaDir, string artist, string album, int track, string title)
    {
        var file = Path.Combine(mediaDir, artist, album, $"{track:00} {title}.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.Copy(Path.Combine(FixtureDir, "silence.mp3"), file);
        using var tag = TagLib.File.Create(file);
        tag.Tag.Performers = [artist];
        tag.Tag.Album = album;
        tag.Tag.Title = title;
        tag.Tag.Track = (uint)track;
        tag.Save();
    }

    public void Dispose()
    {
        foreach (var d in _tempDirs)
        {
            try { Directory.Delete(d, recursive: true); } catch { /* best effort */ }
        }
    }

    private sealed class NullFpcalcService : IFpcalcService
    {
        public Task<FpcalcOutcome> GetFingerprintAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult(FpcalcOutcome.Failure("test"));
    }

    private sealed class StubCoverResolver : ICoverArtResolver
    {
        public ResolvedCover? Resolve(string audioFilePath) => null;
        public bool DirectoryHasCoverImage(string? directory) => false;
        public bool HasArtwork(string audioFilePath) => false;
    }

    // No network in tests — return no lyrics so the seeder marks tracks NotFound (best-effort path).
    private sealed class StubLrcLibService : ILrcLibService
    {
        public Task<LyricsResult?> FetchLyricsAsync(SongMetadata song, CancellationToken ct = default) =>
            Task.FromResult<LyricsResult?>(null);
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T> where T : class
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    // Records what the heal asked the destination cleaner to delete; never touches the disk.
    private sealed class RecordingCleaner : ILibraryDestinationCleaner
    {
        public List<string> Deleted { get; } = [];
        public void DeleteManagedPathAndPrune(string path, string destinationRoot) => Deleted.Add(path);
    }

    // A scope whose provider hands back a fresh DbContext plus the scanner/resolver/cleaner the real
    // seeder needs.
    private sealed class StubScopeFactory(
        Func<MusicHoarderDbContext> dbFactory, IFileScanner scanner, ICoverArtResolver resolver,
        ILrcLibService lrcLib, ILibraryDestinationCleaner cleaner) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(new Provider(dbFactory(), scanner, resolver, lrcLib, cleaner));

        private sealed class Scope(IServiceProvider provider) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = provider;
            public void Dispose() { }
        }

        private sealed class Provider(
            MusicHoarderDbContext db, IFileScanner scanner, ICoverArtResolver resolver, ILrcLibService lrcLib,
            ILibraryDestinationCleaner cleaner)
            : IServiceProvider
        {
            public object? GetService(Type serviceType)
            {
                if (serviceType == typeof(MusicHoarderDbContext)) return db;
                if (serviceType == typeof(IFileScanner)) return scanner;
                if (serviceType == typeof(ICoverArtResolver)) return resolver;
                if (serviceType == typeof(ILrcLibService)) return lrcLib;
                if (serviceType == typeof(ILibraryDestinationCleaner)) return cleaner;
                return null;
            }
        }
    }
}

using System.IO.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Artwork;
using MusicHoarder.Api.Auth;
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

    private (DemoSeederHostedService Service, Func<MusicHoarderDbContext> CreateCtx) MakeSeeder(string? demoMediaDir)
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var enricher = new MusicEnricherOptions { DemoMediaDirectory = demoMediaDir };
        var scopeFactory = new StubScopeFactory(
            () => new MusicHoarderDbContext(options),
            new FileScanner(new FileSystem(), new NullFpcalcService(), NullLogger<FileScanner>.Instance),
            new StubCoverResolver());

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

    public void Dispose()
    {
        foreach (var d in _tempDirs)
        {
            try { Directory.Delete(d, recursive: true); } catch { /* best effort */ }
        }
    }

    private sealed class NullFpcalcService : IFpcalcService
    {
        public Task<FpcalcResult?> GetFingerprintAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult<FpcalcResult?>(null);
    }

    private sealed class StubCoverResolver : ICoverArtResolver
    {
        public ResolvedCover? Resolve(string audioFilePath) => null;
        public bool DirectoryHasCoverImage(string? directory) => false;
        public bool HasArtwork(string audioFilePath) => false;
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T> where T : class
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    // A scope whose provider hands back a fresh DbContext plus the scanner/resolver the real seeder needs.
    private sealed class StubScopeFactory(
        Func<MusicHoarderDbContext> dbFactory, IFileScanner scanner, ICoverArtResolver resolver) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(new Provider(dbFactory(), scanner, resolver));

        private sealed class Scope(IServiceProvider provider) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = provider;
            public void Dispose() { }
        }

        private sealed class Provider(MusicHoarderDbContext db, IFileScanner scanner, ICoverArtResolver resolver)
            : IServiceProvider
        {
            public object? GetService(Type serviceType)
            {
                if (serviceType == typeof(MusicHoarderDbContext)) return db;
                if (serviceType == typeof(IFileScanner)) return scanner;
                if (serviceType == typeof(ICoverArtResolver)) return resolver;
                return null;
            }
        }
    }
}

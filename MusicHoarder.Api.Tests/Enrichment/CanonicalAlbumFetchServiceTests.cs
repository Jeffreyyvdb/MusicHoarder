using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Enrichment.AlbumTracklist;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Enrichment;

public class CanonicalAlbumFetchServiceTests
{
    [Fact]
    public async Task Sweep_ReconcilesProviders_AndPersistsCanonicalAlbum()
    {
        await using var db = NewContext();
        db.Songs.Add(MatchedSong("/a.mp3", "Daft Punk", "Discovery"));
        await db.SaveChangesAsync();

        var providers = new IAlbumTracklistProvider[]
        {
            new StubProvider(EnrichmentProvider.MusicBrainzWeb, _ => Candidate(EnrichmentProvider.MusicBrainzWeb, "Discovery")),
            new StubProvider(EnrichmentProvider.Deezer, _ => Candidate(EnrichmentProvider.Deezer, "Discovery")),
        };

        var fetched = await CreateService(db, providers).RunSweepAsync(CancellationToken.None);

        Assert.Equal(1, fetched);
        var row = await db.CanonicalAlbums.Include(a => a.Tracks).SingleAsync();
        Assert.Equal("daft punk", row.ArtistKey);
        Assert.Equal("discovery", row.AlbumKey);
        Assert.Equal(CanonicalAlbumStatus.Fetched, row.Status);
        Assert.Equal("Discovery", row.DisplayTitle);
        Assert.Equal(2, row.Tracks.Count);
        Assert.False(row.TrackCountContested);
        Assert.NotNull(row.SourcesJson);
        Assert.Contains("MusicBrainzWeb", row.Tracks.First().CorroboratingProviders);
    }

    [Fact]
    public async Task Sweep_NoCandidates_MarksNotFound()
    {
        await using var db = NewContext();
        db.Songs.Add(MatchedSong("/a.mp3", "Nobody", "Phantom Album"));
        await db.SaveChangesAsync();

        var providers = new IAlbumTracklistProvider[]
        {
            new StubProvider(EnrichmentProvider.Deezer, _ => null),
        };

        var fetched = await CreateService(db, providers).RunSweepAsync(CancellationToken.None);

        Assert.Equal(0, fetched);
        var row = await db.CanonicalAlbums.SingleAsync();
        Assert.Equal(CanonicalAlbumStatus.NotFound, row.Status);
        Assert.NotNull(row.NextRetryAfterUtc);
        Assert.Empty(db.CanonicalAlbumTracks);
    }

    [Fact]
    public async Task Sweep_PassesSongHintsToProviders()
    {
        await using var db = NewContext();
        var song = MatchedSong("/a.mp3", "Daft Punk", "Discovery");
        song.MusicBrainzReleaseId = "rel-1";
        song.SpotifyId = "track-1";
        song.Isrc = "USABC1234567";
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        AlbumQuery? seen = null;
        var providers = new IAlbumTracklistProvider[]
        {
            new StubProvider(EnrichmentProvider.MusicBrainzWeb, q => { seen = q; return Candidate(EnrichmentProvider.MusicBrainzWeb, "Discovery"); }),
        };

        await CreateService(db, providers).RunSweepAsync(CancellationToken.None);

        Assert.NotNull(seen);
        Assert.Equal("Daft Punk", seen!.AlbumArtist);
        Assert.Equal("Discovery", seen.Album);
        Assert.Equal("rel-1", seen.MusicBrainzReleaseId);
        Assert.Equal("track-1", seen.SpotifyTrackId);
        Assert.Contains("USABC1234567", seen.Isrcs);
    }

    [Fact]
    public async Task Sweep_SkipsAlreadyFetchedAlbums()
    {
        await using var db = NewContext();
        db.Songs.Add(MatchedSong("/a.mp3", "Daft Punk", "Discovery"));
        db.CanonicalAlbums.Add(new CanonicalAlbum
        {
            ArtistKey = "daft punk",
            AlbumKey = "discovery",
            Status = CanonicalAlbumStatus.Fetched,
            FetchedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var called = false;
        var providers = new IAlbumTracklistProvider[]
        {
            new StubProvider(EnrichmentProvider.Deezer, _ => { called = true; return null; }),
        };

        var fetched = await CreateService(db, providers).RunSweepAsync(CancellationToken.None);

        Assert.Equal(0, fetched);
        Assert.False(called);
    }

    [Fact]
    public async Task Sweep_ExcludesDemoTenant()
    {
        await using var db = NewContext();
        var demoSong = MatchedSong("/demo/a.mp3", "Daft Punk", "Discovery");
        demoSong.OwnerUserId = WellKnownUsers.DemoId; // read-only demo library must never spawn fetches
        db.Songs.Add(demoSong);
        await db.SaveChangesAsync();

        var called = false;
        var providers = new IAlbumTracklistProvider[]
        {
            new StubProvider(EnrichmentProvider.Deezer, _ => { called = true; return null; }),
        };

        var fetched = await CreateService(db, providers).RunSweepAsync(CancellationToken.None);

        Assert.Equal(0, fetched);
        Assert.False(called);
        Assert.Empty(db.CanonicalAlbums);
    }

    private static AlbumTracklistCandidate Candidate(EnrichmentProvider source, string title)
        => new(source, $"id-{source}", title, "Daft Punk", 2001, null,
            [
                new CandidateTrack(1, 1, "One More Time", 320000, source == EnrichmentProvider.MusicBrainzWeb ? "rec-1" : null),
                new CandidateTrack(1, 2, "Aerodynamic", 210000, null),
            ]);

    private static CanonicalAlbumFetchService CreateService(MusicHoarderDbContext db, IAlbumTracklistProvider[] providers)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/source",
            DestinationDirectory = "/dest",
        });
        return new CanonicalAlbumFetchService(
            new SimpleScopeFactory(db), providers, options, NullLogger<CanonicalAlbumFetchService>.Instance);
    }

    private static SongMetadata MatchedSong(string sourcePath, string albumArtist, string album) => new()
    {
        OwnerUserId = WellKnownUsers.OwnerId,
        SourcePath = sourcePath,
        FileName = Path.GetFileName(sourcePath),
        Extension = Path.GetExtension(sourcePath),
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        EnrichmentStatus = EnrichmentStatus.Matched,
        AlbumArtist = albumArtist,
        Artist = albumArtist,
        Album = album,
    };

    private static MusicHoarderDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MusicHoarderDbContext(options);
    }

    private sealed class StubProvider(EnrichmentProvider source, Func<AlbumQuery, AlbumTracklistCandidate?> fetch) : IAlbumTracklistProvider
    {
        public EnrichmentProvider Source => source;
        public bool IsEnabled(MusicEnricherOptions options) => true;
        public Task<AlbumTracklistCandidate?> FetchAsync(AlbumQuery query, CancellationToken ct = default)
            => Task.FromResult(fetch(query));
    }

    private sealed class SimpleScopeFactory(MusicHoarderDbContext db) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new SimpleScope(new SimpleProvider(db));
    }

    private sealed class SimpleScope(IServiceProvider provider) : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = provider;
        public void Dispose() { }
    }

    private sealed class SimpleProvider(MusicHoarderDbContext db) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(MusicHoarderDbContext) ? db : null;
    }
}

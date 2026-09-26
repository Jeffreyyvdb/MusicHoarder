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

    [Fact]
    public async Task Sweep_ProviderAnswersWithADifferentAlbum_MarksNotFound()
    {
        // An album no catalog carries still gets an answer from every search: something else.
        await using var db = NewContext();
        db.Songs.Add(MatchedSong("/a.mp3", "Kanye West", "CHIRAQ", title: "Awesome"));
        await db.SaveChangesAsync();

        var providers = new IAlbumTracklistProvider[]
        {
            new StubProvider(EnrichmentProvider.AppleMusic, _ => new AlbumTracklistCandidate(
                EnrichmentProvider.AppleMusic, "530026734", "Pride 'n' Joy (feat. Kanye West) - Single", "Fat Joe",
                2012, null, [new CandidateTrack(1, 1, "Pride 'n' Joy", 300000, null)])),
        };

        var fetched = await CreateService(db, providers).RunSweepAsync(CancellationToken.None);

        Assert.Equal(0, fetched);
        var row = await db.CanonicalAlbums.SingleAsync();
        Assert.Equal(CanonicalAlbumStatus.NotFound, row.Status);
        Assert.Empty(db.CanonicalAlbumTracks);
    }

    [Fact]
    public async Task Sweep_KeepsOnlyTheProvidersThatFoundThisAlbum()
    {
        await using var db = NewContext();
        db.Songs.Add(MatchedSong("/a.mp3", "Daft Punk", "Discovery", title: "One More Time"));
        await db.SaveChangesAsync();

        var providers = new IAlbumTracklistProvider[]
        {
            new StubProvider(EnrichmentProvider.MusicBrainzWeb, _ => Candidate(EnrichmentProvider.MusicBrainzWeb, "Discovery")),
            new StubProvider(EnrichmentProvider.Deezer, _ => Candidate(EnrichmentProvider.Deezer, "Homework")),
        };

        await CreateService(db, providers).RunSweepAsync(CancellationToken.None);

        var row = await db.CanonicalAlbums.SingleAsync();
        Assert.Equal(CanonicalAlbumStatus.Fetched, row.Status);
        var source = Assert.Single(CanonicalAlbumSources.Parse(row.SourcesJson));
        Assert.Equal(EnrichmentProvider.MusicBrainzWeb, source.Provider);
    }

    [Fact]
    public async Task Sweep_FeaturedTrackOnAnotherArtistsAlbum_IsKept()
    {
        // Drake's verse on "Forever" files the song under Drake, but the album is Eminem's.
        await using var db = NewContext();
        db.Songs.Add(MatchedSong("/a.mp3", "Drake", "Relapse: Refill", title: "Forever"));
        await db.SaveChangesAsync();

        var providers = new IAlbumTracklistProvider[]
        {
            new StubProvider(EnrichmentProvider.Deezer, _ => new AlbumTracklistCandidate(
                EnrichmentProvider.Deezer, "464090", "Relapse: Refill", "Eminem", 2009, null,
                [new CandidateTrack(1, 1, "Forever", 357000, null), new CandidateTrack(1, 2, "Crack a Bottle", 297000, null)])),
        };

        Assert.Equal(1, await CreateService(db, providers).RunSweepAsync(CancellationToken.None));
        Assert.Equal("Eminem", (await db.CanonicalAlbums.SingleAsync()).DisplayArtist);
    }

    [Fact]
    public async Task Sweep_AlbumFillDownloadsDoNotVouchForAnAlbum()
    {
        // Album completion once filled 2 Chainz's "So Help Me God!" into a Kanye leak of that name. The
        // downloads share tracks with 2 Chainz's record only because they were fetched from it.
        await using var db = NewContext();
        db.Songs.Add(MatchedSong("/a.mp3", "Kanye West", "So Help Me God", title: "Only One"));
        db.Songs.Add(MatchedSong("/b.mp3", "Kanye West", "So Help Me God", title: "Lambo Wrist",
            intent: SongAcquisitionIntent.AlbumFill));
        await db.SaveChangesAsync();

        var providers = new IAlbumTracklistProvider[]
        {
            new StubProvider(EnrichmentProvider.Deezer, _ => new AlbumTracklistCandidate(
                EnrichmentProvider.Deezer, "184493512", "So Help Me God!", "2 Chainz", 2020, null,
                [new CandidateTrack(1, 1, "Lambo Wrist", 200000, null), new CandidateTrack(1, 2, "Grey Area", 180000, null)])),
        };

        Assert.Equal(0, await CreateService(db, providers).RunSweepAsync(CancellationToken.None));
        Assert.Equal(CanonicalAlbumStatus.NotFound, (await db.CanonicalAlbums.SingleAsync()).Status);
    }

    [Fact]
    public async Task Sweep_StoredRowDescribingADifferentAlbum_IsRetired()
    {
        // Stored before answers had to prove themselves. Retired like a fetch that found nothing, so no
        // reader trusts it; its display fields stay for the fill items that point at it.
        await using var db = NewContext();
        db.Songs.Add(MatchedSong("/a.mp3", "Rigo Dominguez Y Su Grupo Audaz", "CHIRAQ", title: "Awesome"));
        var row = new CanonicalAlbum
        {
            ArtistKey = "rigo dominguez y su grupo audaz",
            AlbumKey = "chiraq",
            DisplayTitle = "20 Éxitos Bailables",
            DisplayArtist = "Rigo Dominguez y Su Grupo Audaz",
            Status = CanonicalAlbumStatus.Fetched,
            FetchedAtUtc = DateTime.UtcNow.AddDays(-2),
        };
        row.Tracks.Add(new CanonicalAlbumTrack { DiscNumber = 1, TrackNumber = 1, Title = "Macumba" });
        db.CanonicalAlbums.Add(row);
        await db.SaveChangesAsync();

        var called = false;
        var providers = new IAlbumTracklistProvider[]
        {
            new StubProvider(EnrichmentProvider.Deezer, _ => { called = true; return null; }),
        };

        await CreateService(db, providers).RunSweepAsync(CancellationToken.None);

        var retired = await db.CanonicalAlbums.Include(a => a.Tracks).SingleAsync();
        Assert.Equal(CanonicalAlbumStatus.NotFound, retired.Status);
        Assert.NotNull(retired.NextRetryAfterUtc);
        Assert.Equal("20 Éxitos Bailables", retired.DisplayTitle);
        Assert.Single(retired.Tracks);
        Assert.False(called); // waits for the retry timer like any NotFound
    }

    [Fact]
    public async Task Sweep_StoredRowForThisAlbum_IsKept()
    {
        await using var db = NewContext();
        db.Songs.Add(MatchedSong("/a.mp3", "Daft Punk", "Discovery", title: "One More Time"));
        db.CanonicalAlbums.Add(new CanonicalAlbum
        {
            ArtistKey = "daft punk",
            AlbumKey = "discovery",
            DisplayTitle = "Discovery",
            DisplayArtist = "Daft Punk",
            Status = CanonicalAlbumStatus.Fetched,
            FetchedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        await CreateService(db, []).RunSweepAsync(CancellationToken.None);

        Assert.Equal(CanonicalAlbumStatus.Fetched, (await db.CanonicalAlbums.SingleAsync()).Status);
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

    private static SongMetadata MatchedSong(
        string sourcePath, string albumArtist, string album, string? title = null,
        SongAcquisitionIntent intent = SongAcquisitionIntent.Explicit) => new()
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
        Title = title,
        AcquisitionIntent = intent,
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

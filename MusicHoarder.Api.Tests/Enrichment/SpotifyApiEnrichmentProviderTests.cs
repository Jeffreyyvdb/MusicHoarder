using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Enrichment.Providers;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Spotify;
using MusicHoarder.Api.Tests.Auth;

namespace MusicHoarder.Api.Tests.Enrichment;

public class SpotifyApiEnrichmentProviderTests
{
    [Fact]
    public async Task TryEnrichAsync_WithCredentialsFromSpotifyOptions_WhenDbEmpty_CallsCatalogSearch()
    {
        await using var db = CreateDb();
        var track = new SpotifyCatalogTrack(
            "id1",
            "Lucid Dreams",
            "Juice WRLD",
            "Album",
            2018,
            1,
            239_000,
            null);
        var catalog = new StubCatalogSearchService(_ => Task.FromResult<IReadOnlyList<SpotifyCatalogTrack>>([track]));
        var spotifyOpts = Microsoft.Extensions.Options.Options.Create(new SpotifyOptions
        {
            ClientId = "cfg-id",
            ClientSecret = "cfg-secret",
        });
        var provider = CreateProvider(db, catalog, spotifyOpts: spotifyOpts);

        var song = new SongMetadata
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            SourcePath = "/a.mp3",
            FileName = "a.mp3",
            Extension = ".mp3",
            FileSizeBytes = 1,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Artist = "Juice WRLD",
            Title = "Lucid Dreams",
            DurationSeconds = 239,
            EnrichmentStatus = EnrichmentStatus.Pending,
        };

        var result = await provider.TryEnrichAsync(song);
        Assert.IsType<ProviderMatched>(result);
        Assert.Equal(1, catalog.CallCount);
    }

    [Fact]
    public async Task TryEnrichAsync_NoSpotifyCredentials_ReturnsNoMatch()
    {
        await using var db = CreateDb();
        var catalog = new StubCatalogSearchService(_ => Task.FromResult<IReadOnlyList<SpotifyCatalogTrack>>([]));
        var provider = CreateProvider(db, catalog);

        var song = new SongMetadata
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            SourcePath = "/a.mp3",
            FileName = "a.mp3",
            Extension = ".mp3",
            FileSizeBytes = 1,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Artist = "A",
            Title = "T",
            EnrichmentStatus = EnrichmentStatus.Pending,
        };

        var result = await provider.TryEnrichAsync(song);
        Assert.IsType<ProviderNoMatch>(result);
        Assert.Equal(0, catalog.CallCount);
    }

    [Fact]
    public async Task TryEnrichAsync_StrongMatch_ReturnsMatchedWithSpotifyIdAndAlbum()
    {
        await using var db = CreateDb();
        db.SpotifySettings.Add(new SpotifySettings {     OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId, ClientId = "id", ClientSecret = "secret" });
        await db.SaveChangesAsync();

        var track = new SpotifyCatalogTrack(
            "spotifyTrackId",
            "Lucid Dreams",
            "Juice WRLD",
            "Goodbye & Good Riddance",
            2018,
            3,
            239_000,
            "USUM71807840");

        var catalog = new StubCatalogSearchService(_ => Task.FromResult<IReadOnlyList<SpotifyCatalogTrack>>([track]));
        var opts = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/s",
            DestinationDirectory = "/d",
            SpotifyApiMinConfidence = 0.7,
            SpotifyApiMatchedThreshold = 0.85,
            SpotifyApiIsrcConfidenceBoost = 0.12,
            SpotifyApiDurationMismatchPenalty = 0.7,
            SpotifyApiDurationDeltaThresholdSeconds = 20,
        });
        var provider = CreateProvider(db, catalog, opts);

        var song = new SongMetadata
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            SourcePath = "/a.mp3",
            FileName = "a.mp3",
            Extension = ".mp3",
            FileSizeBytes = 1,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Artist = "Juice WRLD",
            Title = "Lucid Dreams",
            DurationSeconds = 239,
            EnrichmentStatus = EnrichmentStatus.Pending,
        };

        var result = await provider.TryEnrichAsync(song);
        var matched = Assert.IsType<ProviderMatched>(result);
        Assert.Equal(EnrichmentStatus.Matched, matched.Result.RecommendedStatus);
        Assert.Equal("spotifyTrackId", matched.Result.SpotifyId);
        Assert.Equal("Goodbye & Good Riddance", matched.Result.Album);
        Assert.Equal(2018, matched.Result.Year);
        Assert.Equal(3, matched.Result.TrackNumber);
        Assert.Equal("SpotifyAPI", matched.Result.MatchedBy);
        Assert.True(matched.Result.MatchConfidence >= 0.85);
    }

    [Fact]
    public async Task TryEnrichAsync_DurationMismatch_BlocksMatched_GoesNeedsReview()
    {
        await using var db = CreateDb();
        db.SpotifySettings.Add(new SpotifySettings {     OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId, ClientId = "id", ClientSecret = "secret" });
        await db.SaveChangesAsync();

        var track = new SpotifyCatalogTrack(
            "id1",
            "Lucid Dreams",
            "Juice WRLD",
            "Album",
            2018,
            1,
            60_000,
            null);

        var catalog = new StubCatalogSearchService(_ => Task.FromResult<IReadOnlyList<SpotifyCatalogTrack>>([track]));
        var provider = CreateProvider(db, catalog);

        var song = new SongMetadata
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            SourcePath = "/a.mp3",
            FileName = "a.mp3",
            Extension = ".mp3",
            FileSizeBytes = 1,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Artist = "Juice WRLD",
            Title = "Lucid Dreams",
            DurationSeconds = 239,
            EnrichmentStatus = EnrichmentStatus.Pending,
        };

        var result = await provider.TryEnrichAsync(song);
        var matched = Assert.IsType<ProviderMatched>(result);
        Assert.Equal(EnrichmentStatus.NeedsReview, matched.Result.RecommendedStatus);
        Assert.Contains("duration_mismatch", matched.Result.MatchWarnings);
    }

    [Fact]
    public async Task TryEnrichAsync_IsrcMismatch_ReturnsNoMatch_WhenScoreBelowMin()
    {
        await using var db = CreateDb();
        db.SpotifySettings.Add(new SpotifySettings {     OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId, ClientId = "id", ClientSecret = "secret" });
        await db.SaveChangesAsync();

        var track = new SpotifyCatalogTrack(
            "id1",
            "Lucid Dreams",
            "Juice WRLD",
            "Album",
            2018,
            1,
            239_000,
            "USUM71809999");

        var catalog = new StubCatalogSearchService(_ => Task.FromResult<IReadOnlyList<SpotifyCatalogTrack>>([track]));
        var provider = CreateProvider(db, catalog);

        var song = new SongMetadata
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            SourcePath = "/a.mp3",
            FileName = "a.mp3",
            Extension = ".mp3",
            FileSizeBytes = 1,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Artist = "Juice WRLD",
            Title = "Lucid Dreams",
            DurationSeconds = 239,
            Isrc = "USUM71807840",
            EnrichmentStatus = EnrichmentStatus.Pending,
        };

        var result = await provider.TryEnrichAsync(song);
        Assert.IsType<ProviderNoMatch>(result);
    }

    [Fact]
    public async Task TryEnrichAsync_RateLimited_ReturnsProviderRateLimited()
    {
        await using var db = CreateDb();
        db.SpotifySettings.Add(new SpotifySettings {     OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId, ClientId = "id", ClientSecret = "secret" });
        await db.SaveChangesAsync();

        var catalog = new StubCatalogSearchService(_ =>
            throw new ProviderRateLimitedException(TimeSpan.FromSeconds(30)));
        var provider = CreateProvider(db, catalog);

        var song = new SongMetadata
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            SourcePath = "/a.mp3",
            FileName = "a.mp3",
            Extension = ".mp3",
            FileSizeBytes = 1,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Artist = "Juice WRLD",
            Title = "Lucid Dreams",
            DurationSeconds = 239,
            EnrichmentStatus = EnrichmentStatus.Pending,
        };

        var result = await provider.TryEnrichAsync(song);
        var rateLimited = Assert.IsType<ProviderRateLimited>(result);
        Assert.Equal(TimeSpan.FromSeconds(30), rateLimited.RetryAfter);
    }

    [Fact]
    public async Task TryEnrichAsync_BelowMinConfidence_ReturnsNoMatchWithBestCandidate()
    {
        await using var db = CreateDb();
        db.SpotifySettings.Add(new SpotifySettings {     OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId, ClientId = "id", ClientSecret = "secret" });
        await db.SaveChangesAsync();

        // Candidate has wildly different artist/title — score will fall well below MinConfidence.
        var track = new SpotifyCatalogTrack(
            "near-miss-id",
            "Different Title Entirely",
            "Different Artist Entirely",
            "Different Album",
            2010,
            7,
            239_000,
            null);
        var catalog = new StubCatalogSearchService(_ => Task.FromResult<IReadOnlyList<SpotifyCatalogTrack>>([track]));
        var opts = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/s",
            DestinationDirectory = "/d",
            SpotifyApiMinConfidence = 0.7,
            SpotifyApiMatchedThreshold = 0.85,
        });
        var provider = CreateProvider(db, catalog, opts);

        var song = new SongMetadata
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            SourcePath = "/a.mp3",
            FileName = "a.mp3",
            Extension = ".mp3",
            FileSizeBytes = 1,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Artist = "Juice WRLD",
            Title = "Lucid Dreams",
            DurationSeconds = 239,
            EnrichmentStatus = EnrichmentStatus.Pending,
        };

        var result = await provider.TryEnrichAsync(song);
        var noMatch = Assert.IsType<ProviderNoMatch>(result);
        Assert.NotNull(noMatch.BestCandidate);
        Assert.Equal("Different Title Entirely", noMatch.BestCandidate!.Title);
        Assert.Equal("Different Artist Entirely", noMatch.BestCandidate.Artist);
        Assert.Equal("Different Album", noMatch.BestCandidate.Album);
        Assert.Equal("near-miss-id", noMatch.BestCandidate.SpotifyId);
        Assert.True(noMatch.BestCandidate.MatchConfidence < 0.7,
            $"Expected confidence below MinConfidence threshold, got {noMatch.BestCandidate.MatchConfidence:F3}");
        Assert.Equal(EnrichmentStatus.NeedsReview, noMatch.BestCandidate.RecommendedStatus);
    }

    [Fact]
    public async Task TryEnrichAsync_NoCandidatesAtAll_ReturnsBareNoMatch()
    {
        await using var db = CreateDb();
        db.SpotifySettings.Add(new SpotifySettings {     OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId, ClientId = "id", ClientSecret = "secret" });
        await db.SaveChangesAsync();

        var catalog = new StubCatalogSearchService(_ => Task.FromResult<IReadOnlyList<SpotifyCatalogTrack>>([]));
        var provider = CreateProvider(db, catalog);

        var song = new SongMetadata
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            SourcePath = "/a.mp3",
            FileName = "a.mp3",
            Extension = ".mp3",
            FileSizeBytes = 1,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Artist = "Juice WRLD",
            Title = "Lucid Dreams",
            DurationSeconds = 239,
            EnrichmentStatus = EnrichmentStatus.Pending,
        };

        var result = await provider.TryEnrichAsync(song);
        var noMatch = Assert.IsType<ProviderNoMatch>(result);
        Assert.Null(noMatch.BestCandidate);
    }

    [Fact]
    public async Task ApplyEnrichmentMatch_WithAlbum_PersistsAlbum()
    {
        var song = new SongMetadata
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            SourcePath = "/a.mp3",
            FileName = "a.mp3",
            Extension = ".mp3",
            FileSizeBytes = 1,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Artist = "Old",
            Title = "Old",
            Album = "Old Album",
            EnrichmentStatus = EnrichmentStatus.Pending,
        };

        song.ApplyEnrichmentMatch(new EnrichmentMatchData(
            "A", "A", "T", 2020, 1,
            null, null, "sid", null, null,
            "SpotifyAPI", 0.9, null, EnrichmentStatus.Matched,
            "New Album"));

        Assert.Equal("New Album", song.Album);
    }

    [Fact]
    public async Task TryEnrichAsync_SymbolOnlyArtist_WrongCandidate_NotCleanMatch()
    {
        // Regression: "¥$" normalizes to empty, which used to score the artist as a free 100%
        // and let an unrelated same-title candidate sail through. Now the raw fallback catches it.
        await using var db = CreateDb();
        db.SpotifySettings.Add(new SpotifySettings { OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId, ClientId = "id", ClientSecret = "secret" });
        await db.SaveChangesAsync();

        var track = new SpotifyCatalogTrack(
            "wrong-id", "FIELD TRIP", "RAREKID", "FIELD TRIP", 2024, 1, 318_000, "GXBDS2665817");
        var catalog = new StubCatalogSearchService(_ => Task.FromResult<IReadOnlyList<SpotifyCatalogTrack>>([track]));
        var provider = CreateProvider(db, catalog);

        var song = new SongMetadata
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            SourcePath = "/a.mp3",
            FileName = "a.mp3",
            Extension = ".mp3",
            FileSizeBytes = 1,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Artist = "¥$",
            Title = "Field Trip",
            DurationSeconds = 318,
            EnrichmentStatus = EnrichmentStatus.Pending,
        };

        var result = await provider.TryEnrichAsync(song);
        var candidate = result switch
        {
            ProviderMatched m => m.Result,
            ProviderNoMatch nm => nm.BestCandidate,
            _ => null,
        };
        Assert.NotNull(candidate);
        Assert.NotEqual(EnrichmentStatus.Matched, candidate!.RecommendedStatus);
        Assert.Contains("artist_mismatch", candidate.MatchWarnings);
    }

    [Fact]
    public async Task TryEnrichAsync_UntaggedFile_DerivesQueryFromPath_Matches()
    {
        // Untagged loose download: no embedded artist/title, but the path carries both.
        await using var db = CreateDb();
        db.SpotifySettings.Add(new SpotifySettings { OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId, ClientId = "id", ClientSecret = "secret" });
        await db.SaveChangesAsync();

        var track = new SpotifyCatalogTrack(
            "spotifyTrackId", "Lucid Dreams", "Juice WRLD", "Goodbye & Good Riddance", 2018, 5, 239_000, null);
        string? seenQuery = null;
        var catalog = new StubCatalogSearchService(q => { seenQuery = q; return Task.FromResult<IReadOnlyList<SpotifyCatalogTrack>>([track]); });
        var provider = CreateProvider(db, catalog);

        var song = new SongMetadata
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            SourcePath = "/s/Juice WRLD/Goodbye & Good Riddance/05 Lucid Dreams.mp3",
            FileName = "05 Lucid Dreams.mp3",
            Extension = ".mp3",
            FileSizeBytes = 1,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Artist = null,
            Title = null,
            DurationSeconds = 239,
            EnrichmentStatus = EnrichmentStatus.Pending,
        };

        var result = await provider.TryEnrichAsync(song);
        var matched = Assert.IsType<ProviderMatched>(result);
        Assert.Equal("spotifyTrackId", matched.Result.SpotifyId);
        Assert.Equal(1, catalog.CallCount);
        Assert.Contains("lucid dreams", seenQuery);
        Assert.DoesNotContain("05", seenQuery);
    }

    private static MusicHoarderDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MusicHoarderDbContext(options);
    }

    private static SpotifyApiEnrichmentProvider CreateProvider(
        MusicHoarderDbContext db,
        ISpotifyCatalogSearchService catalog,
        IOptions<MusicEnricherOptions>? opts = null,
        IOptions<SpotifyOptions>? spotifyOpts = null)
    {
        opts ??= Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/s",
            DestinationDirectory = "/d",
        });
        spotifyOpts ??= Microsoft.Extensions.Options.Options.Create(new SpotifyOptions());
        var scopeFactory = new DbOnlyScopeFactory(db);
        return new SpotifyApiEnrichmentProvider(
            scopeFactory,
            catalog,
            new TestOwnerLookupService(),
            opts,
            spotifyOpts,
            NullLogger<SpotifyApiEnrichmentProvider>.Instance);
    }

    private sealed class StubCatalogSearchService(
        Func<string, Task<IReadOnlyList<SpotifyCatalogTrack>>> handler) : ISpotifyCatalogSearchService
    {
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<SpotifyCatalogTrack>> SearchTracksAsync(
            string clientId,
            string clientSecret,
            string query,
            CancellationToken ct = default)
        {
            CallCount++;
            return handler(query);
        }

        public Task<string?> GetTrackAlbumIdAsync(string clientId, string clientSecret, string trackId, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<string?> SearchAlbumIdAsync(string clientId, string clientSecret, string artist, string album, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<SpotifyAlbumDetail?> GetAlbumAsync(string clientId, string clientSecret, string albumId, CancellationToken ct = default)
            => Task.FromResult<SpotifyAlbumDetail?>(null);

        public Task<string?> SearchTrackIdByIsrcAsync(string clientId, string clientSecret, string isrc, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class DbOnlyScopeFactory(MusicHoarderDbContext db) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(new Provider(db));

        private sealed class Scope(IServiceProvider sp) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = sp;
            public void Dispose() { }
        }

        private sealed class Provider(MusicHoarderDbContext db) : IServiceProvider
        {
            public object? GetService(Type serviceType) =>
                serviceType == typeof(MusicHoarderDbContext) ? db : null;
        }
    }
}

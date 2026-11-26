using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Artwork;
using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Spotify;

namespace MusicHoarder.Api.Tests.Artwork;

/// <summary>
/// The HTTP-facing half of <see cref="ArtistImageService"/>: <see cref="IArtistImageService.GetImageUrlAsync"/>
/// only ever reads the cache and enqueues a background refresh — it never calls a provider, so a
/// slow or rate-limited Spotify/Deezer can never hold up an image request. The provider-facing half
/// (<see cref="IArtistImageService.RefreshAsync"/>) is what the prefetch worker runs; the fetch
/// scenarios below drive it directly.
/// </summary>
public class ArtistImageServiceTests
{
    [Fact]
    public async Task CachedRowIsServedWithoutProviderCallsOrEnqueue()
    {
        using var db = CreateDbContext();
        SeedLibraryArtist(db, "Daft Punk");
        db.ArtistImages.Add(new ArtistImage
        {
            NormalizedName = "daft punk",
            DisplayName = "Daft Punk",
            ImageUrl = "https://i.scdn.example/dp.jpg",
            ImageUrlLarge = "https://i.scdn.example/dp-xl.jpg",
            Source = "spotify",
            FetchedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var (service, prefetcher, deezer, spotify) = CreateService(db);

        Assert.Equal("https://i.scdn.example/dp.jpg", await service.GetImageUrlAsync("Daft Punk"));

        Assert.Equal(0, deezer.SearchCalls);
        Assert.Equal(0, spotify.SearchCalls);
        Assert.Equal(0, prefetcher.PendingCount);
    }

    [Fact]
    public async Task LargePrefersTheHeroUrlAndFallsBackToTheGridUrl()
    {
        using var db = CreateDbContext();
        SeedLibraryArtist(db, "Daft Punk");
        db.ArtistImages.Add(new ArtistImage
        {
            NormalizedName = "daft punk",
            DisplayName = "Daft Punk",
            ImageUrl = "https://i.scdn.example/dp.jpg",
            ImageUrlLarge = "https://i.scdn.example/dp-xl.jpg",
            Source = "spotify",
            FetchedAtUtc = DateTime.UtcNow,
        });
        db.ArtistImages.Add(new ArtistImage
        {
            NormalizedName = "old row",
            DisplayName = "Old Row",
            ImageUrl = "https://i.scdn.example/old.jpg",
            Source = "deezer",
            FetchedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var (service, _, _, _) = CreateService(db);

        Assert.Equal("https://i.scdn.example/dp-xl.jpg", await service.GetImageUrlAsync("Daft Punk", large: true));
        Assert.Equal("https://i.scdn.example/dp.jpg", await service.GetImageUrlAsync("Daft Punk"));
        // A row from before the large column existed serves its grid URL for both.
        Assert.Equal("https://i.scdn.example/old.jpg", await service.GetImageUrlAsync("Old Row", large: true));
    }

    [Fact]
    public async Task MissingRowEnqueuesAKnownArtistAndReturnsNullUntilTheWorkerLands()
    {
        using var db = CreateDbContext();
        SeedLibraryArtist(db, "Daft Punk");
        var (service, prefetcher, deezer, spotify) = CreateService(db);
        spotify.Candidates = [new SpotifyArtistCandidate("Daft Punk", "https://i.scdn.example/dp.jpg", "https://i.scdn.example/dp-xl.jpg")];

        // First request: no row, so null (404) — but the name is queued for the background fetch
        // instead of blocking the request on the providers.
        Assert.Null(await service.GetImageUrlAsync("Daft Punk"));
        Assert.Equal(0, spotify.SearchCalls);
        Assert.Equal(1, prefetcher.PendingCount);

        // The worker's pass lands the row; the next request serves it, and a fresh row is not
        // re-enqueued.
        await service.RefreshAsync("Daft Punk");
        prefetcher.Release("Daft Punk");
        Assert.Equal("https://i.scdn.example/dp.jpg", await service.GetImageUrlAsync("Daft Punk"));
        Assert.Equal(1, spotify.SearchCalls);
        Assert.Equal(0, deezer.SearchCalls);
        var row = Assert.Single(db.ArtistImages);
        Assert.Equal("spotify", row.Source);
        Assert.Equal("https://i.scdn.example/dp-xl.jpg", row.ImageUrlLarge);
        Assert.Equal(0, prefetcher.PendingCount);
    }

    [Fact]
    public async Task StaleRowIsServedImmediatelyAndEnqueuedForRefresh()
    {
        using var db = CreateDbContext();
        SeedLibraryArtist(db, "Daft Punk");
        db.ArtistImages.Add(new ArtistImage
        {
            NormalizedName = "daft punk",
            DisplayName = "Daft Punk",
            ImageUrl = "https://cdn.deezer.example/dp.jpg",
            Source = "deezer",
            FetchedAtUtc = DateTime.UtcNow.AddDays(-120), // past ArtistImageRefreshDays (90)
        });
        await db.SaveChangesAsync();
        var (service, prefetcher, _, spotify) = CreateService(db);
        spotify.Candidates = [new SpotifyArtistCandidate("Daft Punk", "https://i.scdn.example/dp.jpg")];

        // Stale-while-revalidate: the old portrait is served now, the refresh is queued.
        Assert.Equal("https://cdn.deezer.example/dp.jpg", await service.GetImageUrlAsync("Daft Punk"));
        Assert.Equal(1, prefetcher.PendingCount);
        Assert.Equal(0, spotify.SearchCalls);

        await service.RefreshAsync("Daft Punk");
        var row = Assert.Single(db.ArtistImages);
        Assert.Equal("https://i.scdn.example/dp.jpg", row.ImageUrl);
        Assert.Equal("spotify", row.Source);
        Assert.True(row.FetchedAtUtc > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task UnknownNamesReturnNullAndAreNeverEnqueued()
    {
        using var db = CreateDbContext();
        var (service, prefetcher, deezer, spotify) = CreateService(db);

        for (var i = 0; i < 5; i++)
            Assert.Null(await service.GetImageUrlAsync($"zz made up artist {i:000}"));

        Assert.Equal(0, prefetcher.PendingCount);
        Assert.Equal(0, deezer.SearchCalls);
        Assert.Equal(0, spotify.SearchCalls);
        Assert.Empty(db.ArtistImages);
    }

    [Fact]
    public async Task BulkMapReturnsCachedRowsAndEnqueuesTheMissingKnownOnes()
    {
        using var db = CreateDbContext();
        SeedLibraryArtist(db, "Daft Punk");
        SeedLibraryArtist(db, "Drake");
        db.ArtistImages.Add(new ArtistImage
        {
            NormalizedName = "daft punk",
            DisplayName = "Daft Punk",
            ImageUrl = "https://i.scdn.example/dp.jpg",
            Source = "spotify",
            FetchedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var (service, prefetcher, _, _) = CreateService(db);

        var map = await service.GetImagesMapAsync(["Daft Punk", "Drake", "Nobody"]);

        Assert.Equal("https://i.scdn.example/dp.jpg", map["Daft Punk"]?.ImageUrl);
        Assert.Null(map["Drake"]);   // known, but no row yet → enqueued
        Assert.Null(map["Nobody"]);   // not a library artist → not enqueued
        Assert.Equal(1, prefetcher.PendingCount);
    }

    // ── RefreshAsync: the worker's fetch pass ──────────────────────────────────

    [Fact]
    public async Task Refresh_VerifiedSpotifyHitWinsOverDeezerAndIsCached()
    {
        using var db = CreateDbContext();
        SeedLibraryArtist(db, "Daft Punk");
        var (service, _, deezer, spotify) = CreateService(db);
        deezer.Candidates = [new DeezerArtistCandidate("Daft Punk", "https://cdn.deezer.example/dp.jpg")];
        spotify.Candidates = [new SpotifyArtistCandidate("Daft Punk", "https://i.scdn.example/dp.jpg")];

        await service.RefreshAsync("Daft Punk");

        var row = Assert.Single(db.ArtistImages);
        Assert.Equal("https://i.scdn.example/dp.jpg", row.ImageUrl);
        Assert.Equal("spotify", row.Source);
        Assert.Equal("daft punk", row.NormalizedName);
        Assert.Equal(1, spotify.SearchCalls);
        Assert.Equal(0, deezer.SearchCalls);

        // A fresh row is not re-fetched: the second pass is a no-op.
        await service.RefreshAsync("Daft Punk");
        Assert.Equal(1, spotify.SearchCalls);
    }

    [Fact]
    public async Task Refresh_UnverifiedSpotifyHitFallsThroughToDeezer()
    {
        using var db = CreateDbContext();
        SeedLibraryArtist(db, "Drake");
        var (service, _, deezer, spotify) = CreateService(db);
        spotify.Candidates = [new SpotifyArtistCandidate("Drake Bell", "https://i.scdn.example/wrong.jpg")];
        deezer.Candidates = [new DeezerArtistCandidate("Drake", "https://cdn.deezer.example/drake.jpg", PictureUrlLarge: "https://cdn.deezer.example/drake-xl.jpg")];

        await service.RefreshAsync("Drake");

        var row = Assert.Single(db.ArtistImages);
        Assert.Equal("https://cdn.deezer.example/drake.jpg", row.ImageUrl);
        Assert.Equal("https://cdn.deezer.example/drake-xl.jpg", row.ImageUrlLarge);
        Assert.Equal("deezer", row.Source);
    }

    [Fact]
    public async Task Refresh_SpotifyIsSkippedWithoutCredentialsAndFallsBackToDeezer()
    {
        using var db = CreateDbContext();
        SeedLibraryArtist(db, "Drake");
        var (service, _, deezer, spotify) = CreateService(db, hasSpotifyCredentials: false);
        spotify.Candidates = [new SpotifyArtistCandidate("Drake", "https://i.scdn.example/drake.jpg")];
        deezer.Candidates = [new DeezerArtistCandidate("Drake", "https://cdn.deezer.example/drake.jpg")];

        await service.RefreshAsync("Drake");

        var row = Assert.Single(db.ArtistImages);
        Assert.Equal("https://cdn.deezer.example/drake.jpg", row.ImageUrl);
        Assert.Equal("deezer", row.Source);
        Assert.Equal(0, spotify.SearchCalls);
        Assert.Equal(1, deezer.SearchCalls);
    }

    [Fact]
    public async Task Refresh_InvalidatedDeezerRowUpgradesToSpotifyEvenWhenPeriodicRefreshIsDisabled()
    {
        using var db = CreateDbContext();
        SeedLibraryArtist(db, "Daft Punk");
        db.ArtistImages.Add(new ArtistImage
        {
            NormalizedName = "daft punk",
            DisplayName = "Daft Punk",
            ImageUrl = "https://cdn.deezer.example/dp.jpg",
            Source = "deezer",
            FetchedAtUtc = DateTime.UnixEpoch,
        });
        await db.SaveChangesAsync();

        var (service, _, deezer, spotify) = CreateService(db, configure: o => o.ArtistImageRefreshDays = 0);
        spotify.Candidates = [new SpotifyArtistCandidate("Daft Punk", "https://i.scdn.example/dp.jpg")];
        deezer.Candidates = [new DeezerArtistCandidate("Daft Punk", "https://cdn.deezer.example/dp.jpg")];

        await service.RefreshAsync("Daft Punk");

        var row = Assert.Single(db.ArtistImages);
        Assert.Equal("spotify", row.Source);
        Assert.Equal("https://i.scdn.example/dp.jpg", row.ImageUrl);
        Assert.True(row.FetchedAtUtc > DateTime.UtcNow.AddMinutes(-1));
        Assert.Equal(0, deezer.SearchCalls);
    }

    [Fact]
    public async Task Refresh_SpotifyRateLimitKeepsInvalidatedDeezerRowEligibleForUpgrade()
    {
        using var db = CreateDbContext();
        SeedLibraryArtist(db, "Daft Punk");
        db.ArtistImages.Add(new ArtistImage
        {
            NormalizedName = "daft punk",
            DisplayName = "Daft Punk",
            ImageUrl = "https://cdn.deezer.example/dp.jpg",
            Source = "deezer",
            FetchedAtUtc = DateTime.UnixEpoch,
        });
        await db.SaveChangesAsync();

        var (service, _, deezer, spotify) = CreateService(db);
        spotify.RateLimited = true;
        deezer.Candidates = [];

        // Rate-limited with an existing cached URL: keep the row invalidated (no re-stamp), so the
        // next pass still tries Spotify.
        await service.RefreshAsync("Daft Punk");
        Assert.Equal(0, deezer.SearchCalls);
        Assert.Equal(DateTime.UnixEpoch, Assert.Single(db.ArtistImages).FetchedAtUtc);

        // Once the rate limit clears, the same pass upgrades to Spotify.
        spotify.RateLimited = false;
        spotify.Candidates = [new SpotifyArtistCandidate("Daft Punk", "https://i.scdn.example/dp.jpg")];

        await service.RefreshAsync("Daft Punk");
        var row = Assert.Single(db.ArtistImages);
        Assert.Equal("spotify", row.Source);
        Assert.True(row.FetchedAtUtc > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task Refresh_SpotifyRateLimitOnUncachedArtistServesDeezerAndUpgradesOnceCleared()
    {
        using var db = CreateDbContext();
        SeedLibraryArtist(db, "Daft Punk");
        var (service, _, deezer, spotify) = CreateService(db);
        spotify.RateLimited = true;
        deezer.Candidates = [new DeezerArtistCandidate("Daft Punk", "https://cdn.deezer.example/dp.jpg")];

        await service.RefreshAsync("Daft Punk");
        var row = Assert.Single(db.ArtistImages);
        Assert.Equal("deezer", row.Source);
        Assert.Equal(DateTime.UnixEpoch, row.FetchedAtUtc); // stays invalidated for the Spotify upgrade

        // Once Spotify recovers, the next pass upgrades without touching Deezer again.
        spotify.RateLimited = false;
        spotify.Candidates = [new SpotifyArtistCandidate("Daft Punk", "https://i.scdn.example/dp.jpg")];

        await service.RefreshAsync("Daft Punk");
        Assert.Equal("spotify", row.Source);
        Assert.Equal("https://i.scdn.example/dp.jpg", row.ImageUrl);
        Assert.Equal(1, deezer.SearchCalls);
    }

    [Fact]
    public async Task Refresh_NotFoundIsNegativeCachedUntilRetryWindow()
    {
        using var db = CreateDbContext();
        SeedLibraryArtist(db, "Unknown Artist");
        var (service, _, deezer, spotify) = CreateService(db);

        await service.RefreshAsync("Unknown Artist");
        await service.RefreshAsync("Unknown Artist");

        Assert.Equal(1, spotify.SearchCalls);
        Assert.Equal(1, deezer.SearchCalls);
        var row = Assert.Single(db.ArtistImages);
        Assert.Null(row.ImageUrl);

        // Age the row past the not-found retry window: the next pass fetches again.
        row.FetchedAtUtc = DateTime.UtcNow.AddDays(-15);
        await db.SaveChangesAsync();
        spotify.Candidates = [new SpotifyArtistCandidate("Unknown Artist", "https://i.scdn.example/ua.jpg")];

        await service.RefreshAsync("Unknown Artist");
        Assert.Equal("https://i.scdn.example/ua.jpg", row.ImageUrl);
        Assert.Equal(2, spotify.SearchCalls);
        Assert.Equal(1, deezer.SearchCalls);
    }

    [Fact]
    public async Task Refresh_ThatFindsNothingKeepsTheOldUrl()
    {
        using var db = CreateDbContext();
        SeedLibraryArtist(db, "Daft Punk");
        db.ArtistImages.Add(new ArtistImage
        {
            NormalizedName = "daft punk",
            DisplayName = "Daft Punk",
            ImageUrl = "https://cdn.deezer.example/old.jpg",
            Source = "deezer",
            FetchedAtUtc = DateTime.UtcNow.AddDays(-120),
        });
        await db.SaveChangesAsync();
        var (service, _, _, _) = CreateService(db);

        await service.RefreshAsync("Daft Punk");

        // The row was re-stamped so the empty refresh isn't retried per pass.
        var row = Assert.Single(db.ArtistImages);
        Assert.Equal("https://cdn.deezer.example/old.jpg", row.ImageUrl);
        Assert.True(row.FetchedAtUtc > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task Refresh_DisabledFeatureDoesNothing()
    {
        using var db = CreateDbContext();
        SeedLibraryArtist(db, "Daft Punk");
        var (service, _, deezer, spotify) = CreateService(db, configure: o => o.EnableArtistImages = false);
        spotify.Candidates = [new SpotifyArtistCandidate("Daft Punk", "https://i.scdn.example/dp.jpg")];
        deezer.Candidates = [new DeezerArtistCandidate("Daft Punk", "https://cdn.deezer.example/dp.jpg")];

        Assert.Null(await service.GetImageUrlAsync("Daft Punk"));
        await service.RefreshAsync("Daft Punk");

        Assert.Equal(0, spotify.SearchCalls);
        Assert.Equal(0, deezer.SearchCalls);
        Assert.Empty(db.ArtistImages);
    }

    [Fact]
    public async Task Refresh_UnknownNameNeverReachesAProvider()
    {
        using var db = CreateDbContext();
        var (service, _, deezer, spotify) = CreateService(db);

        await service.RefreshAsync("Totally Made Up");

        Assert.Equal(0, spotify.SearchCalls);
        Assert.Equal(0, deezer.SearchCalls);
        Assert.Empty(db.ArtistImages);
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static void SeedLibraryArtist(MusicHoarderDbContext db, string artist)
    {
        db.Songs.Add(new SongMetadata
        {
            SourcePath = $"/source/{artist}.mp3",
            FileSizeBytes = 1,
            FileName = $"{artist}.mp3",
            Extension = ".mp3",
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Artist = artist,
        });
        db.SaveChanges();
    }

    private static (ArtistImageService Service, ArtistImagePrefetcher Prefetcher, StubDeezer Deezer, StubSpotify Spotify) CreateService(
        MusicHoarderDbContext db,
        StubDeezer? deezer = null,
        StubSpotify? spotify = null,
        bool hasSpotifyCredentials = true,
        Action<MusicEnricherOptions>? configure = null)
    {
        var options = new MusicEnricherOptions
        {
            SourceDirectory = "/source",
            DestinationDirectory = "/dest",
        };
        configure?.Invoke(options);

        var prefetcher = new ArtistImagePrefetcher();
        var known = new KnownArtistIndex();
        known.RefreshAsync(db, CancellationToken.None).GetAwaiter().GetResult();

        deezer ??= new StubDeezer();
        spotify ??= new StubSpotify();
        var service = new ArtistImageService(
            db,
            deezer,
            spotify,
            new StubCredentials(hasSpotifyCredentials),
            known,
            prefetcher,
            Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<ArtistImageService>.Instance);
        return (service, prefetcher, deezer, spotify);
    }

    private static MusicHoarderDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private sealed class StubCredentials(bool hasCredentials) : ISpotifyAppCredentialsProvider
    {
        public Task<(string? ClientId, string? ClientSecret)> ResolveAsync(CancellationToken ct = default)
            => Task.FromResult(hasCredentials ? ((string?)"id", (string?)"secret") : (null, null));
    }

    private sealed class StubDeezer : IDeezerCatalogService
    {
        public IReadOnlyList<DeezerArtistCandidate> Candidates { get; set; } = [];
        public int SearchCalls { get; private set; }

        public Task<IReadOnlyList<DeezerArtistCandidate>> SearchArtistCandidatesAsync(string name, CancellationToken ct = default)
        {
            SearchCalls++;
            return Task.FromResult(Candidates);
        }

        public Task<DeezerCatalogTrack?> LookupByIsrcAsync(string isrc, CancellationToken ct = default)
            => Task.FromResult<DeezerCatalogTrack?>(null);

        public Task<IReadOnlyList<DeezerCatalogTrack>> SearchTracksAsync(string query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DeezerCatalogTrack>>([]);

        public Task<DeezerCatalogTrack?> LookupByIdAsync(string id, CancellationToken ct = default)
            => Task.FromResult<DeezerCatalogTrack?>(null);

        public Task<string?> SearchAlbumIdAsync(string artist, string album, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<DeezerAlbumCandidate>> SearchAlbumCandidatesAsync(string artist, string album, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DeezerAlbumCandidate>>([]);

        public Task<DeezerAlbumDetail?> GetAlbumAsync(string albumId, CancellationToken ct = default)
            => Task.FromResult<DeezerAlbumDetail?>(null);

        public Task<DeezerPlaylistSummary?> GetPlaylistAsync(string id, CancellationToken ct = default)
            => Task.FromResult<DeezerPlaylistSummary?>(null);

        public Task<DeezerPlaylistTracksResult> GetPlaylistTracksAsync(string id, int? maxTracks = null, CancellationToken ct = default)
            => Task.FromResult(new DeezerPlaylistTracksResult([], IsComplete: true));
    }

    private sealed class StubSpotify : ISpotifyCatalogSearchService
    {
        public IReadOnlyList<SpotifyArtistCandidate> Candidates { get; set; } = [];
        public bool RateLimited { get; set; }
        public int SearchCalls { get; private set; }

        public Task<IReadOnlyList<SpotifyArtistCandidate>> SearchArtistCandidatesAsync(string clientId, string clientSecret, string name, CancellationToken ct = default)
        {
            SearchCalls++;
            if (RateLimited)
                throw new ProviderRateLimitedException(TimeSpan.FromMinutes(10));
            return Task.FromResult(Candidates);
        }

        public Task<IReadOnlyList<SpotifyCatalogTrack>> SearchTracksAsync(string clientId, string clientSecret, string query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SpotifyCatalogTrack>>([]);

        public Task<SpotifyCatalogTrack?> GetTrackAsync(string clientId, string clientSecret, string trackId, CancellationToken ct = default)
            => Task.FromResult<SpotifyCatalogTrack?>(null);

        public Task<string?> GetTrackAlbumIdAsync(string clientId, string clientSecret, string trackId, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<string?> SearchAlbumIdAsync(string clientId, string clientSecret, string artist, string album, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<SpotifyAlbumCandidate>> SearchAlbumCandidatesAsync(string clientId, string clientSecret, string artist, string album, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SpotifyAlbumCandidate>>([]);

        public Task<SpotifyAlbumDetail?> GetAlbumAsync(string clientId, string clientSecret, string albumId, CancellationToken ct = default)
            => Task.FromResult<SpotifyAlbumDetail?>(null);

        public Task<string?> SearchTrackIdByIsrcAsync(string clientId, string clientSecret, string isrc, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }
}

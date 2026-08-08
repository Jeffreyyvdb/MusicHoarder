using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Artwork;
using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Spotify;

namespace MusicHoarder.Api.Tests.Artwork;

public class ArtistImageServiceTests
{
    [Fact]
    public async Task VerifiedDeezerHitWinsAndIsCached()
    {
        using var db = CreateDbContext();
        var deezer = new StubDeezer { Candidates = [new DeezerArtistCandidate("Daft Punk", "https://cdn.deezer.example/dp.jpg")] };
        var service = CreateService(db, deezer);

        var url = await service.GetImageUrlAsync("Daft Punk");

        Assert.Equal("https://cdn.deezer.example/dp.jpg", url);
        var row = Assert.Single(db.ArtistImages);
        Assert.Equal("deezer", row.Source);
        Assert.Equal("daft punk", row.NormalizedName);

        // Second call is served from the cache — no provider round-trip.
        await service.GetImageUrlAsync("Daft Punk");
        Assert.Equal(1, deezer.SearchCalls);
    }

    [Fact]
    public async Task UnverifiedDeezerHitFallsThroughToSpotify()
    {
        using var db = CreateDbContext();
        var deezer = new StubDeezer { Candidates = [new DeezerArtistCandidate("Drake Bell", "https://cdn.deezer.example/wrong.jpg")] };
        var spotify = new StubSpotify { Candidates = [new SpotifyArtistCandidate("Drake", "https://i.scdn.example/drake.jpg")] };
        var service = CreateService(db, deezer, spotify);

        var url = await service.GetImageUrlAsync("Drake");

        Assert.Equal("https://i.scdn.example/drake.jpg", url);
        Assert.Equal("spotify", Assert.Single(db.ArtistImages).Source);
    }

    [Fact]
    public async Task SpotifyIsSkippedWithoutCredentials()
    {
        using var db = CreateDbContext();
        var spotify = new StubSpotify { Candidates = [new SpotifyArtistCandidate("Drake", "https://i.scdn.example/drake.jpg")] };
        var service = CreateService(db, new StubDeezer(), spotify, hasSpotifyCredentials: false);

        var url = await service.GetImageUrlAsync("Drake");

        Assert.Null(url);
        Assert.Equal(0, spotify.SearchCalls);
    }

    [Fact]
    public async Task NotFoundIsNegativeCachedUntilRetryWindow()
    {
        using var db = CreateDbContext();
        var deezer = new StubDeezer();
        var service = CreateService(db, deezer);

        Assert.Null(await service.GetImageUrlAsync("Unknown Artist"));
        Assert.Null(await service.GetImageUrlAsync("Unknown Artist"));

        Assert.Equal(1, deezer.SearchCalls);
        var row = Assert.Single(db.ArtistImages);
        Assert.Null(row.ImageUrl);

        // Age the row past the not-found retry window: the next call fetches again.
        row.FetchedAtUtc = DateTime.UtcNow.AddDays(-15);
        await db.SaveChangesAsync();
        deezer.Candidates = [new DeezerArtistCandidate("Unknown Artist", "https://cdn.deezer.example/ua.jpg")];

        Assert.Equal("https://cdn.deezer.example/ua.jpg", await service.GetImageUrlAsync("Unknown Artist"));
        Assert.Equal(2, deezer.SearchCalls);
    }

    [Fact]
    public async Task RefreshThatFindsNothingKeepsTheOldUrl()
    {
        using var db = CreateDbContext();
        db.ArtistImages.Add(new ArtistImage
        {
            NormalizedName = "daft punk",
            DisplayName = "Daft Punk",
            ImageUrl = "https://cdn.deezer.example/old.jpg",
            Source = "deezer",
            FetchedAtUtc = DateTime.UtcNow.AddDays(-120),
        });
        await db.SaveChangesAsync();
        var service = CreateService(db, new StubDeezer());

        var url = await service.GetImageUrlAsync("Daft Punk");

        Assert.Equal("https://cdn.deezer.example/old.jpg", url);
        // The row was re-stamped so the empty refresh isn't retried per request.
        Assert.True(Assert.Single(db.ArtistImages).FetchedAtUtc > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task DisabledFeatureReturnsNullWithoutFetching()
    {
        using var db = CreateDbContext();
        var deezer = new StubDeezer { Candidates = [new DeezerArtistCandidate("Daft Punk", "https://cdn.deezer.example/dp.jpg")] };
        var service = CreateService(db, deezer, configure: o => o.EnableArtistImages = false);

        Assert.Null(await service.GetImageUrlAsync("Daft Punk"));
        Assert.Equal(0, deezer.SearchCalls);
    }

    private static ArtistImageService CreateService(
        MusicHoarderDbContext db,
        StubDeezer deezer,
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

        return new ArtistImageService(
            db,
            deezer,
            spotify ?? new StubSpotify(),
            new StubCredentials(hasSpotifyCredentials),
            Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<ArtistImageService>.Instance);
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

        public Task<IReadOnlyList<DeezerGenre>> GetGenresAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DeezerGenre>>([]);

        public Task<IReadOnlyList<DeezerPlaylistSummary>> GetChartPlaylistsAsync(long? genreId, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DeezerPlaylistSummary>>([]);

        public Task<IReadOnlyList<DeezerPlaylistSummary>> SearchPlaylistsAsync(string query, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DeezerPlaylistSummary>>([]);

        public Task<DeezerPlaylistSummary?> GetPlaylistAsync(string id, CancellationToken ct = default)
            => Task.FromResult<DeezerPlaylistSummary?>(null);

        public Task<DeezerPlaylistTracksResult> GetPlaylistTracksAsync(string id, int? maxTracks = null, CancellationToken ct = default)
            => Task.FromResult(new DeezerPlaylistTracksResult([], IsComplete: true));
    }

    private sealed class StubSpotify : ISpotifyCatalogSearchService
    {
        public IReadOnlyList<SpotifyArtistCandidate> Candidates { get; set; } = [];
        public int SearchCalls { get; private set; }

        public Task<IReadOnlyList<SpotifyArtistCandidate>> SearchArtistCandidatesAsync(string clientId, string clientSecret, string name, CancellationToken ct = default)
        {
            SearchCalls++;
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

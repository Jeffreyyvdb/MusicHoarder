using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Artwork;
using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Spotify;

namespace MusicHoarder.Api.Tests.Artwork;

public class ArtistImageLookupScopeTests
{
    [Fact]
    public async Task NamesOutsideTheLibraryGetNoProviderLookupAndNoRow()
    {
        using var db = CreateDbContext();
        var deezer = new CountingDeezer();
        var spotify = new CountingSpotify();
        var service = CreateService(db, deezer, spotify);

        for (var i = 0; i < 5; i++)
            Assert.Null(await service.GetImageUrlAsync($"zz made up artist {i:000}"));

        Assert.Equal(0, deezer.Calls);
        Assert.Equal(0, spotify.Calls);
        Assert.Empty(db.ArtistImages);
    }

    [Fact]
    public async Task OverlongNamesAreRejectedEvenWhenInTheLibrary()
    {
        using var db = CreateDbContext();
        var longName = new string('a', 500);
        SeedArtist(db, longName);
        var deezer = new CountingDeezer();
        var service = CreateService(db, deezer, new CountingSpotify());

        Assert.Null(await service.GetImageUrlAsync(longName));

        Assert.Equal(0, deezer.Calls);
        Assert.Empty(db.ArtistImages);
    }

    [Fact]
    public async Task LibraryArtistsStillGetALookupAndARow()
    {
        using var db = CreateDbContext();
        SeedArtist(db, "Daft Punk", artists: "Daft Punk; Some Guest");
        var deezer = new CountingDeezer();
        var service = CreateService(db, deezer, new CountingSpotify());

        await service.GetImageUrlAsync("daft punk");
        await service.GetImageUrlAsync("Some Guest");
        await service.GetImageUrlAsync("Some");

        Assert.Equal(2, deezer.Calls);
        Assert.Equal(2, db.ArtistImages.Count());
    }

    private static void SeedArtist(MusicHoarderDbContext db, string artist, string? artists = null)
    {
        db.Songs.Add(new SongMetadata
        {
            SourcePath = "/source/a.mp3",
            FileSizeBytes = 1,
            FileName = "a.mp3",
            Extension = ".mp3",
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Artist = artist,
            Artists = artists,
        });
        db.SaveChanges();
    }

    private static ArtistImageService CreateService(MusicHoarderDbContext db, IDeezerCatalogService deezer, ISpotifyCatalogSearchService spotify) =>
        new(
            db,
            deezer,
            spotify,
            new Creds(),
            Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
            {
                SourceDirectory = "/source",
                DestinationDirectory = "/dest",
            }),
            NullLogger<ArtistImageService>.Instance);

    private static MusicHoarderDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private sealed class Creds : ISpotifyAppCredentialsProvider
    {
        public Task<(string? ClientId, string? ClientSecret)> ResolveAsync(CancellationToken ct = default)
            => Task.FromResult(((string?)"id", (string?)"secret"));
    }

    private sealed class CountingSpotify : ISpotifyCatalogSearchService
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<SpotifyArtistCandidate>> SearchArtistCandidatesAsync(string clientId, string clientSecret, string name, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<SpotifyArtistCandidate>>([]);
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

    private sealed class CountingDeezer : IDeezerCatalogService
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<DeezerArtistCandidate>> SearchArtistCandidatesAsync(string name, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<DeezerArtistCandidate>>([]);
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
}

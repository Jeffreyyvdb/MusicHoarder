using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Artwork;
using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Spotify;
using MusicHoarder.Api.Tests.Auth;

namespace MusicHoarder.Api.Tests.Artwork;

/// <summary>
/// Which names may trigger a provider lookup at all: the in-memory <see cref="IKnownArtistIndex"/>
/// the prefetch worker reloads — every artist in any library on the instance (the portrait cache
/// is catalog data with no tenant scope, so a name is either "someone's artist here" or not) plus
/// every followed artist. Anything else gets a 404 and is never enqueued nor fetched.
/// </summary>
public class ArtistImageLookupScopeTests
{
    [Fact]
    public async Task NamesOutsideTheLibraryGetNoProviderLookupAndNoRow()
    {
        using var db = CreateDbContext();
        var (service, prefetcher, deezer, spotify) = CreateService(db);

        for (var i = 0; i < 5; i++)
            Assert.Null(await service.GetImageUrlAsync($"zz made up artist {i:000}"));

        Assert.Equal(0, prefetcher.PendingCount);
        Assert.Equal(0, deezer.Calls);
        Assert.Equal(0, spotify.Calls);
        Assert.Empty(db.ArtistImages);

        // Even a direct worker pass refuses them.
        await service.RefreshAsync("zz made up artist 000");
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
        var (service, prefetcher, deezer, _) = CreateService(db);

        Assert.Null(await service.GetImageUrlAsync(longName));

        Assert.Equal(0, prefetcher.PendingCount);
        Assert.Equal(0, deezer.Calls);
        Assert.Empty(db.ArtistImages);
    }

    [Fact]
    public async Task LibraryArtistsGetALookupAndARow_IncludingEachDiscreteCredit()
    {
        using var db = CreateDbContext();
        SeedArtist(db, "Daft Punk", artists: "Daft Punk; Some Guest");
        var (service, prefetcher, deezer, _) = CreateService(db);

        await service.GetImageUrlAsync("daft punk");
        await service.GetImageUrlAsync("Some Guest");
        await service.GetImageUrlAsync("Some"); // a prefix of a credited artist is NOT the artist

        Assert.Equal(2, prefetcher.PendingCount);

        await service.RefreshAsync("daft punk");
        await service.RefreshAsync("Some Guest");
        await service.RefreshAsync("Some");

        Assert.Equal(2, deezer.Calls);
        Assert.Equal(2, db.ArtistImages.Count());
    }

    [Fact]
    public async Task TheKnownSetIsInstanceWide_BecauseThePortraitCacheIsCatalogData()
    {
        // The portrait cache has no per-user rows: one row serves every tenant. So the gate is
        // "is this name an artist in some library here (or followed)", not "in the caller's" —
        // a member can only ever trigger lookups for names already on the instance, never for
        // arbitrary ones.
        using var db = CreateDbContext();
        SeedArtist(db, "Daft Punk", ownerId: TestUsers.OwnerId);
        SeedArtist(db, "Someone Else's Band", ownerId: TestUsers.SecondFriendId);
        var (service, _, deezer, _) = CreateService(db);

        await service.RefreshAsync("Daft Punk");
        await service.RefreshAsync("Someone Else's Band");

        Assert.Equal(2, deezer.Calls);
        Assert.Equal(2, db.ArtistImages.Count());
    }

    [Fact]
    public async Task FollowedArtistsGetALookupEvenWithoutOwnedSongs()
    {
        using var db = CreateDbContext();
        db.FollowedArtists.Add(new FollowedArtist
        {
            OwnerUserId = TestUsers.OwnerId,
            DeezerArtistId = "27",
            Name = "Daft Punk",
            ArtistKey = "daft punk",
            FollowedAtUtc = DateTime.UtcNow,
        });
        db.FollowedArtists.Add(new FollowedArtist
        {
            OwnerUserId = TestUsers.FriendId,
            DeezerArtistId = "99",
            Name = "Someone Else",
            ArtistKey = "someone else",
            FollowedAtUtc = DateTime.UtcNow,
        });
        db.SaveChanges();
        var (service, _, _, spotify) = CreateService(db);

        await service.GetImageUrlAsync("Daft Punk");
        await service.GetImageUrlAsync("Someone Else");

        await service.RefreshAsync("Daft Punk");
        await service.RefreshAsync("Someone Else");

        Assert.Equal(2, spotify.Calls);
        Assert.Equal(2, db.ArtistImages.Count());
    }

    private static void SeedArtist(MusicHoarderDbContext db, string artist, string? artists = null, Guid? ownerId = null)
    {
        db.Songs.Add(new SongMetadata
        {
            OwnerUserId = ownerId ?? Guid.Empty,
            LibraryBuildStatus = LibraryBuildStatus.Done,
            SourcePath = $"/source/{artist}.mp3",
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

    private static (ArtistImageService Service, ArtistImagePrefetcher Prefetcher, CountingDeezer Deezer, CountingSpotify Spotify) CreateService(
        MusicHoarderDbContext db)
    {
        var prefetcher = new ArtistImagePrefetcher();
        var known = new KnownArtistIndex();
        known.RefreshAsync(db, CancellationToken.None).GetAwaiter().GetResult();

        var deezer = new CountingDeezer();
        var spotify = new CountingSpotify();
        var service = new ArtistImageService(
            db,
            deezer,
            spotify,
            new Creds(),
            known,
            prefetcher,
            Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
            {
                SourceDirectory = "/source",
                DestinationDirectory = "/dest",
            }),
            NullLogger<ArtistImageService>.Instance);
        return (service, prefetcher, deezer, spotify);
    }

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

        public Task<DeezerPlaylistSummary?> GetPlaylistAsync(string id, CancellationToken ct = default)
            => Task.FromResult<DeezerPlaylistSummary?>(null);

        public Task<DeezerPlaylistTracksResult> GetPlaylistTracksAsync(string id, int? maxTracks = null, CancellationToken ct = default)
            => Task.FromResult(new DeezerPlaylistTracksResult([], IsComplete: true));
    }
}

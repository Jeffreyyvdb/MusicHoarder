using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Spotify;
using MusicHoarder.Api.Tests.Auth;
using MusicHoarder.Api.Wishlist;

namespace MusicHoarder.Api.Tests.Wishlist;

public class WishlistSyncBackgroundServiceTests
{
    private static readonly Guid Owner = WellKnownUsers.OwnerId;

    [Fact]
    public async Task SyncOnce_WithBothDownloadFlags_KicksDownloadWhenItemsAdded()
    {
        var jobManager = new JobManager();
        var (svc, _) = BuildService(jobManager, enableDownloads: true, autoDownload: true,
            liked: [Track("a", "Song A"), Track("b", "Song B")]);

        var added = await svc.SyncOnceAsync(full: true, CancellationToken.None);

        Assert.Equal(2, added);
        Assert.Equal("Running", jobManager.GetStepSnapshot(JobType.Download).Status);
    }

    [Fact]
    public async Task SyncOnce_WithDownloadsDisabled_DoesNotKickDownload()
    {
        var jobManager = new JobManager();
        var (svc, _) = BuildService(jobManager, enableDownloads: false, autoDownload: true,
            liked: [Track("a", "Song A")]);

        var added = await svc.SyncOnceAsync(full: true, CancellationToken.None);

        Assert.Equal(1, added);
        Assert.Equal("Idle", jobManager.GetStepSnapshot(JobType.Download).Status);
    }

    [Fact]
    public async Task SyncOnce_WithAutoDownloadOff_DoesNotKickDownload()
    {
        var jobManager = new JobManager();
        var (svc, _) = BuildService(jobManager, enableDownloads: true, autoDownload: false,
            liked: [Track("a", "Song A")]);

        var added = await svc.SyncOnceAsync(full: true, CancellationToken.None);

        Assert.Equal(1, added);
        Assert.Equal("Idle", jobManager.GetStepSnapshot(JobType.Download).Status);
    }

    [Fact]
    public async Task SyncOnce_WithNoNewItems_DoesNotKickDownload()
    {
        var jobManager = new JobManager();
        var (svc, db) = BuildService(jobManager, enableDownloads: true, autoDownload: true,
            liked: [Track("a", "Song A")]);
        // Pre-seed the only liked track so the sweep adds nothing.
        db.WishlistItems.Add(new WishlistItem
        {
            OwnerUserId = Owner,
            SpotifyTrackId = "a",
            Title = "Song A",
            Artist = "Artist",
            Status = WishlistItemStatus.Pending,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var added = await svc.SyncOnceAsync(full: true, CancellationToken.None);

        Assert.Equal(0, added);
        Assert.Equal("Idle", jobManager.GetStepSnapshot(JobType.Download).Status);
    }

    private static (WishlistSyncBackgroundService Svc, MusicHoarderDbContext Db) BuildService(
        JobManager jobManager, bool enableDownloads, bool autoDownload, List<SpotifyTrackItem> liked)
    {
        var dbOptions = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        // Seed a connected Spotify settings row and an auto-synced Liked Songs source.
        using (var seed = new MusicHoarderDbContext(dbOptions))
        {
            seed.SpotifySettings.Add(new SpotifySettings
            {
                OwnerUserId = Owner,
                AccessToken = "access",
                RefreshToken = "refresh",
            });
            seed.WishlistSources.Add(new WishlistSource
            {
                OwnerUserId = Owner,
                SourceType = WishlistSourceType.LikedSongs,
                Name = "Liked Songs",
                AutoSync = true,
                CreatedAtUtc = DateTime.UtcNow,
            });
            seed.SaveChanges();
        }

        var api = new FakeSpotifyApi { LikedSongs = liked };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => new MusicHoarderDbContext(dbOptions));
        services.AddSingleton<ISpotifyApiService>(api);
        services.AddScoped<IWishlistService, WishlistService>();
        var provider = services.BuildServiceProvider();

        var svc = new WishlistSyncBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new TestOwnerLookupService(),
            jobManager,
            Microsoft.Extensions.Options.Options.Create(new SpotifyOptions()),
            Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
            {
                SourceDirectory = "/src",
                DestinationDirectory = "/dst",
                EnableWishlistDownloads = enableDownloads,
                AutoDownloadWishlist = autoDownload,
            }),
            NullLogger<WishlistSyncBackgroundService>.Instance);

        return (svc, new MusicHoarderDbContext(dbOptions));
    }

    private static SpotifyTrackItem Track(string id, string title) =>
        new(id, title, "Artist", "Album", null, 200_000, DateTime.UtcNow, null);

    private sealed class FakeSpotifyApi : ISpotifyApiService
    {
        public List<SpotifyTrackItem> LikedSongs { get; set; } = [];

        public Task<SpotifyLikedSongsResponse> GetLikedSongsAsync(int offset = 0, int limit = 50, CancellationToken ct = default)
        {
            var page = LikedSongs.Skip(offset).Take(limit).ToList();
            return Task.FromResult(new SpotifyLikedSongsResponse(LikedSongs.Count, offset, limit, page));
        }

        public Task<SpotifyPlaylistsResponse> GetPlaylistsAsync(CancellationToken ct = default) =>
            Task.FromResult(new SpotifyPlaylistsResponse([]));

        public Task<SpotifyPlaylistTracksResponse> GetPlaylistTracksAsync(string playlistId, int offset = 0, int limit = 50, CancellationToken ct = default) =>
            Task.FromResult(new SpotifyPlaylistTracksResponse(0, offset, limit, []));
    }
}

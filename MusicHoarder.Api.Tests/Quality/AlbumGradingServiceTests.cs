using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Quality;

namespace MusicHoarder.Api.Tests.Quality;

public class AlbumGradingServiceTests
{
    [Fact]
    public async Task GradeAlbum_Configured_PersistsGrade()
    {
        var db = CreateDb();
        var albumId = await SeedFetchedAlbumWithOwnedSongs(db);

        var client = new FakeChatClient(configured: true,
            content: """{"score": 22, "verdict": "wrong", "summary":"Most owned songs don't appear on this album.","issues":[{"code":"owned_tracks_absent","severity":"high"}]}""");
        var service = CreateService(db, client);

        var result = await service.GradeAlbumAsync(albumId, force: false);

        Assert.Equal(GradeOutcome.Graded, result.Outcome);
        Assert.Equal(1, client.CallCount);

        var grade = await db.CanonicalAlbumQualityGrades.SingleAsync();
        Assert.Equal(22, grade.Score);
        Assert.Equal(SongQualityVerdict.Wrong, grade.Verdict);
        Assert.Equal("test/model", grade.Model);
        Assert.Equal(WellKnownUsers.OwnerId, grade.OwnerUserId);
        Assert.NotNull(grade.InputFingerprint);
        Assert.True(grade.OwnedTrackCount > 0);
    }

    [Fact]
    public async Task GradeAlbum_NotConfigured_DoesNotPersist()
    {
        var db = CreateDb();
        var albumId = await SeedFetchedAlbumWithOwnedSongs(db);

        var service = CreateService(db, new FakeChatClient(configured: false, content: "{}"));
        var result = await service.GradeAlbumAsync(albumId, force: false);

        Assert.Equal(GradeOutcome.NotConfigured, result.Outcome);
        Assert.False(await db.CanonicalAlbumQualityGrades.AnyAsync());
    }

    [Fact]
    public async Task GradeAlbum_UnchangedDossier_SkipsSecondCall()
    {
        var db = CreateDb();
        var albumId = await SeedFetchedAlbumWithOwnedSongs(db);

        var client = new FakeChatClient(configured: true, content: """{"score": 92, "verdict": "excellent"}""");
        var service = CreateService(db, client);

        var first = await service.GradeAlbumAsync(albumId, force: false);
        var second = await service.GradeAlbumAsync(albumId, force: false);

        Assert.Equal(GradeOutcome.Graded, first.Outcome);
        Assert.Equal(GradeOutcome.Skipped, second.Outcome);
        Assert.Equal(1, client.CallCount);
        Assert.Equal(1, await db.CanonicalAlbumQualityGrades.CountAsync());
    }

    [Fact]
    public async Task GradeAlbum_NotFetched_ReturnsNotFound()
    {
        var db = CreateDb();
        db.CanonicalAlbums.Add(new CanonicalAlbum { Id = 5, ArtistKey = "x", AlbumKey = "y", Status = CanonicalAlbumStatus.NotFound });
        await db.SaveChangesAsync();

        var service = CreateService(db, new FakeChatClient(configured: true, content: "{}"));
        var result = await service.GradeAlbumAsync(5, force: false);

        Assert.Equal(GradeOutcome.NotFound, result.Outcome);
    }

    // --- helpers ---

    private static async Task<int> SeedFetchedAlbumWithOwnedSongs(MusicHoarderDbContext db)
    {
        var album = new CanonicalAlbum
        {
            ArtistKey = "daft punk",
            AlbumKey = "discovery",
            DisplayArtist = "Daft Punk",
            DisplayTitle = "Discovery",
            Status = CanonicalAlbumStatus.Fetched,
            ResolvedTrackCount = 2,
            Tracks =
            [
                new CanonicalAlbumTrack { DiscNumber = 1, TrackNumber = 1, Title = "One More Time", MusicBrainzRecordingId = "rec-1" },
                new CanonicalAlbumTrack { DiscNumber = 1, TrackNumber = 2, Title = "Aerodynamic" },
            ],
        };
        db.CanonicalAlbums.Add(album);
        db.Songs.Add(OwnedSong("/1.mp3", trackNumber: 1, title: "One More Time", mbid: "rec-1"));
        db.Songs.Add(OwnedSong("/2.mp3", trackNumber: 2, title: "Aerodynamic"));
        await db.SaveChangesAsync();
        return album.Id;
    }

    private static SongMetadata OwnedSong(string path, int trackNumber, string title, string? mbid = null) => new()
    {
        OwnerUserId = WellKnownUsers.OwnerId,
        SourcePath = path,
        FileName = Path.GetFileName(path),
        Extension = ".mp3",
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        EnrichmentStatus = EnrichmentStatus.Matched,
        AlbumArtist = "Daft Punk",
        Artist = "Daft Punk",
        Album = "Discovery",
        Title = title,
        TrackNumber = trackNumber,
        MusicBrainzId = mbid,
    };

    private static MusicHoarderDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static AlbumGradingService CreateService(MusicHoarderDbContext db, FakeChatClient client)
    {
        var enricher = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/s",
            DestinationDirectory = "/d",
        });
        return new AlbumGradingService(
            new SimpleScopeFactory(db), client, new AlbumGradingDossierFactory(), new StubOwnerLookup(),
            enricher, new TestOptionsMonitor(new QualityGradingOptions { Model = "test/model" }),
            NullLogger<AlbumGradingService>.Instance);
    }

    private sealed class FakeChatClient(bool configured, string content) : IChatCompletionClient
    {
        public int CallCount { get; private set; }
        public bool IsConfigured => configured;

        public Task<ChatCompletionResult> CompleteAsync(ChatCompletionRequest request, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(new ChatCompletionResult(content, 100, 50));
        }
    }

    private sealed class StubOwnerLookup : IOwnerLookupService
    {
        public Guid OwnerUserId => WellKnownUsers.OwnerId;
    }

    private sealed class TestOptionsMonitor(QualityGradingOptions value) : IOptionsMonitor<QualityGradingOptions>
    {
        public QualityGradingOptions CurrentValue { get; } = value;
        public QualityGradingOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<QualityGradingOptions, string?> listener) => null;
    }

    private sealed class SimpleScopeFactory(MusicHoarderDbContext db) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(new Provider(db));
        private sealed class Scope(IServiceProvider provider) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = provider;
            public void Dispose() { }
        }
        private sealed class Provider(MusicHoarderDbContext db) : IServiceProvider
        {
            public object? GetService(Type serviceType) =>
                serviceType == typeof(MusicHoarderDbContext) ? db : null;
        }
    }
}

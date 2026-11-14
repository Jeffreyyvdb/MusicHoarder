using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Auth;

namespace MusicHoarder.Api.Tests.Quality;

/// <summary>
/// The album-matches overview counts its coverage total through the caller's own songs, because
/// the canonical-album catalog has no per-user query filter. Without that, the demo account (which
/// may read the overview) would be told how many albums the rest of the instance has.
/// </summary>
public class AlbumQualityOverviewScopeTests
{
    [Fact]
    public async Task Each_account_counts_only_the_albums_it_owns_tracks_of()
    {
        var options = await SeedAsync();

        Assert.Equal(2, await CountAs(options, TestCurrentUserAccessor.OwnerUser));
        Assert.Equal(1, await CountAs(options, TestCurrentUserAccessor.DemoUser));
        Assert.Equal(0, await CountAs(options, TestCurrentUserAccessor.FriendUser));
    }

    [Fact]
    public async Task Demo_overview_holds_only_its_own_figures()
    {
        var options = await SeedAsync();
        await using var db = new MusicHoarderDbContext(options, new TestCurrentUserAccessor(TestCurrentUserAccessor.DemoUser));

        var result = await AlbumQualityEndpoints.GetOverview(db, new StubOptions(), CancellationToken.None);
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        var body = new MemoryStream();
        http.Response.Body = body;
        await result.ExecuteAsync(http);
        var json = System.Text.Encoding.UTF8.GetString(body.ToArray());

        Assert.Contains("\"gradeableTotal\":1", json);
        Assert.Contains("\"graded\":0", json);
        Assert.DoesNotContain("Discovery", json);
    }

    [Fact]
    public async Task Coverage_never_exceeds_the_whole()
    {
        var options = await SeedAsync();
        await using (var seed = new MusicHoarderDbContext(options))
        {
            // Graded while the owner still had tracks of it; they are gone now.
            seed.CanonicalAlbumQualityGrades.AddRange(
                Grade(1), Grade(2), Grade(3));
            await seed.SaveChangesAsync();
        }

        await using var db = new MusicHoarderDbContext(options, new TestCurrentUserAccessor(TestCurrentUserAccessor.OwnerUser));
        var result = await AlbumQualityEndpoints.GetOverview(db, new StubOptions(), CancellationToken.None);
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        var body = new MemoryStream();
        http.Response.Body = body;
        await result.ExecuteAsync(http);

        Assert.Contains("\"coverage\":1", System.Text.Encoding.UTF8.GetString(body.ToArray()));
    }

    private static CanonicalAlbumQualityGrade Grade(int albumId) => new()
    {
        OwnerUserId = TestUsers.OwnerId,
        CanonicalAlbumId = albumId,
        Score = 90,
        Verdict = SongQualityVerdict.Good,
        GradedAtUtc = DateTime.UtcNow,
    };

    private static async Task<int> CountAs(DbContextOptions<MusicHoarderDbContext> options, CurrentUser user)
    {
        await using var db = new MusicHoarderDbContext(options, new TestCurrentUserAccessor(user));
        return await AlbumQualityEndpoints.CountGradeableAsync(db, CancellationToken.None);
    }

    private static async Task<DbContextOptions<MusicHoarderDbContext>> SeedAsync()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        await using var db = new MusicHoarderDbContext(options);

        db.CanonicalAlbums.AddRange(
            Album(1, "Daft Punk", "Discovery"),
            Album(2, "Daft Punk", "Homework"),
            Album(3, "Air", "Moon Safari"),
            Album(4, "Justice", "Cross"),
            Album(5, "Daft Punk", "Human After All", CanonicalAlbumStatus.NotFound));

        db.Songs.AddRange(
            Song(TestUsers.OwnerId, "Daft Punk", "Discovery", "/o/1.flac"),
            Song(TestUsers.OwnerId, "Daft Punk", "Discovery", "/o/2.flac"),
            Song(TestUsers.OwnerId, "Daft Punk", "Homework", "/o/3.flac"),
            Song(TestUsers.OwnerId, "Daft Punk", "Human After All", "/o/4.flac"),
            // Not counted: deleted, not yet matched.
            Song(TestUsers.OwnerId, "Justice", "Cross", "/o/5.flac", deleted: true),
            Song(TestUsers.OwnerId, "Air", "Moon Safari", "/o/6.flac", status: EnrichmentStatus.NeedsReview),
            Song(TestUsers.DemoId, "Air", "Moon Safari", "/d/1.flac"),
            // Not counted: the demo's synthetic seed rows.
            Song(TestUsers.DemoId, "Justice", "Cross", "demo://x", synthetic: true));

        await db.SaveChangesAsync();
        return options;
    }

    private static CanonicalAlbum Album(int id, string artist, string album,
        CanonicalAlbumStatus status = CanonicalAlbumStatus.Fetched) => new()
    {
        Id = id,
        ArtistKey = TitleNormalizer.NormalizeForSearch(artist),
        AlbumKey = TitleNormalizer.NormalizeForSearch(album),
        DisplayArtist = artist,
        DisplayTitle = album,
        Status = status,
    };

    private static SongMetadata Song(Guid owner, string artist, string album, string path,
        EnrichmentStatus status = EnrichmentStatus.Matched, bool deleted = false, bool synthetic = false) => new()
    {
        OwnerUserId = owner,
        SourcePath = path,
        FileName = Path.GetFileName(path),
        Extension = ".flac",
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        Artist = artist,
        Album = album,
        EnrichmentStatus = status,
        IsSynthetic = synthetic,
        DeletedAtUtc = deleted ? DateTime.UtcNow : null,
    };

    private sealed class StubOptions : IOptionsMonitor<QualityGradingOptions>
    {
        public QualityGradingOptions CurrentValue { get; } = new();
        public QualityGradingOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<QualityGradingOptions, string?> listener) => null;
    }
}

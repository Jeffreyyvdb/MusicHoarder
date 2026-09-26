using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Quality;

namespace MusicHoarder.Api.Tests.Quality;

/// <summary>
/// The album auto-grade sweep mirrors the song sweep: it enqueues fetched albums that were never
/// graded or were re-fetched since their grade, and leaves alone albums whose only staleness is a
/// prompt-version change (those are regraded on an explicit "regrade outdated" action only).
/// </summary>
public class AlbumGradingSweepTests
{
    [Fact]
    public async Task EnqueueUngraded_EnqueuesNeverGradedAndRefetched_SkipsUpToDateAndUnfetched()
    {
        using var db = NewContext();
        var graded = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var beforeGrade = graded.AddDays(-1);
        var afterGrade = graded.AddDays(1);

        var neverGraded = AddAlbum(db, 1, fetchedAt: beforeGrade);
        var upToDate = AddAlbum(db, 2, fetchedAt: beforeGrade);
        AddGrade(db, upToDate.Id, graded, AlbumGradingPrompt.Version);
        var reFetched = AddAlbum(db, 3, fetchedAt: afterGrade); // fetched again after its grade
        AddGrade(db, reFetched.Id, graded, AlbumGradingPrompt.Version);
        var oldPrompt = AddAlbum(db, 4, fetchedAt: beforeGrade);
        AddGrade(db, oldPrompt.Id, graded, promptVersion: AlbumGradingPrompt.Version + 1);
        var notFetched = AddAlbum(db, 5, fetchedAt: null, status: CanonicalAlbumStatus.Pending);

        var channel = new AlbumGradingChannel(new AlbumGradingProgressTracker());
        var sut = NewService(db, channel);

        var count = await sut.EnqueueUngradedAsync(new QualityGradingOptions(), CancellationToken.None);

        var enqueued = Drain(channel);
        Assert.Equal(enqueued.Count, count);
        Assert.Contains(neverGraded.Id, enqueued);
        Assert.Contains(reFetched.Id, enqueued);
        Assert.DoesNotContain(upToDate.Id, enqueued);
        Assert.DoesNotContain(oldPrompt.Id, enqueued);   // version-only staleness is NOT auto-regraded
        Assert.DoesNotContain(notFetched.Id, enqueued);  // nothing to grade until the tracklist is fetched
    }

    [Fact]
    public async Task EnqueueUngraded_TakesTheMostRecentlyFetchedAlbums_UpToBatchSize()
    {
        using var db = NewContext();
        var older = AddAlbum(db, 1, fetchedAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var newer = AddAlbum(db, 2, fetchedAt: new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));

        var channel = new AlbumGradingChannel(new AlbumGradingProgressTracker());
        var sut = NewService(db, channel);

        var count = await sut.EnqueueUngradedAsync(new QualityGradingOptions { BatchSize = 1 }, CancellationToken.None);

        Assert.Equal(1, count);
        Assert.Equal([newer.Id], Drain(channel));
        Assert.DoesNotContain(older.Id, Drain(channel));
    }

    [Fact]
    public async Task EnqueueUngraded_WithNothingFetched_EnqueuesNothing()
    {
        using var db = NewContext();
        AddAlbum(db, 1, fetchedAt: null, status: CanonicalAlbumStatus.NotFound);

        var channel = new AlbumGradingChannel(new AlbumGradingProgressTracker());
        var sut = NewService(db, channel);

        Assert.Equal(0, await sut.EnqueueUngradedAsync(new QualityGradingOptions(), CancellationToken.None));
        Assert.Empty(Drain(channel));
    }

    [Fact]
    public async Task EnqueueUngraded_EnqueuesALegacyBlankGrade_ButNotAGenuineUngradeable()
    {
        using var db = NewContext();
        var graded = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var beforeGrade = graded.AddDays(-1);

        var blank = AddAlbum(db, 1, fetchedAt: beforeGrade);
        AddGrade(db, blank.Id, graded, AlbumGradingPrompt.Version, SongQualityVerdict.Ungradeable, raw: "{ }");
        var genuine = AddAlbum(db, 2, fetchedAt: beforeGrade);
        AddGrade(db, genuine.Id, graded, AlbumGradingPrompt.Version, SongQualityVerdict.Ungradeable,
            raw: GenuineUngradeableReply);

        var channel = new AlbumGradingChannel(new AlbumGradingProgressTracker());
        var sut = NewService(db, channel);

        var count = await sut.EnqueueUngradedAsync(new QualityGradingOptions(), CancellationToken.None);

        Assert.Equal(1, count);
        Assert.Equal([blank.Id], Drain(channel)); // the genuine verdict is never regraded in a loop
    }

    [Fact]
    public async Task EnqueueUngraded_FindsALegacyBlankGradeOutsideTheNewestWindow()
    {
        using var db = NewContext();
        var graded = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        var oldBlank = AddAlbum(db, 1, fetchedAt: graded.AddDays(-30));
        AddGrade(db, oldBlank.Id, graded, AlbumGradingPrompt.Version, SongQualityVerdict.Ungradeable,
            raw: """{ "analysis": [ ]}""");
        var newest = AddAlbum(db, 2, fetchedAt: graded.AddDays(-1)); // the only album in the window
        AddGrade(db, newest.Id, graded, AlbumGradingPrompt.Version);

        var channel = new AlbumGradingChannel(new AlbumGradingProgressTracker());
        var sut = NewService(db, channel);

        var count = await sut.EnqueueUngradedAsync(new QualityGradingOptions { BatchSize = 1 }, CancellationToken.None);

        Assert.Equal(1, count);
        Assert.Equal([oldBlank.Id], Drain(channel));
    }

    [Fact]
    public async Task EnqueueUngraded_RepeatedSweepsBeforeTheQueueDrains_QueueEachBlankAlbumOnce()
    {
        // Regrading a blank backlog outlasts a sweep interval, and every sweep still sees the albums
        // that haven't run yet — the channel must not hand them to the workers twice.
        using var db = NewContext();
        var graded = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var blankA = AddAlbum(db, 1, fetchedAt: graded.AddDays(-1));
        AddGrade(db, blankA.Id, graded, AlbumGradingPrompt.Version, SongQualityVerdict.Ungradeable, raw: "{ }");
        var blankB = AddAlbum(db, 2, fetchedAt: graded.AddDays(-2));
        AddGrade(db, blankB.Id, graded, AlbumGradingPrompt.Version, SongQualityVerdict.Ungradeable, raw: "{ }");

        var channel = new AlbumGradingChannel(new AlbumGradingProgressTracker());
        var sut = NewService(db, channel);

        await sut.EnqueueUngradedAsync(new QualityGradingOptions(), CancellationToken.None);
        await sut.EnqueueUngradedAsync(new QualityGradingOptions(), CancellationToken.None);

        Assert.Equal(new[] { blankA.Id, blankB.Id }, Drain(channel).OrderBy(id => id));
    }

    private const string GenuineUngradeableReply = """{"score":0,"verdict":"ungradeable"}""";

    private static AlbumGradingBackgroundService NewService(MusicHoarderDbContext db, AlbumGradingChannel channel) =>
        new(
            new SimpleScopeFactory(db), channel, new AlbumGradingProgressTracker(),
            gradingService: null!, runtimeSettings: null!,
            new TestOptionsMonitor(new QualityGradingOptions()),
            NullLogger<AlbumGradingBackgroundService>.Instance);

    private static List<int> Drain(AlbumGradingChannel channel)
    {
        var ids = new List<int>();
        while (channel.Reader.TryRead(out var item)) ids.Add(item.CanonicalAlbumId);
        return ids;
    }

    private static CanonicalAlbum AddAlbum(
        MusicHoarderDbContext db, int n, DateTime? fetchedAt, CanonicalAlbumStatus status = CanonicalAlbumStatus.Fetched)
    {
        var album = new CanonicalAlbum
        {
            ArtistKey = $"artist{n}",
            AlbumKey = $"album{n}",
            DisplayArtist = $"Artist {n}",
            DisplayTitle = $"Album {n}",
            Status = status,
            FetchedAtUtc = fetchedAt,
        };
        db.CanonicalAlbums.Add(album);
        db.SaveChanges();
        return album;
    }

    private static void AddGrade(
        MusicHoarderDbContext db, int albumId, DateTime gradedAt, int promptVersion,
        SongQualityVerdict verdict = SongQualityVerdict.Good, string? raw = null)
    {
        db.CanonicalAlbumQualityGrades.Add(new CanonicalAlbumQualityGrade
        {
            CanonicalAlbumId = albumId,
            OwnerUserId = WellKnownUsers.OwnerId,
            Score = verdict == SongQualityVerdict.Ungradeable ? 0 : 80,
            Verdict = verdict,
            PromptVersion = promptVersion,
            Model = "test/model",
            RawResponseJson = raw,
            GradedAtUtc = gradedAt,
        });
        db.SaveChanges();
    }

    private static MusicHoarderDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

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

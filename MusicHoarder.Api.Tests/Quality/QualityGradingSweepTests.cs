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
/// The auto-grade sweep must enqueue genuinely new work (never-graded, re-enriched) but must NOT
/// re-grade a song whose only staleness is a prompt-version or model change — those are surfaced as
/// "outdated" and refreshed via an explicit manual / "regrade outdated" action instead.
/// </summary>
public class QualityGradingSweepTests
{
    private const string CurrentModel = "openai/gpt-4o-mini";

    [Fact]
    public async Task EnqueueUngraded_SkipsVersionOrModelOnlyStaleGrades_ButEnqueuesNewAndReenriched()
    {
        using var db = NewContext();
        var graded = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var beforeGrade = graded.AddDays(-1);
        var afterGrade = graded.AddDays(1);

        var neverGraded = AddSong(db, 1, enrichedAt: beforeGrade);
        var upToDate = AddSong(db, 2, enrichedAt: beforeGrade);
        AddGrade(db, upToDate.Id, graded, QualityGradingPrompt.Version, CurrentModel);
        var oldPrompt = AddSong(db, 3, enrichedAt: beforeGrade);
        AddGrade(db, oldPrompt.Id, graded, promptVersion: 1, model: CurrentModel);
        var oldModel = AddSong(db, 4, enrichedAt: beforeGrade);
        AddGrade(db, oldModel.Id, graded, QualityGradingPrompt.Version, model: "some/older-model");
        var reEnriched = AddSong(db, 5, enrichedAt: afterGrade); // enriched after its (current) grade
        AddGrade(db, reEnriched.Id, graded, QualityGradingPrompt.Version, CurrentModel);

        var channel = new QualityGradingChannel(new QualityGradingProgressTracker());
        var sut = new QualityGradingBackgroundService(
            new SimpleScopeFactory(db), channel, new QualityGradingProgressTracker(),
            gradingService: null!, runtimeSettings: null!, ownerLookup: null!,
            new TestOptionsMonitor(new QualityGradingOptions { Model = CurrentModel }),
            NullLogger<QualityGradingBackgroundService>.Instance);

        await sut.EnqueueUngradedAsync(new QualityGradingOptions { Model = CurrentModel }, CancellationToken.None);

        var enqueued = new List<int>();
        while (channel.Reader.TryRead(out var item)) enqueued.Add(item.SongId);

        Assert.Contains(neverGraded.Id, enqueued);
        Assert.Contains(reEnriched.Id, enqueued);
        Assert.DoesNotContain(upToDate.Id, enqueued);
        Assert.DoesNotContain(oldPrompt.Id, enqueued);  // version-only staleness is NOT auto-regraded
        Assert.DoesNotContain(oldModel.Id, enqueued);   // model-only staleness is NOT auto-regraded
    }

    [Fact]
    public async Task EnqueueUngraded_ExcludesDemoTenant()
    {
        using var db = NewContext();
        var enriched = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var ownerSong = AddSong(db, 1, enrichedAt: enriched);
        var demoSong = AddSong(db, 2, enrichedAt: enriched);
        demoSong.OwnerUserId = WellKnownUsers.DemoId; // read-only demo library is never auto-graded
        db.SaveChanges();

        var channel = new QualityGradingChannel(new QualityGradingProgressTracker());
        var sut = new QualityGradingBackgroundService(
            new SimpleScopeFactory(db), channel, new QualityGradingProgressTracker(),
            gradingService: null!, runtimeSettings: null!, ownerLookup: null!,
            new TestOptionsMonitor(new QualityGradingOptions { Model = CurrentModel }),
            NullLogger<QualityGradingBackgroundService>.Instance);

        await sut.EnqueueUngradedAsync(new QualityGradingOptions { Model = CurrentModel }, CancellationToken.None);

        var enqueued = new List<int>();
        while (channel.Reader.TryRead(out var item)) enqueued.Add(item.SongId);

        Assert.Contains(ownerSong.Id, enqueued);
        Assert.DoesNotContain(demoSong.Id, enqueued);
    }

    [Fact]
    public async Task EnqueueUngraded_EnqueuesALegacyBlankGrade_ButNotAGenuineUngradeable()
    {
        using var db = NewContext();
        var graded = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var beforeGrade = graded.AddDays(-1);

        var blank = AddSong(db, 1, enrichedAt: beforeGrade);
        AddGrade(db, blank.Id, graded, QualityGradingPrompt.Version, CurrentModel,
            SongQualityVerdict.Ungradeable, raw: AnalysisOnlyReply);
        // A blank row judged nothing, so it is regraded even when its model is no longer current.
        var blankOldModel = AddSong(db, 2, enrichedAt: beforeGrade);
        AddGrade(db, blankOldModel.Id, graded, QualityGradingPrompt.Version, "some/older-model",
            SongQualityVerdict.Ungradeable, raw: "{ }");
        var genuine = AddSong(db, 3, enrichedAt: beforeGrade);
        AddGrade(db, genuine.Id, graded, QualityGradingPrompt.Version, CurrentModel,
            SongQualityVerdict.Ungradeable, raw: GenuineUngradeableReply);

        var channel = new QualityGradingChannel(new QualityGradingProgressTracker());
        var sut = NewService(db, channel);

        await sut.EnqueueUngradedAsync(new QualityGradingOptions { Model = CurrentModel }, CancellationToken.None);

        var enqueued = Drain(channel);
        Assert.Contains(blank.Id, enqueued);
        Assert.Contains(blankOldModel.Id, enqueued);
        Assert.DoesNotContain(genuine.Id, enqueued); // the model's real answer: never regraded in a loop
    }

    [Fact]
    public async Task EnqueueUngraded_FindsALegacyBlankGradeOutsideTheNewestWindow()
    {
        using var db = NewContext();
        var graded = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        var oldBlank = AddSong(db, 1, enrichedAt: graded.AddDays(-30));
        AddGrade(db, oldBlank.Id, graded, QualityGradingPrompt.Version, CurrentModel,
            SongQualityVerdict.Ungradeable, raw: AnalysisOnlyReply);
        var oldGenuine = AddSong(db, 2, enrichedAt: graded.AddDays(-30));
        AddGrade(db, oldGenuine.Id, graded, QualityGradingPrompt.Version, CurrentModel,
            SongQualityVerdict.Ungradeable, raw: GenuineUngradeableReply);
        // The two newest songs fill the window, and both are up to date.
        foreach (var n in new[] { 3, 4 })
        {
            var newest = AddSong(db, n, enrichedAt: graded.AddDays(-1));
            AddGrade(db, newest.Id, graded, QualityGradingPrompt.Version, CurrentModel);
        }

        var channel = new QualityGradingChannel(new QualityGradingProgressTracker());
        var sut = NewService(db, channel);
        var opts = new QualityGradingOptions { Model = CurrentModel, BatchSize = 2 };

        var count = await sut.EnqueueUngradedAsync(opts, CancellationToken.None);

        Assert.Equal(1, count);
        Assert.Equal([oldBlank.Id], Drain(channel));
    }

    [Fact]
    public async Task EnqueueUngraded_PagesThroughBlankCandidates_SoOneThatStaysCannotHoldTheRestOut()
    {
        using var db = NewContext();
        var graded = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        // Both pass the query's verdict/summary pre-filter; only the second is really blank. With a
        // page of one, the genuine row fills the first sweep's page on its own.
        var genuine = AddSong(db, 1, enrichedAt: graded.AddDays(-30));
        AddGrade(db, genuine.Id, graded, QualityGradingPrompt.Version, CurrentModel,
            SongQualityVerdict.Ungradeable, raw: GenuineUngradeableReply);
        var blank = AddSong(db, 2, enrichedAt: graded.AddDays(-30));
        AddGrade(db, blank.Id, graded, QualityGradingPrompt.Version, CurrentModel,
            SongQualityVerdict.Ungradeable, raw: AnalysisOnlyReply);
        var newest = AddSong(db, 3, enrichedAt: graded.AddDays(-1));
        AddGrade(db, newest.Id, graded, QualityGradingPrompt.Version, CurrentModel);

        var channel = new QualityGradingChannel(new QualityGradingProgressTracker());
        var sut = NewService(db, channel);
        var opts = new QualityGradingOptions { Model = CurrentModel, BatchSize = 1 };

        Assert.Equal(0, await sut.EnqueueUngradedAsync(opts, CancellationToken.None));
        Assert.Equal(1, await sut.EnqueueUngradedAsync(opts, CancellationToken.None));
        Assert.Equal([blank.Id], Drain(channel));
    }

    private const string AnalysisOnlyReply =
        """{"analysis":"We need to grade the final chosen metadata ... So just looks_correct. Score 95. Let's output. }" }""";

    private const string GenuineUngradeableReply = """{"score":0,"verdict":"ungradeable"}""";

    private static QualityGradingBackgroundService NewService(MusicHoarderDbContext db, QualityGradingChannel channel) =>
        new(
            new SimpleScopeFactory(db), channel, new QualityGradingProgressTracker(),
            gradingService: null!, runtimeSettings: null!, ownerLookup: null!,
            new TestOptionsMonitor(new QualityGradingOptions { Model = CurrentModel }),
            NullLogger<QualityGradingBackgroundService>.Instance);

    private static List<int> Drain(QualityGradingChannel channel)
    {
        var ids = new List<int>();
        while (channel.Reader.TryRead(out var item)) ids.Add(item.SongId);
        return ids;
    }

    private static SongMetadata AddSong(MusicHoarderDbContext db, int n, DateTime enrichedAt)
    {
        var song = new SongMetadata
        {
            OwnerUserId = WellKnownUsers.OwnerId,
            SourcePath = $"/root/music/song{n}.mp3",
            FileName = $"song{n}.mp3",
            Extension = ".mp3",
            FileSizeBytes = 1_000_000,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Title = $"Song {n}",
            Artist = "Artist",
            EnrichmentStatus = EnrichmentStatus.Matched,
            EnrichedAtUtc = enrichedAt,
        };
        db.Songs.Add(song);
        db.SaveChanges();
        return song;
    }

    private static void AddGrade(
        MusicHoarderDbContext db, int songId, DateTime gradedAt, int promptVersion, string? model,
        SongQualityVerdict verdict = SongQualityVerdict.Good, string? raw = null)
    {
        db.SongQualityGrades.Add(new SongQualityGrade
        {
            SongId = songId,
            OwnerUserId = WellKnownUsers.OwnerId,
            Score = verdict == SongQualityVerdict.Ungradeable ? 0 : 80,
            Verdict = verdict,
            EnrichmentStatusAtGrade = "Matched",
            PromptVersion = promptVersion,
            Model = model,
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

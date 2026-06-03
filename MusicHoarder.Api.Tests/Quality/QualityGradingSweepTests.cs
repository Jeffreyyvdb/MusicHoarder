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
        MusicHoarderDbContext db, int songId, DateTime gradedAt, int promptVersion, string? model)
    {
        db.SongQualityGrades.Add(new SongQualityGrade
        {
            SongId = songId,
            OwnerUserId = WellKnownUsers.OwnerId,
            Score = 80,
            Verdict = SongQualityVerdict.Good,
            EnrichmentStatusAtGrade = "Matched",
            PromptVersion = promptVersion,
            Model = model,
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

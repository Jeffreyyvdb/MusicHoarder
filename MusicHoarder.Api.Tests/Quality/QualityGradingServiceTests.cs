using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Quality;

namespace MusicHoarder.Api.Tests.Quality;

public class QualityGradingServiceTests
{
    [Fact]
    public async Task GradeSong_Configured_PersistsGrade()
    {
        var db = CreateDb();
        var song = AddSong(db);
        await db.SaveChangesAsync();

        var client = new FakeChatClient(configured: true,
            content: """{"score": 18, "verdict": "wrong", "summary":"No provider matched yet a specific song was named.","issues":[{"code":"unsupported_identity","severity":"high"}]}""");
        var service = CreateService(db, client);

        var result = await service.GradeSongAsync(song.Id, force: false);

        Assert.Equal(GradeOutcome.Graded, result.Outcome);
        Assert.Equal(1, client.CallCount);

        var grade = await db.SongQualityGrades.SingleAsync();
        Assert.Equal(18, grade.Score);
        Assert.Equal(SongQualityVerdict.Wrong, grade.Verdict);
        Assert.Equal("test/model", grade.Model);
        Assert.NotNull(grade.InputFingerprint);
        Assert.Equal(song.OwnerUserId, grade.OwnerUserId);
    }

    [Fact]
    public async Task GradeSong_NotConfigured_DoesNotPersist()
    {
        var db = CreateDb();
        var song = AddSong(db);
        await db.SaveChangesAsync();

        var client = new FakeChatClient(configured: false, content: "{}");
        var service = CreateService(db, client);

        var result = await service.GradeSongAsync(song.Id, force: false);

        Assert.Equal(GradeOutcome.NotConfigured, result.Outcome);
        Assert.Equal(0, client.CallCount);
        Assert.False(await db.SongQualityGrades.AnyAsync());
    }

    [Fact]
    public async Task GradeSong_UnchangedDossier_SkipsSecondCall()
    {
        var db = CreateDb();
        var song = AddSong(db);
        await db.SaveChangesAsync();

        var client = new FakeChatClient(configured: true,
            content: """{"score": 80, "verdict": "good"}""");
        var service = CreateService(db, client);

        var first = await service.GradeSongAsync(song.Id, force: false);
        var second = await service.GradeSongAsync(song.Id, force: false);

        Assert.Equal(GradeOutcome.Graded, first.Outcome);
        Assert.Equal(GradeOutcome.Skipped, second.Outcome);
        Assert.Equal(1, client.CallCount);            // not re-called
        Assert.Equal(1, await db.SongQualityGrades.CountAsync());
    }

    [Fact]
    public async Task GradeSong_Force_RegradesAndKeepsHistory()
    {
        var db = CreateDb();
        var song = AddSong(db);
        await db.SaveChangesAsync();

        var client = new FakeChatClient(configured: true,
            content: """{"score": 80, "verdict": "good"}""");
        var service = CreateService(db, client);

        await service.GradeSongAsync(song.Id, force: false);
        var forced = await service.GradeSongAsync(song.Id, force: true);

        Assert.Equal(GradeOutcome.Graded, forced.Outcome);
        Assert.Equal(2, client.CallCount);
        Assert.Equal(2, await db.SongQualityGrades.CountAsync()); // history retained
    }

    [Fact]
    public async Task GradeSong_MissingSong_ReturnsNotFound()
    {
        var db = CreateDb();
        var client = new FakeChatClient(configured: true, content: "{}");
        var service = CreateService(db, client);

        var result = await service.GradeSongAsync(999, force: false);

        Assert.Equal(GradeOutcome.NotFound, result.Outcome);
        Assert.Equal(0, client.CallCount);
    }

    // --- helpers ---

    private static MusicHoarderDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static SongMetadata AddSong(MusicHoarderDbContext db)
    {
        var song = new SongMetadata
        {
            OwnerUserId = Api.Auth.WellKnownUsers.OwnerId,
            SourcePath = "/root/music/a/Juice - Benjamin.mp3",
            FileName = "Juice - Benjamin.mp3",
            Extension = ".mp3",
            FileSizeBytes = 5_800_000,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Title = "Blood On My Jeans",
            Artist = "Juice WRLD",
            EnrichmentStatus = EnrichmentStatus.NeedsReview,
        };
        db.Songs.Add(song);
        return song;
    }

    private static QualityGradingService CreateService(MusicHoarderDbContext db, FakeChatClient client)
    {
        var opts = new TestOptionsMonitor(new QualityGradingOptions { Model = "test/model" });
        var factory = new QualityDossierFactory(new FixedResolver());
        return new QualityGradingService(
            new SimpleScopeFactory(db), client, factory, opts,
            NullLogger<QualityGradingService>.Instance);
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

    private sealed class FixedResolver : IDestinationPathResolver
    {
        public string ResolvePath(SongMetadata song) => "/dest/preview.mp3";
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

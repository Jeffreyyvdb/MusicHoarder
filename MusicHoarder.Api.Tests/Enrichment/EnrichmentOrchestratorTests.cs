using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Enrichment;

public class EnrichmentOrchestratorTests
{
    [Fact]
    public async Task ProcessNextBatch_HighConfidenceMatch_SetsMatchedWithEnrichedFields()
    {
        await using var db = CreateDb();
        var song = AddPendingSong(db, artist: "Juice WRLD", title: "Lucid Dreams");
        await db.SaveChangesAsync();

        var acoustId = new StubAcoustIdService(_ => Task.FromResult<AcoustIdMatch?>(
            new AcoustIdMatch("mb-123", "Lucid Dreams", "Juice WRLD", "Juice WRLD", 0.95f, 240_000)));
        var orchestrator = CreateOrchestrator(db, acoustId);

        var result = await orchestrator.ProcessNextBatchAsync(Guid.NewGuid());
        var updated = await db.Songs.SingleAsync();

        Assert.Equal(1, result.Enriched);
        Assert.Equal(0, result.NeedsReview);
        Assert.Equal(0, result.Failed);
        Assert.Equal(EnrichmentStatus.Matched, updated.EnrichmentStatus);
        Assert.Equal("AcoustID", updated.MatchedBy);
        Assert.NotNull(updated.MatchConfidence);
        Assert.Equal("mb-123", updated.MusicBrainzId);
        Assert.Equal("Juice WRLD", updated.Artist);
        Assert.Equal("Lucid Dreams", updated.Title);
        Assert.NotNull(updated.EnrichedAtUtc);
        Assert.Null(updated.EnrichmentError);
    }

    [Fact]
    public async Task ProcessNextBatch_NoMatch_SetsNeedsReview()
    {
        await using var db = CreateDb();
        AddPendingSong(db, artist: "Unknown", title: "Unknown Track");
        await db.SaveChangesAsync();

        var acoustId = new StubAcoustIdService(_ => Task.FromResult<AcoustIdMatch?>(null));
        var orchestrator = CreateOrchestrator(db, acoustId);

        var result = await orchestrator.ProcessNextBatchAsync(Guid.NewGuid());
        var updated = await db.Songs.SingleAsync();

        Assert.Equal(0, result.Enriched);
        Assert.Equal(1, result.NeedsReview);
        Assert.Equal(EnrichmentStatus.NeedsReview, updated.EnrichmentStatus);
        Assert.Contains("No confident AcoustID match", updated.EnrichmentError);
    }

    [Fact]
    public async Task ProcessNextBatch_ArtistMismatchPenalty_SetsNeedsReview()
    {
        await using var db = CreateDb();
        AddPendingSong(db, artist: "Juice WRLD", title: "Lucid Dreams");
        await db.SaveChangesAsync();

        var acoustId = new StubAcoustIdService(_ => Task.FromResult<AcoustIdMatch?>(
            new AcoustIdMatch("mb-456", "Lucid Dreams", "Stevie Wonder", "Stevie Wonder", 0.90f, 240_000)));
        var orchestrator = CreateOrchestrator(db, acoustId);

        var result = await orchestrator.ProcessNextBatchAsync(Guid.NewGuid());
        var updated = await db.Songs.SingleAsync();

        Assert.Equal(0, result.Enriched);
        Assert.Equal(1, result.NeedsReview);
        Assert.Equal(EnrichmentStatus.NeedsReview, updated.EnrichmentStatus);
        Assert.NotNull(updated.MatchWarnings);
        Assert.Contains("artist_mismatch", updated.MatchWarnings);
    }

    [Fact]
    public async Task ProcessNextBatch_MissingFingerprint_SkippedByQuery()
    {
        await using var db = CreateDb();
        db.Songs.Add(new SongMetadata
        {
            SourcePath = "/source/no-fp.mp3",
            FileName = "no-fp.mp3",
            Extension = ".mp3",
            FileSizeBytes = 5000,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Fingerprint = null,
            DurationSeconds = 240,
            DurationMs = 240_000,
            EnrichmentStatus = EnrichmentStatus.Pending,
        });
        await db.SaveChangesAsync();

        var acoustId = new StubAcoustIdService(_ => throw new InvalidOperationException("should not be called"));
        var orchestrator = CreateOrchestrator(db, acoustId);

        var result = await orchestrator.ProcessNextBatchAsync(Guid.NewGuid());
        var updated = await db.Songs.SingleAsync();

        Assert.Equal(0, result.TotalTracks);
        Assert.Equal(EnrichmentStatus.Pending, updated.EnrichmentStatus);
    }

    [Fact]
    public async Task ProcessNextBatch_AcoustIdThrows_SetsFailed()
    {
        await using var db = CreateDb();
        AddPendingSong(db);
        await db.SaveChangesAsync();

        var acoustId = new StubAcoustIdService(_ =>
            throw new HttpRequestException("connection refused"));
        var orchestrator = CreateOrchestrator(db, acoustId);

        var result = await orchestrator.ProcessNextBatchAsync(Guid.NewGuid());
        var updated = await db.Songs.SingleAsync();

        Assert.Equal(0, result.Enriched);
        Assert.Equal(0, result.NeedsReview);
        Assert.Equal(1, result.Failed);
        Assert.Equal(EnrichmentStatus.Failed, updated.EnrichmentStatus);
        Assert.Contains("connection refused", updated.EnrichmentError);
    }

    [Fact]
    public async Task ProcessNextBatch_DeletedSong_SkipsWithoutChangingStatus()
    {
        await using var db = CreateDb();
        var song = AddPendingSong(db);
        song.DeletedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var acoustId = new StubAcoustIdService(_ => throw new InvalidOperationException("should not be called"));
        var orchestrator = CreateOrchestrator(db, acoustId);

        var result = await orchestrator.ProcessNextBatchAsync(Guid.NewGuid());

        Assert.Equal(0, result.TotalTracks);
    }

    [Fact]
    public async Task ProcessNextBatch_CapturesOriginalMetadata_BeforeOverwriting()
    {
        await using var db = CreateDb();
        var song = AddPendingSong(db, artist: "Original Artist", title: "Original Title");
        song.Album = "Original Album";
        song.Isrc = "USRC12345";
        await db.SaveChangesAsync();

        var acoustId = new StubAcoustIdService(_ => Task.FromResult<AcoustIdMatch?>(
            new AcoustIdMatch("mb-789", "New Title", "New Artist", "New Artist", 0.95f, 240_000)));
        var orchestrator = CreateOrchestrator(db, acoustId);

        await orchestrator.ProcessNextBatchAsync(Guid.NewGuid());
        var updated = await db.Songs.SingleAsync();

        Assert.True(updated.OriginalMetadataCaptured);
        Assert.Equal("Original Artist", updated.OriginalArtist);
        Assert.Equal("Original Title", updated.OriginalTitle);
        Assert.Equal("Original Album", updated.OriginalAlbum);
        Assert.Equal("USRC12345", updated.OriginalIsrc);
        Assert.NotNull(updated.OriginalMetadataCapturedAtUtc);

        Assert.Equal("New Artist", updated.Artist);
        Assert.Equal("New Title", updated.Title);
    }

    [Fact]
    public async Task ProcessNextBatch_OriginalMetadata_NotOverwrittenOnSecondEnrichment()
    {
        await using var db = CreateDb();
        var song = AddPendingSong(db, artist: "Already Changed", title: "Already Changed");
        song.OriginalMetadataCaptured = true;
        song.OriginalArtist = "Very Original";
        song.OriginalTitle = "Very Original Title";
        song.OriginalMetadataCapturedAtUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await db.SaveChangesAsync();

        var acoustId = new StubAcoustIdService(_ => Task.FromResult<AcoustIdMatch?>(
            new AcoustIdMatch("mb-999", "Third Title", "Third Artist", "Third Artist", 0.95f, 240_000)));
        var orchestrator = CreateOrchestrator(db, acoustId);

        await orchestrator.ProcessNextBatchAsync(Guid.NewGuid());
        var updated = await db.Songs.SingleAsync();

        Assert.Equal("Very Original", updated.OriginalArtist);
        Assert.Equal("Very Original Title", updated.OriginalTitle);
        Assert.Equal(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), updated.OriginalMetadataCapturedAtUtc);
    }

    [Fact]
    public async Task ProcessNextBatch_MixedBatch_ReturnsCorrectCounts()
    {
        await using var db = CreateDb();

        var matched = AddPendingSong(db, artist: "Juice WRLD", title: "Lucid Dreams", fingerprint: "fp-match");
        var review = AddPendingSong(db, artist: "Unknown", title: "Unknown", fingerprint: "fp-review");
        var failed = AddPendingSong(db, artist: "Fail", title: "Fail", fingerprint: "fp-fail");
        await db.SaveChangesAsync();

        var acoustId = new StubAcoustIdService(fingerprint => fingerprint switch
        {
            "fp-match" => Task.FromResult<AcoustIdMatch?>(
                new AcoustIdMatch("mb-1", "Lucid Dreams", "Juice WRLD", "Juice WRLD", 0.95f, 240_000)),
            "fp-review" => Task.FromResult<AcoustIdMatch?>(null),
            "fp-fail" => throw new HttpRequestException("boom"),
            _ => Task.FromResult<AcoustIdMatch?>(null)
        });
        var orchestrator = CreateOrchestrator(db, acoustId);

        var result = await orchestrator.ProcessNextBatchAsync(Guid.NewGuid());

        Assert.Equal(3, result.TotalTracks);
        Assert.Equal(1, result.Enriched);
        Assert.Equal(1, result.NeedsReview);
        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public async Task ProcessNextBatch_NoPendingSongs_ReturnsZeroCounts()
    {
        await using var db = CreateDb();
        var orchestrator = CreateOrchestrator(db, new StubAcoustIdService(_ =>
            throw new InvalidOperationException("should not be called")));

        var result = await orchestrator.ProcessNextBatchAsync(Guid.NewGuid());

        Assert.Equal(0, result.TotalTracks);
        Assert.Equal(0, result.Enriched);
        Assert.Equal(0, result.NeedsReview);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public async Task ProcessNextBatch_MatchPreservesExistingArtist_WhenMatchArtistIsBlank()
    {
        await using var db = CreateDb();
        AddPendingSong(db, artist: "Existing Artist", title: "Existing Title");
        await db.SaveChangesAsync();

        var acoustId = new StubAcoustIdService(_ => Task.FromResult<AcoustIdMatch?>(
            new AcoustIdMatch("mb-blank", "New Title", "", "", 0.95f, 240_000)));
        var orchestrator = CreateOrchestrator(db, acoustId);

        await orchestrator.ProcessNextBatchAsync(Guid.NewGuid());
        var updated = await db.Songs.SingleAsync();

        Assert.Equal("Existing Artist", updated.Artist);
        Assert.Equal("New Title", updated.Title);
    }

    [Fact]
    public async Task ProcessNextBatch_StampsEnrichmentLastAttemptedAtUtc()
    {
        await using var db = CreateDb();
        AddPendingSong(db);
        await db.SaveChangesAsync();

        var before = DateTime.UtcNow;
        var acoustId = new StubAcoustIdService(_ => Task.FromResult<AcoustIdMatch?>(
            new AcoustIdMatch("mb-ts", "Title", "Artist", "Artist", 0.95f, 240_000)));
        var orchestrator = CreateOrchestrator(db, acoustId);

        await orchestrator.ProcessNextBatchAsync(Guid.NewGuid());
        var updated = await db.Songs.SingleAsync();

        Assert.NotNull(updated.EnrichmentLastAttemptedAtUtc);
        Assert.True(updated.EnrichmentLastAttemptedAtUtc >= before);
    }

    // --- Helpers ---

    private static SongMetadata AddPendingSong(
        MusicHoarderDbContext db,
        string artist = "Artist",
        string title = "Title",
        string fingerprint = "fp-abc123")
    {
        var song = new SongMetadata
        {
            SourcePath = $"/source/{Guid.NewGuid():N}.mp3",
            FileName = $"{title}.mp3",
            Extension = ".mp3",
            FileSizeBytes = 5_000_000,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Artist = artist,
            Title = title,
            Fingerprint = fingerprint,
            DurationSeconds = 240,
            DurationMs = 240_000,
            EnrichmentStatus = EnrichmentStatus.Pending,
        };
        db.Songs.Add(song);
        return song;
    }

    private static MusicHoarderDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MusicHoarderDbContext(options);
    }

    private static EnrichmentOrchestrator CreateOrchestrator(
        MusicHoarderDbContext db,
        IAcoustIdService acoustIdService)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/source",
            DestinationDirectory = "/dest",
            EnrichmentBatchSize = 100,
            EnrichmentWorkerConcurrency = 1,
        });

        var progressTracker = new EnrichmentProgressTracker();
        var matchValidator = new AcoustIdMatchValidator();
        var scopeFactory = new OrchestratorScopeFactory(db, acoustIdService);

        return new EnrichmentOrchestrator(
            scopeFactory,
            progressTracker,
            matchValidator,
            options,
            NullLogger<EnrichmentOrchestrator>.Instance);
    }

    private sealed class StubAcoustIdService(
        Func<string, Task<AcoustIdMatch?>> handler) : IAcoustIdService
    {
        public Task<AcoustIdMatch?> LookupAsync(string fingerprint, int durationSeconds, CancellationToken ct = default)
            => handler(fingerprint);
    }

    private sealed class OrchestratorScopeFactory(
        MusicHoarderDbContext db,
        IAcoustIdService acoustIdService) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() =>
            new SimpleScope(new SimpleServiceProvider(db, acoustIdService));
    }

    private sealed class SimpleScope(IServiceProvider provider) : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = provider;
        public void Dispose() { }
    }

    private sealed class SimpleServiceProvider(
        MusicHoarderDbContext db,
        IAcoustIdService acoustIdService) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(MusicHoarderDbContext)) return db;
            if (serviceType == typeof(IAcoustIdService)) return acoustIdService;
            return null;
        }
    }
}

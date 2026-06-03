using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Snapshots;

namespace MusicHoarder.Api.Tests.Snapshots;

public class EnrichmentSnapshotServiceTests
{
    private static readonly Guid Owner = WellKnownUsers.OwnerId;

    [Fact]
    public async Task Capture_ComputesAggregatesAndPerSongRows()
    {
        var db = CreateDb();
        var a = AddSong(db, EnrichmentStatus.Matched, confidence: 0.9, matchedBy: "SpotifyAPI");
        var b = AddSong(db, EnrichmentStatus.NeedsReview, confidence: 0.4);
        AddSong(db, EnrichmentStatus.Failed);
        var d = AddSong(db, EnrichmentStatus.Matched, confidence: 0.8);
        var deleted = AddSong(db, EnrichmentStatus.Matched, confidence: 1.0);
        deleted.SoftDelete();
        await db.SaveChangesAsync();

        AddGrade(db, a.Id, 90, SongQualityVerdict.Excellent);
        AddGrade(db, b.Id, 50, SongQualityVerdict.Questionable);
        AddGrade(db, d.Id, 0, SongQualityVerdict.Ungradeable);
        await db.SaveChangesAsync();

        var snap = await CreateService(db).CaptureAsync(Owner, SnapshotTrigger.Manual, "test");

        Assert.NotNull(snap);
        Assert.Equal(4, snap!.TotalSongs);          // deleted excluded
        Assert.Equal(2, snap.MatchedCount);
        Assert.Equal(1, snap.NeedsReviewCount);
        Assert.Equal(1, snap.FailedCount);
        Assert.Equal(0, snap.PendingCount);

        // Avg over matched/needs-review with a confidence: (0.9 + 0.4 + 0.8) / 3.
        Assert.Equal(0.7, snap.AvgMatchConfidence!.Value, 3);

        Assert.Equal(3, snap.GradedCount);           // a, b, d graded
        Assert.Equal(70d, snap.AvgAiScore!.Value, 3); // ungradeable (d) excluded → (90 + 50) / 2
        Assert.Equal(1, snap.AiExcellent);
        Assert.Equal(1, snap.AiQuestionable);
        Assert.Equal(1, snap.AiUngradeable);
        Assert.Equal(0, snap.AiGood);
        Assert.Equal(0, snap.AiWrong);

        var rows = await db.EnrichmentSnapshotSongs.Where(s => s.SnapshotId == snap.Id).ToListAsync();
        Assert.Equal(4, rows.Count);
        var rowA = rows.Single(r => r.SongId == a.Id);
        Assert.Equal(90, rowA.AiScore);
        Assert.Equal(SongQualityVerdict.Excellent, rowA.AiVerdict);
        Assert.Equal("SpotifyAPI", rowA.MatchedBy);
    }

    [Fact]
    public async Task Capture_SkipsWhenNothingChanged()
    {
        var db = CreateDb();
        AddSong(db, EnrichmentStatus.Matched, confidence: 0.9);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var first = await service.CaptureAsync(Owner, SnapshotTrigger.PipelineRun, null);
        var second = await service.CaptureAsync(Owner, SnapshotTrigger.PipelineRun, null);

        Assert.NotNull(first);
        Assert.Null(second);                          // de-duped
        Assert.Equal(1, await db.EnrichmentSnapshots.CountAsync());
    }

    [Fact]
    public async Task Capture_UpdatesInPlaceWhenMetricsChangeOnSameVersion()
    {
        var db = CreateDb();
        var song = AddSong(db, EnrichmentStatus.NeedsReview, confidence: 0.4);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var first = await service.CaptureAsync(Owner, SnapshotTrigger.PipelineRun, null);
        Assert.NotNull(first);
        Assert.Equal(0, first!.MatchedCount);

        song.EnrichmentStatus = EnrichmentStatus.Matched;
        song.MatchConfidence = 0.95;
        await db.SaveChangesAsync();

        var second = await service.CaptureAsync(Owner, SnapshotTrigger.PipelineRun, null);

        // Same version + config → the single point is refreshed in place, not duplicated.
        Assert.NotNull(second);
        Assert.Equal(first.Id, second!.Id);
        Assert.Equal(1, second.MatchedCount);
        Assert.Equal(1, await db.EnrichmentSnapshots.CountAsync());

        // The per-song rows are replaced, not accumulated.
        Assert.Equal(1, await db.EnrichmentSnapshotSongs.CountAsync(s => s.SnapshotId == second.Id));
    }

    [Fact]
    public async Task Capture_CreatesNewSnapshotWhenConfigChanges()
    {
        var db = CreateDb();
        AddSong(db, EnrichmentStatus.Matched, confidence: 0.9);
        await db.SaveChangesAsync();

        var options = new MusicEnricherOptions { EnableDeezerProvider = false };
        var service = CreateService(db, options);

        var first = await service.CaptureAsync(Owner, SnapshotTrigger.PipelineRun, null);
        Assert.NotNull(first);

        // Flip a provider so the behavioral fingerprint (ConfigHash) changes — even with identical metrics.
        options.EnableDeezerProvider = true;

        var second = await service.CaptureAsync(Owner, SnapshotTrigger.PipelineRun, null);

        Assert.NotNull(second);
        Assert.NotEqual(first!.Id, second!.Id);
        Assert.NotEqual(first.ConfigHash, second.ConfigHash);
        Assert.Equal(2, await db.EnrichmentSnapshots.CountAsync());
    }

    [Fact]
    public async Task Capture_CollapsesPreExistingDuplicatesForTheSameVersion()
    {
        var db = CreateDb();
        var song = AddSong(db, EnrichmentStatus.NeedsReview, confidence: 0.4);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        // Capture once to learn the runtime Version + ConfigHash, then plant an older duplicate row.
        var first = await service.CaptureAsync(Owner, SnapshotTrigger.PipelineRun, null);
        Assert.NotNull(first);
        db.EnrichmentSnapshots.Add(new EnrichmentSnapshot
        {
            OwnerUserId = Owner,
            CapturedAtUtc = first!.CapturedAtUtc.AddMinutes(-5),
            Trigger = SnapshotTrigger.PipelineRun,
            Version = first.Version,
            ConfigJson = first.ConfigJson,
            ConfigHash = first.ConfigHash,
            MatchedCount = 0,
        });
        await db.SaveChangesAsync();
        Assert.Equal(2, await db.EnrichmentSnapshots.CountAsync());

        // A change forces the next capture down the update path, which also collapses the duplicate.
        song.EnrichmentStatus = EnrichmentStatus.Matched;
        song.MatchConfidence = 0.95;
        await db.SaveChangesAsync();

        var second = await service.CaptureAsync(Owner, SnapshotTrigger.PipelineRun, null);

        Assert.NotNull(second);
        Assert.Equal(1, await db.EnrichmentSnapshots.CountAsync());
        Assert.Equal(first.ConfigHash, second!.ConfigHash);
    }

    [Fact]
    public async Task Capture_PrunesToRetentionCap()
    {
        var db = CreateDb();
        // 55 pre-existing snapshots, oldest first; distinct metrics so the new one won't de-dup.
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 55; i++)
        {
            db.EnrichmentSnapshots.Add(new EnrichmentSnapshot
            {
                OwnerUserId = Owner,
                CapturedAtUtc = baseTime.AddMinutes(i),
                Trigger = SnapshotTrigger.PipelineRun,
                ConfigJson = "{}",
                ConfigHash = $"hash-{i}",
                MatchedCount = 999,
            });
        }
        AddSong(db, EnrichmentStatus.Matched, confidence: 0.9);
        await db.SaveChangesAsync();

        var snap = await CreateService(db).CaptureAsync(Owner, SnapshotTrigger.PipelineRun, null);

        Assert.NotNull(snap);
        Assert.Equal(50, await db.EnrichmentSnapshots.CountAsync()); // capped
        // The freshly-captured one (real metrics) survives; the oldest manual ones were pruned.
        Assert.True(await db.EnrichmentSnapshots.AnyAsync(e => e.Id == snap!.Id));
        Assert.False(await db.EnrichmentSnapshots.AnyAsync(e => e.ConfigHash == "hash-0"));
    }

    [Fact]
    public void ResolveVersion_PrefersOverrideThenFallsBack()
    {
        Assert.Equal("v9.9.9", EnrichmentSnapshotService.ResolveVersion("v9.9.9"));
        Assert.Equal("v9.9.9", EnrichmentSnapshotService.ResolveVersion("  v9.9.9  "));
        Assert.False(string.IsNullOrWhiteSpace(EnrichmentSnapshotService.ResolveVersion(null)));
        Assert.False(string.IsNullOrWhiteSpace(EnrichmentSnapshotService.ResolveVersion("")));
    }

    [Fact]
    public void ResolveVersion_StripsBuildMetadataSuffix()
    {
        // The .NET SDK appends "+<source-revision>" to dev builds; the timeline + /api/version want
        // a clean semver matching the GitHub release / Docker tag.
        Assert.Equal("1.4.2", EnrichmentSnapshotService.ResolveVersion("1.4.2+abc123"));
        Assert.Equal("1.0.0", EnrichmentSnapshotService.ResolveVersion("  1.0.0+deadbeef  "));
        Assert.Equal("1.4.2", EnrichmentSnapshotService.ResolveVersion("1.4.2"));
    }

    // --- helpers ---

    private static MusicHoarderDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static EnrichmentSnapshotService CreateService(
        MusicHoarderDbContext db, MusicEnricherOptions? enricherOptions = null) =>
        new(db,
            new Monitor<MusicEnricherOptions>(enricherOptions ?? new MusicEnricherOptions()),
            new Monitor<QualityGradingOptions>(new QualityGradingOptions()),
            NullLogger<EnrichmentSnapshotService>.Instance);

    private static int _seq;

    private static SongMetadata AddSong(
        MusicHoarderDbContext db, EnrichmentStatus status, double? confidence = null,
        string? matchedBy = null, bool isDuplicate = false)
    {
        var n = ++_seq;
        var song = new SongMetadata
        {
            OwnerUserId = Owner,
            SourcePath = $"/music/song-{n}.mp3",
            FileName = $"song-{n}.mp3",
            Extension = ".mp3",
            FileSizeBytes = 1_000_000,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Title = $"Title {n}",
            Artist = "Artist",
            EnrichmentStatus = status,
            MatchConfidence = confidence,
            MatchedBy = matchedBy,
            IsDuplicate = isDuplicate,
        };
        db.Songs.Add(song);
        return song;
    }

    private static void AddGrade(MusicHoarderDbContext db, int songId, int score, SongQualityVerdict verdict)
    {
        db.SongQualityGrades.Add(new SongQualityGrade
        {
            SongId = songId,
            OwnerUserId = Owner,
            Score = score,
            Verdict = verdict,
            GradedAtUtc = DateTime.UtcNow,
        });
    }

    private sealed class Monitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}

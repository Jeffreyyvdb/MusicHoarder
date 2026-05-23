using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Auth;

namespace MusicHoarder.Api.Tests.Jobs;

public class IngestRunMonitorTests
{
    private static DbContextOptions<MusicHoarderDbContext> NewDbOptions() =>
        new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static IServiceScopeFactory ScopeFactoryFor(DbContextOptions<MusicHoarderDbContext> options)
    {
        var services = new ServiceCollection();
        // Each scope resolves a fresh context over the same named in-memory store.
        services.AddScoped(_ => new MusicHoarderDbContext(options));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static IngestRunMonitor NewMonitor(
        DbContextOptions<MusicHoarderDbContext> options,
        JobManager jobManager,
        EnrichmentPipelineChannel? channel = null) =>
        new(
            ScopeFactoryFor(options),
            jobManager,
            channel ?? new EnrichmentPipelineChannel(jobManager, new EnrichmentProgressTracker()),
            new TestOwnerLookupService(),
            Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
            {
                SourceDirectory = "/src/music",
                DestinationDirectory = "/dst/library"
            }),
            NullLogger<IngestRunMonitor>.Instance);

    private static SongMetadata OwnerSong(string path, EnrichmentStatus enrichment, LibraryBuildStatus build) => new()
    {
        OwnerUserId = TestUsers.OwnerId,
        SourcePath = path,
        FileName = Path.GetFileName(path),
        Extension = ".mp3",
        FileSizeBytes = 1,
        Fingerprint = "fp",
        DurationSeconds = 100,
        EnrichmentStatus = enrichment,
        LibraryBuildStatus = build,
        LastModifiedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        IndexedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public async Task Opens_a_run_on_the_idle_to_running_edge()
    {
        var options = NewDbOptions();
        var jobManager = new JobManager();
        var monitor = NewMonitor(options, jobManager);

        jobManager.TryStartJob(JobType.Scan, out _, out _);
        await monitor.TickAsync(CancellationToken.None);

        await using var db = new MusicHoarderDbContext(options);
        var runs = await db.IngestRuns.IgnoreQueryFilters().ToListAsync();
        var run = Assert.Single(runs);
        Assert.Equal(IngestRunStatus.Running, run.Status);
        Assert.Equal(TestUsers.OwnerId, run.OwnerUserId);
        Assert.Equal("/src/music", run.SourcePath);
        Assert.Equal("/dst/library", run.DestinationPath);
        Assert.Null(run.EndedAtUtc);
    }

    [Fact]
    public async Task Finalizes_completed_run_with_counts_and_throughput()
    {
        var options = NewDbOptions();
        await using (var seed = new MusicHoarderDbContext(options))
        {
            seed.Songs.AddRange(
                OwnerSong("/src/a.mp3", EnrichmentStatus.Matched, LibraryBuildStatus.Done),
                OwnerSong("/src/b.mp3", EnrichmentStatus.NeedsReview, LibraryBuildStatus.Pending),
                OwnerSong("/src/c.mp3", EnrichmentStatus.Failed, LibraryBuildStatus.Pending));
            await seed.SaveChangesAsync();
        }

        var jobManager = new JobManager();
        var monitor = NewMonitor(options, jobManager);

        jobManager.TryStartJob(JobType.Scan, out var jobId, out _);
        await monitor.TickAsync(CancellationToken.None); // open + observe scan running
        jobManager.SignalComplete(JobType.Scan, jobId);
        await monitor.TickAsync(CancellationToken.None); // finalize

        await using var db = new MusicHoarderDbContext(options);
        var run = await db.IngestRuns.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(IngestRunStatus.Completed, run.Status);
        Assert.NotNull(run.EndedAtUtc);
        Assert.Equal(3, run.TracksDiscovered);
        Assert.Equal(2, run.TracksEnriched);   // Matched + NeedsReview
        Assert.Equal(1, run.TracksCopied);     // Done
        Assert.Equal(1, run.TracksReview);     // NeedsReview
        Assert.Equal(1, run.TracksFailed);     // Failed
        Assert.True(run.ThroughputPerSec >= 0);
    }

    [Fact]
    public async Task Persists_enrichment_cycle_trigger_label_on_the_run()
    {
        var options = NewDbOptions();
        var jobManager = new JobManager();
        var channel = new EnrichmentPipelineChannel(jobManager, new EnrichmentProgressTracker());
        var monitor = NewMonitor(options, jobManager, channel);

        // Enqueuing with a label starts the enrich cycle (Enrich step → Running) and sets the label.
        channel.EnqueueRange([1, 2], label: "Manual enrich — Kanye West");
        await monitor.TickAsync(CancellationToken.None);

        await using var db = new MusicHoarderDbContext(options);
        var run = await db.IngestRuns.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(IngestRunStatus.Running, run.Status);
        Assert.Equal("Manual enrich — Kanye West", run.TriggerLabel);
    }

    [Fact]
    public async Task Leaves_trigger_label_null_for_unlabeled_runs()
    {
        var options = NewDbOptions();
        var jobManager = new JobManager();
        var monitor = NewMonitor(options, jobManager);

        jobManager.TryStartJob(JobType.Scan, out _, out _);
        await monitor.TickAsync(CancellationToken.None);

        await using var db = new MusicHoarderDbContext(options);
        var run = await db.IngestRuns.IgnoreQueryFilters().SingleAsync();
        Assert.Null(run.TriggerLabel);
    }

    [Fact]
    public async Task Maps_cancelled_step_to_cancelled_run()
    {
        var options = NewDbOptions();
        var jobManager = new JobManager();
        var monitor = NewMonitor(options, jobManager);

        jobManager.TryStartJob(JobType.Scan, out var jobId, out _);
        await monitor.TickAsync(CancellationToken.None);
        jobManager.SignalComplete(JobType.Scan, jobId, cancelled: true);
        await monitor.TickAsync(CancellationToken.None);

        await using var db = new MusicHoarderDbContext(options);
        var run = await db.IngestRuns.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(IngestRunStatus.Cancelled, run.Status);
    }

    [Fact]
    public async Task Maps_failed_step_to_failed_run()
    {
        var options = NewDbOptions();
        var jobManager = new JobManager();
        var monitor = NewMonitor(options, jobManager);

        jobManager.TryStartJob(JobType.Scan, out var jobId, out _);
        await monitor.TickAsync(CancellationToken.None);
        jobManager.SignalFailed(JobType.Scan, jobId);
        await monitor.TickAsync(CancellationToken.None);

        await using var db = new MusicHoarderDbContext(options);
        var run = await db.IngestRuns.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(IngestRunStatus.Failed, run.Status);
    }
}

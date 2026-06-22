using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Enrichment;

public class EnrichmentRetrySweepTests
{
    [Fact]
    public async Task Refresh_FlipsToPending_WhenNewProviderEnabled()
    {
        await using var db = CreateDb();
        var song = AddSong(db, EnrichmentStatus.NeedsReview);
        song.ProviderAttempts.Add(new SongProviderAttempt
        {
            SongId = song.Id,
            Provider = EnrichmentProvider.AcoustID,
            Status = ProviderAttemptStatus.NoMatch,
            AttemptedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var channel = new EnrichmentPipelineChannel(new JobManager(), new EnrichmentProgressTracker());
        var service = CreateService(
            db, channel,
            enabled: new HashSet<EnrichmentProvider> { EnrichmentProvider.AcoustID, EnrichmentProvider.SpotifyAPI });

        await service.RefreshStaleStatusesAsync(CancellationToken.None);

        var updated = await db.Songs.AsNoTracking().SingleAsync();
        Assert.Equal(EnrichmentStatus.Pending, updated.EnrichmentStatus);
        Assert.True(channel.Reader.TryRead(out var enqueued));
        Assert.Equal(song.Id, enqueued);
    }

    [Fact]
    public async Task Refresh_LeavesStatus_WhenAllEnabledProvidersTerminal()
    {
        await using var db = CreateDb();
        var song = AddSong(db, EnrichmentStatus.NeedsReview);
        song.ProviderAttempts.Add(new SongProviderAttempt
        {
            SongId = song.Id,
            Provider = EnrichmentProvider.AcoustID,
            Status = ProviderAttemptStatus.NoMatch,
            AttemptedAtUtc = DateTime.UtcNow,
        });
        song.ProviderAttempts.Add(new SongProviderAttempt
        {
            SongId = song.Id,
            Provider = EnrichmentProvider.SpotifyAPI,
            Status = ProviderAttemptStatus.NoMatch,
            AttemptedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var channel = new EnrichmentPipelineChannel(new JobManager(), new EnrichmentProgressTracker());
        var service = CreateService(
            db, channel,
            enabled: new HashSet<EnrichmentProvider> { EnrichmentProvider.AcoustID, EnrichmentProvider.SpotifyAPI });

        await service.RefreshStaleStatusesAsync(CancellationToken.None);

        var updated = await db.Songs.AsNoTracking().SingleAsync();
        Assert.Equal(EnrichmentStatus.NeedsReview, updated.EnrichmentStatus);
        Assert.False(channel.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Refresh_SkipsManuallyApprovedSong_EvenWhenMissingProvider()
    {
        // A locked song missing a newly-enabled provider must NOT be flipped to Pending — the
        // orchestrator skips manually-approved songs, so flipping it would strand it in Pending.
        await using var db = CreateDb();
        var song = AddSong(db, EnrichmentStatus.Matched);
        song.IsManuallyApproved = true;
        song.ProviderAttempts.Add(new SongProviderAttempt
        {
            SongId = song.Id,
            Provider = EnrichmentProvider.AcoustID,
            Status = ProviderAttemptStatus.NoMatch,
            AttemptedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var channel = new EnrichmentPipelineChannel(new JobManager(), new EnrichmentProgressTracker());
        var service = CreateService(
            db, channel,
            enabled: new HashSet<EnrichmentProvider> { EnrichmentProvider.AcoustID, EnrichmentProvider.SpotifyAPI });

        await service.RefreshStaleStatusesAsync(CancellationToken.None);

        var updated = await db.Songs.AsNoTracking().SingleAsync();
        Assert.Equal(EnrichmentStatus.Matched, updated.EnrichmentStatus);
        Assert.False(channel.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Refresh_SkipsDemoSong_EvenWithZeroAttempts()
    {
        // Real-file demo rows are seeded terminal-Matched with NO provider attempts, so without the
        // demo exclusion the refresh sweep would see them as "missing every provider", flip them to
        // Pending and re-enrich them — overwriting the curated demo metadata on every boot.
        await using var db = CreateDb();
        var song = AddSong(db, EnrichmentStatus.Matched, owner: MusicHoarder.Api.Auth.WellKnownUsers.DemoId);
        await db.SaveChangesAsync();

        var channel = new EnrichmentPipelineChannel(new JobManager(), new EnrichmentProgressTracker());
        var service = CreateService(
            db, channel,
            enabled: new HashSet<EnrichmentProvider> { EnrichmentProvider.AcoustID, EnrichmentProvider.SpotifyAPI });

        await service.RefreshStaleStatusesAsync(CancellationToken.None);

        var updated = await db.Songs.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.Equal(EnrichmentStatus.Matched, updated.EnrichmentStatus);
        Assert.False(channel.Reader.TryRead(out _));
    }

    [Fact]
    public async Task EnqueueMissing_SkipsDemoSong()
    {
        await using var db = CreateDb();
        AddSong(db, EnrichmentStatus.Matched, owner: MusicHoarder.Api.Auth.WellKnownUsers.DemoId);
        await db.SaveChangesAsync();

        var channel = new EnrichmentPipelineChannel(new JobManager(), new EnrichmentProgressTracker());
        var service = CreateService(
            db, channel,
            enabled: new HashSet<EnrichmentProvider> { EnrichmentProvider.SpotifyAPI, EnrichmentProvider.YeTracker });

        await service.EnqueueSongsMissingProvidersAsync(CancellationToken.None);

        Assert.False(channel.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Retry_ResetsNeedsReview_WhenFlagSet_PreservesCurrentMetadata()
    {
        await using var db = CreateDb();
        var song = AddSong(db, EnrichmentStatus.NeedsReview);
        // Capture original + apply enrichment so RestoreOriginalMetadata would
        // change Title/Artist if invoked with restoreOriginal: true.
        song.CaptureOriginalMetadata();
        song.OriginalTitle = "Original Title";
        song.OriginalArtist = "Original Artist";
        song.Title = "Edited Title";
        song.Artist = "Edited Artist";
        song.ProviderAttempts.Add(new SongProviderAttempt
        {
            SongId = song.Id,
            Provider = EnrichmentProvider.AcoustID,
            Status = ProviderAttemptStatus.NoMatch,
            AttemptedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var channel = new EnrichmentPipelineChannel(new JobManager(), new EnrichmentProgressTracker());
        var service = CreateService(
            db, channel,
            enabled: new HashSet<EnrichmentProvider> { EnrichmentProvider.AcoustID },
            retryNeedsReview: true);

        await service.RetryStaleStatusesAsync(CancellationToken.None);

        var updated = await db.Songs
            .Include(s => s.ProviderAttempts)
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(EnrichmentStatus.Pending, updated.EnrichmentStatus);
        Assert.Empty(updated.ProviderAttempts);
        // Critical: current (edited) metadata must be preserved — only an
        // explicit user reset should restore originals.
        Assert.Equal("Edited Title", updated.Title);
        Assert.Equal("Edited Artist", updated.Artist);
        Assert.True(channel.Reader.TryRead(out var enqueued));
        Assert.Equal(song.Id, enqueued);
    }

    [Fact]
    public async Task Retry_NoOp_WhenBothFlagsOff()
    {
        await using var db = CreateDb();
        AddSong(db, EnrichmentStatus.NeedsReview);
        AddSong(db, EnrichmentStatus.Failed);
        await db.SaveChangesAsync();

        var channel = new EnrichmentPipelineChannel(new JobManager(), new EnrichmentProgressTracker());
        var service = CreateService(db, channel, enabled: new HashSet<EnrichmentProvider> { EnrichmentProvider.AcoustID });

        await service.RetryStaleStatusesAsync(CancellationToken.None);

        var statuses = await db.Songs.AsNoTracking().Select(s => s.EnrichmentStatus).ToListAsync();
        Assert.Contains(EnrichmentStatus.NeedsReview, statuses);
        Assert.Contains(EnrichmentStatus.Failed, statuses);
        Assert.False(channel.Reader.TryRead(out _));
    }

    [Fact]
    public async Task EnqueueMissing_EnqueuesMatchedSong_WhenNewProviderHasNoAttempt_WithoutFlippingStatus()
    {
        await using var db = CreateDb();
        var song = AddSong(db, EnrichmentStatus.Matched);
        song.ProviderAttempts.Add(new SongProviderAttempt
        {
            SongId = song.Id,
            Provider = EnrichmentProvider.SpotifyAPI,
            Status = ProviderAttemptStatus.Matched,
            AttemptedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var channel = new EnrichmentPipelineChannel(new JobManager(), new EnrichmentProgressTracker());
        var service = CreateService(
            db, channel,
            enabled: new HashSet<EnrichmentProvider> { EnrichmentProvider.SpotifyAPI, EnrichmentProvider.YeTracker });

        await service.EnqueueSongsMissingProvidersAsync(CancellationToken.None);

        var updated = await db.Songs.AsNoTracking().SingleAsync();
        // Status must NOT be flipped — the orchestrator re-runs the missing provider in-place.
        Assert.Equal(EnrichmentStatus.Matched, updated.EnrichmentStatus);
        Assert.True(channel.Reader.TryRead(out var enqueued));
        Assert.Equal(song.Id, enqueued);
    }

    [Fact]
    public async Task EnqueueMissing_SkipsSong_WhenAllEnabledProvidersAttempted()
    {
        await using var db = CreateDb();
        var song = AddSong(db, EnrichmentStatus.Matched);
        song.ProviderAttempts.Add(new SongProviderAttempt
        {
            SongId = song.Id,
            Provider = EnrichmentProvider.SpotifyAPI,
            Status = ProviderAttemptStatus.Matched,
            AttemptedAtUtc = DateTime.UtcNow,
        });
        song.ProviderAttempts.Add(new SongProviderAttempt
        {
            SongId = song.Id,
            Provider = EnrichmentProvider.YeTracker,
            Status = ProviderAttemptStatus.NoMatch,
            AttemptedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var channel = new EnrichmentPipelineChannel(new JobManager(), new EnrichmentProgressTracker());
        var service = CreateService(
            db, channel,
            enabled: new HashSet<EnrichmentProvider> { EnrichmentProvider.SpotifyAPI, EnrichmentProvider.YeTracker });

        await service.EnqueueSongsMissingProvidersAsync(CancellationToken.None);

        Assert.False(channel.Reader.TryRead(out _));
    }

    [Fact]
    public async Task EnqueueMissing_SkipsManuallyApprovedSong()
    {
        await using var db = CreateDb();
        var song = AddSong(db, EnrichmentStatus.Matched);
        song.IsManuallyApproved = true;
        song.ProviderAttempts.Add(new SongProviderAttempt
        {
            SongId = song.Id,
            Provider = EnrichmentProvider.SpotifyAPI,
            Status = ProviderAttemptStatus.Matched,
            AttemptedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var channel = new EnrichmentPipelineChannel(new JobManager(), new EnrichmentProgressTracker());
        var service = CreateService(
            db, channel,
            enabled: new HashSet<EnrichmentProvider> { EnrichmentProvider.SpotifyAPI, EnrichmentProvider.YeTracker });

        await service.EnqueueSongsMissingProvidersAsync(CancellationToken.None);

        Assert.False(channel.Reader.TryRead(out _));
    }

    [Fact]
    public async Task EnqueueMissing_SkipsNonEnrichableSong()
    {
        await using var db = CreateDb();
        var song = AddSong(db, EnrichmentStatus.Matched);
        // Strip everything a provider could act on.
        song.Fingerprint = null;
        song.DurationSeconds = null;
        song.Artist = null;
        song.Title = null;
        song.Isrc = null;
        await db.SaveChangesAsync();

        var channel = new EnrichmentPipelineChannel(new JobManager(), new EnrichmentProgressTracker());
        var service = CreateService(
            db, channel,
            enabled: new HashSet<EnrichmentProvider> { EnrichmentProvider.SpotifyAPI, EnrichmentProvider.YeTracker });

        await service.EnqueueSongsMissingProvidersAsync(CancellationToken.None);

        Assert.False(channel.Reader.TryRead(out _));
    }

    [Fact]
    public async Task EnqueueMissing_NoOp_WhenNoProvidersEnabled()
    {
        await using var db = CreateDb();
        AddSong(db, EnrichmentStatus.Matched);
        await db.SaveChangesAsync();

        var channel = new EnrichmentPipelineChannel(new JobManager(), new EnrichmentProgressTracker());
        var service = CreateService(db, channel, enabled: new HashSet<EnrichmentProvider>());

        await service.EnqueueSongsMissingProvidersAsync(CancellationToken.None);

        Assert.False(channel.Reader.TryRead(out _));
    }

    [Fact]
    public async Task AlgorithmStale_ResetsReviewRow_BelowCurrentVersion_AndEnqueues()
    {
        await using var db = CreateDb();
        var song = AddSong(db, EnrichmentStatus.NeedsReview);
        song.LastEnrichmentAlgorithmVersion = EnrichmentAlgorithm.CurrentVersion - 1;
        song.ProviderAttempts.Add(new SongProviderAttempt
        {
            SongId = song.Id,
            Provider = EnrichmentProvider.AcoustID,
            Status = ProviderAttemptStatus.NoMatch,
            AttemptedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var channel = new EnrichmentPipelineChannel(new JobManager(), new EnrichmentProgressTracker());
        var service = CreateService(db, channel, enabled: new HashSet<EnrichmentProvider> { EnrichmentProvider.AcoustID });

        await service.EnqueueAlgorithmStaleSongsAsync(CancellationToken.None);

        var updated = await db.Songs.Include(s => s.ProviderAttempts).AsNoTracking().SingleAsync();
        Assert.Equal(EnrichmentStatus.Pending, updated.EnrichmentStatus);
        Assert.Empty(updated.ProviderAttempts);
        Assert.True(channel.Reader.TryRead(out var enqueued));
        Assert.Equal(song.Id, enqueued);
    }

    [Fact]
    public async Task AlgorithmStale_SkipsMatchedRow()
    {
        await using var db = CreateDb();
        var song = AddSong(db, EnrichmentStatus.Matched);
        song.LastEnrichmentAlgorithmVersion = EnrichmentAlgorithm.CurrentVersion - 1;
        await db.SaveChangesAsync();

        var channel = new EnrichmentPipelineChannel(new JobManager(), new EnrichmentProgressTracker());
        var service = CreateService(db, channel, enabled: new HashSet<EnrichmentProvider> { EnrichmentProvider.AcoustID });

        await service.EnqueueAlgorithmStaleSongsAsync(CancellationToken.None);

        var updated = await db.Songs.AsNoTracking().SingleAsync();
        Assert.Equal(EnrichmentStatus.Matched, updated.EnrichmentStatus);
        Assert.False(channel.Reader.TryRead(out _));
    }

    [Fact]
    public async Task AlgorithmStale_SkipsRowAlreadyAtCurrentVersion()
    {
        await using var db = CreateDb();
        var song = AddSong(db, EnrichmentStatus.NeedsReview);
        song.LastEnrichmentAlgorithmVersion = EnrichmentAlgorithm.CurrentVersion;
        await db.SaveChangesAsync();

        var channel = new EnrichmentPipelineChannel(new JobManager(), new EnrichmentProgressTracker());
        var service = CreateService(db, channel, enabled: new HashSet<EnrichmentProvider> { EnrichmentProvider.AcoustID });

        await service.EnqueueAlgorithmStaleSongsAsync(CancellationToken.None);

        var updated = await db.Songs.AsNoTracking().SingleAsync();
        Assert.Equal(EnrichmentStatus.NeedsReview, updated.EnrichmentStatus);
        Assert.False(channel.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData(true, false)]  // demo owner
    [InlineData(false, true)]  // manually approved
    public async Task AlgorithmStale_SkipsDemoAndManuallyApprovedRows(bool demo, bool manuallyApproved)
    {
        await using var db = CreateDb();
        var song = AddSong(db, EnrichmentStatus.NeedsReview,
            owner: demo ? MusicHoarder.Api.Auth.WellKnownUsers.DemoId : null);
        song.LastEnrichmentAlgorithmVersion = EnrichmentAlgorithm.CurrentVersion - 1;
        song.IsManuallyApproved = manuallyApproved;
        await db.SaveChangesAsync();

        var channel = new EnrichmentPipelineChannel(new JobManager(), new EnrichmentProgressTracker());
        var service = CreateService(db, channel, enabled: new HashSet<EnrichmentProvider> { EnrichmentProvider.AcoustID });

        await service.EnqueueAlgorithmStaleSongsAsync(CancellationToken.None);

        var updated = await db.Songs.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.Equal(EnrichmentStatus.NeedsReview, updated.EnrichmentStatus);
        Assert.False(channel.Reader.TryRead(out _));
    }

    [Fact]
    public async Task LyricsBackfill_FetchesOnlyEligibleStrandedSongs()
    {
        await using var db = CreateDb();
        // Eligible: matched/needs-review with NotFetched lyrics and a name.
        var matchedStranded = AddSong(db, EnrichmentStatus.Matched);
        var reviewStranded = AddSong(db, EnrichmentStatus.NeedsReview);
        // Ineligible.
        var pending = AddSong(db, EnrichmentStatus.Pending);
        var alreadyFetched = AddSong(db, EnrichmentStatus.Matched);
        alreadyFetched.LyricsStatus = LyricsStatus.Fetched;
        var demoStranded = AddSong(db, EnrichmentStatus.Matched, owner: MusicHoarder.Api.Auth.WellKnownUsers.DemoId);
        await db.SaveChangesAsync();

        var orchestrator = new RecordingOrchestrator();
        var service = CreateService(db, orchestrator);

        await service.BackfillMissingLyricsAsync(CancellationToken.None);

        Assert.Equal(
            new[] { matchedStranded.Id, reviewStranded.Id }.OrderBy(x => x),
            orchestrator.FetchedSongIds.OrderBy(x => x));
    }

    [Fact]
    public async Task LyricsBackfill_Disabled_FetchesNothing()
    {
        await using var db = CreateDb();
        AddSong(db, EnrichmentStatus.Matched);
        await db.SaveChangesAsync();

        var orchestrator = new RecordingOrchestrator();
        var service = CreateService(db, orchestrator, enableLyricsBackfill: false);

        await service.BackfillMissingLyricsAsync(CancellationToken.None);

        Assert.Empty(orchestrator.FetchedSongIds);
    }

    private static SongMetadata AddSong(MusicHoarderDbContext db, EnrichmentStatus status, Guid? owner = null)
    {
        var song = new SongMetadata
        {
            OwnerUserId = owner ?? MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            SourcePath = $"/source/{Guid.NewGuid():N}.mp3",
            FileName = "song.mp3",
            Extension = ".mp3",
            FileSizeBytes = 1_000,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Artist = "Artist",
            Title = "Title",
            Fingerprint = "fp",
            DurationSeconds = 200,
            DurationMs = 200_000,
            EnrichmentStatus = status,
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

    private static EnrichmentBackgroundService CreateService(
        MusicHoarderDbContext db,
        EnrichmentPipelineChannel channel,
        IReadOnlySet<EnrichmentProvider> enabled,
        bool retryNeedsReview = false,
        bool retryFailed = false)
    {
        var opts = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/source",
            DestinationDirectory = "/dest",
            RetryNeedsReviewOnStartup = retryNeedsReview,
            RetryFailedOnStartup = retryFailed,
        });

        return new EnrichmentBackgroundService(
            new SimpleScopeFactory(db),
            new JobManager(),
            new EnrichmentProgressTracker(),
            channel,
            new StubOrchestrator(enabled),
            opts,
            TestPipelineMetrics.Create(),
            NullLogger<EnrichmentBackgroundService>.Instance);
    }

    private static EnrichmentBackgroundService CreateService(
        MusicHoarderDbContext db,
        IEnrichmentOrchestrator orchestrator,
        bool enableLyricsBackfill = true)
    {
        var opts = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/source",
            DestinationDirectory = "/dest",
            EnableLyricsBackfillSweep = enableLyricsBackfill,
        });

        return new EnrichmentBackgroundService(
            new SimpleScopeFactory(db),
            new JobManager(),
            new EnrichmentProgressTracker(),
            new EnrichmentPipelineChannel(new JobManager(), new EnrichmentProgressTracker()),
            orchestrator,
            opts,
            TestPipelineMetrics.Create(),
            NullLogger<EnrichmentBackgroundService>.Instance);
    }

    private sealed class RecordingOrchestrator : IEnrichmentOrchestrator
    {
        private readonly List<int> _fetched = [];
        public IReadOnlyList<int> FetchedSongIds => _fetched;

        public Task<EnrichmentOutcome> ProcessSongAsync(int songId, CancellationToken ct = default)
            => Task.FromResult(EnrichmentOutcome.Skipped);

        public Task<IReadOnlySet<EnrichmentProvider>> GetEnabledProviderEnumsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlySet<EnrichmentProvider>>(new HashSet<EnrichmentProvider>());

        public Task<bool> FetchLyricsForSongAsync(int songId, CancellationToken ct = default)
        {
            lock (_fetched) _fetched.Add(songId);
            return Task.FromResult(false);
        }
    }

    private sealed class StubOrchestrator(IReadOnlySet<EnrichmentProvider> enabled) : IEnrichmentOrchestrator
    {
        public Task<EnrichmentOutcome> ProcessSongAsync(int songId, CancellationToken ct = default)
            => Task.FromResult(EnrichmentOutcome.Skipped);

        public Task<IReadOnlySet<EnrichmentProvider>> GetEnabledProviderEnumsAsync(CancellationToken ct = default) => Task.FromResult(enabled);

        public Task<bool> FetchLyricsForSongAsync(int songId, CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class SimpleScopeFactory(MusicHoarderDbContext db) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new SimpleScope(new SimpleProvider(db));
    }

    private sealed class SimpleScope(IServiceProvider provider) : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = provider;
        public void Dispose() { }
    }

    private sealed class SimpleProvider(MusicHoarderDbContext db) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(MusicHoarderDbContext) ? db : null;
    }
}

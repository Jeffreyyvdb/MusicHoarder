using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Observability;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Settings;
using static MusicHoarder.Api.Tests.Enrichment.EnrichmentOrchestratorTests;

namespace MusicHoarder.Api.Tests.Enrichment;

/// <summary>
/// A provider that cannot handle a song never records an attempt on it: a community tracker only
/// handles its allowlisted artists. The startup sweeps and the terminal-outcome metric must measure
/// "has every provider had its turn" against the providers that can act on the song, or every song
/// outside a tracker's allowlist looks unfinished on every boot.
/// </summary>
public class ProviderApplicabilityTests
{
    [Fact]
    public async Task EnqueueMissing_SkipsSong_WhoseOnlyMissingProviderCannotHandleIt()
    {
        await using var db = CreateDb();
        var song = AddSong(db, EnrichmentStatus.Matched);
        AddAttempt(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.Matched);
        await db.SaveChangesAsync();
        var pipeline = new Pipeline(db);

        await pipeline.Service.EnqueueSongsMissingProvidersAsync(CancellationToken.None);

        Assert.False(pipeline.Channel.Reader.TryRead(out _));
    }

    [Fact]
    public async Task EnqueueMissing_EnqueuesSong_MissingAProviderThatCanHandleIt()
    {
        await using var db = CreateDb();
        var song = AddSong(db, EnrichmentStatus.Matched);
        AddAttempt(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.Matched);
        await db.SaveChangesAsync();
        var pipeline = new Pipeline(db, withDeezer: true);

        await pipeline.Service.EnqueueSongsMissingProvidersAsync(CancellationToken.None);

        Assert.True(pipeline.Channel.Reader.TryRead(out var enqueued));
        Assert.Equal(song.Id, enqueued);
    }

    [Fact]
    public async Task Refresh_LeavesReviewSong_WhoseOnlyMissingProviderCannotHandleIt()
    {
        await using var db = CreateDb();
        var song = AddSong(db, EnrichmentStatus.NeedsReview);
        AddAttempt(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.NoMatch);
        await db.SaveChangesAsync();
        var pipeline = new Pipeline(db);

        await pipeline.Service.RefreshStaleStatusesAsync(CancellationToken.None);

        var stored = await db.Songs.AsNoTracking().SingleAsync();
        Assert.Equal(EnrichmentStatus.NeedsReview, stored.EnrichmentStatus);
        Assert.False(pipeline.Channel.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Refresh_FlipsReviewSong_MissingAProviderThatCanHandleIt()
    {
        await using var db = CreateDb();
        var song = AddSong(db, EnrichmentStatus.NeedsReview);
        AddAttempt(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.NoMatch);
        await db.SaveChangesAsync();
        var pipeline = new Pipeline(db, withDeezer: true);

        await pipeline.Service.RefreshStaleStatusesAsync(CancellationToken.None);

        var stored = await db.Songs.AsNoTracking().SingleAsync();
        Assert.Equal(EnrichmentStatus.Pending, stored.EnrichmentStatus);
        Assert.True(pipeline.Channel.Reader.TryRead(out var enqueued));
        Assert.Equal(song.Id, enqueued);
    }

    [Fact]
    public async Task Worker_RecordsNoTerminalOutcome_ForASongItLeavesUnchanged()
    {
        await using var db = CreateDb();
        var song = AddSong(db, EnrichmentStatus.NeedsReview);
        AddAttempt(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.NoMatch);
        await db.SaveChangesAsync();
        var pipeline = new Pipeline(db);

        var outcomes = await pipeline.ProcessAsync(song.Id);

        Assert.Empty(outcomes);
        Assert.Equal(EnrichmentStatus.NeedsReview, (await db.Songs.AsNoTracking().SingleAsync()).EnrichmentStatus);
    }

    [Fact]
    public async Task Worker_RecordsATerminalOutcome_WhenASongReachesOne()
    {
        await using var db = CreateDb();
        var song = AddSong(db, EnrichmentStatus.Pending);
        await db.SaveChangesAsync();
        var pipeline = new Pipeline(db);

        var outcomes = await pipeline.ProcessAsync(song.Id);

        Assert.Equal(["needs_review"], outcomes);
        Assert.Equal(EnrichmentStatus.NeedsReview, (await db.Songs.AsNoTracking().SingleAsync()).EnrichmentStatus);
    }

    /// <summary>
    /// The real orchestrator and background service over SpotifyAPI (handles every song, finds
    /// nothing), YeTracker (handles none of these songs) and optionally Deezer (handles every song).
    /// </summary>
    private sealed class Pipeline
    {
        private readonly IMeterFactory _meterFactory =
            new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>();
        private readonly JobManager _jobs = new();

        public Pipeline(MusicHoarderDbContext db, bool withDeezer = false)
        {
            var options = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
            {
                SourceDirectory = "/source",
                DestinationDirectory = "/dest",
                AutoStartPipeline = false,
                EnrichmentWorkerConcurrency = 1,
                EnableAcoustIdProvider = false,
                EnableMusicBrainzWebProvider = false,
                EnableSpotifyApiProvider = true,
                EnableTrackerProvider = false,
                EnableYeTrackerProvider = true,
                EnableDeezerProvider = withDeezer,
                EnableAppleMusicProvider = false,
            });
            IEnrichmentProvider[] providers =
            [
                Provider("SpotifyAPI", canHandle: true),
                Provider("YeTracker", canHandle: false),
                .. withDeezer ? [Provider("Deezer", canHandle: true)] : Array.Empty<IEnrichmentProvider>(),
            ];
            var scopes = new DbScopeFactory(db);
            Metrics = new PipelineMetrics(_meterFactory);
            Channel = new EnrichmentPipelineChannel(_jobs, new EnrichmentProgressTracker());
            var orchestrator = new EnrichmentOrchestrator(
                scopes,
                providers,
                new NoLyrics(),
                new NoLyrics(),
                options,
                new FixedRuntimeSettings(options.Value),
                NullLogger<EnrichmentOrchestrator>.Instance);
            Service = new EnrichmentBackgroundService(
                scopes, _jobs, new EnrichmentProgressTracker(), Channel, orchestrator, options, Metrics,
                NullLogger<EnrichmentBackgroundService>.Instance);
        }

        public PipelineMetrics Metrics { get; }
        public EnrichmentPipelineChannel Channel { get; }
        public EnrichmentBackgroundService Service { get; }

        /// <summary>Runs one song through the worker loop and returns the terminal outcomes recorded.</summary>
        public async Task<string[]> ProcessAsync(int songId)
        {
            var outcomes = new ConcurrentQueue<string>();
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Scope == _meterFactory && instrument.Name == "mh.song.terminal")
                    l.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                foreach (var tag in tags)
                    if (tag.Key == "outcome")
                        for (var i = 0; i < value; i++)
                            outcomes.Enqueue((string)tag.Value!);
            });
            listener.Start();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Service.StartAsync(cts.Token);
            Channel.EnqueueRange([songId]);
            // The channel asks for a build once the cycle drains, which is after the worker has
            // recorded the song's outcome.
            await _jobs.BuildTriggers.ReadAsync(cts.Token);
            await cts.CancelAsync();
            try { await Service.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
            return [.. outcomes];
        }
    }

    private static StubEnrichmentProvider Provider(string name, bool canHandle) =>
        new(name, 100, _ => canHandle, _ => Task.FromResult<ProviderOutcome>(new ProviderNoMatch()));

    private static SongMetadata AddSong(MusicHoarderDbContext db, EnrichmentStatus status)
    {
        var song = new SongMetadata
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            SourcePath = $"/source/{Guid.NewGuid():N}.mp3",
            FileName = "song.mp3",
            Extension = ".mp3",
            FileSizeBytes = 1_000,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Artist = "Daft Punk",
            Title = "One More Time",
            DurationSeconds = 200,
            EnrichmentStatus = status,
        };
        db.Songs.Add(song);
        return song;
    }

    private static void AddAttempt(SongMetadata song, EnrichmentProvider provider, ProviderAttemptStatus status) =>
        song.ProviderAttempts.Add(new SongProviderAttempt
        {
            Provider = provider,
            Status = status,
            AttemptedAtUtc = DateTime.UtcNow,
            NextRetryAfterUtc = status == ProviderAttemptStatus.NoMatch ? DateTime.UtcNow.AddDays(30) : null,
        });

    private static MusicHoarderDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private sealed class NoLyrics : ILrcLibService, IGeniusLyricsService
    {
        public Task<LyricsResult?> FetchLyricsAsync(SongMetadata song, CancellationToken ct = default)
            => Task.FromResult<LyricsResult?>(null);
    }

    private sealed class FixedRuntimeSettings(MusicEnricherOptions opts) : IRuntimeSettingsService
    {
        private readonly EffectiveSettings _effective = new(
            opts.EnableAcoustIdProvider,
            opts.EnableMusicBrainzWebProvider,
            opts.EnableSpotifyApiProvider,
            opts.EnableTrackerProvider,
            opts.EnableDeezerProvider,
            opts.EnableAppleMusicProvider,
            QualityGradingEnabled: false,
            AutoDownloadWishlist: false,
            AlbumCompletionEnabled: false,
            ReleaseStagedSourcesEnabled: false,
            UpdatedAtUtc: null);

        public Task<EffectiveSettings> GetAsync(CancellationToken ct = default) => Task.FromResult(_effective);

        public Task<EffectiveSettings> UpdateAsync(RuntimeSettingsUpdate update, CancellationToken ct = default)
            => Task.FromResult(_effective);
    }

    private sealed class DbScopeFactory(MusicHoarderDbContext db) : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        public IServiceScope CreateScope() => this;
        public IServiceProvider ServiceProvider => this;
        public object? GetService(Type serviceType) => serviceType == typeof(MusicHoarderDbContext) ? db : null;
        public void Dispose() { }
    }
}

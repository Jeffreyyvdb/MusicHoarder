using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Enrichment.Providers;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Observability;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Settings;
using MusicHoarder.Api.Tests.Persistence;
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

    [PostgresFact]
    public async Task Refresh_FlipsReviewSong_MissingAProviderThatCanHandleIt()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var db = database.NewContext();
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
        // Due for a retry, so SpotifyAPI runs again, finds nothing again, and the song is saved
        // with the status it already had.
        AddAttempt(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.NoMatch, retryDue: true);
        await db.SaveChangesAsync();
        var pipeline = new Pipeline(db);

        var outcomes = await pipeline.ProcessAsync(song.Id);

        Assert.Empty(outcomes);
        Assert.Equal(EnrichmentStatus.NeedsReview, (await db.Songs.AsNoTracking().SingleAsync()).EnrichmentStatus);
    }

    [Theory]
    [InlineData(ProviderAttemptStatus.NoMatch)]
    [InlineData(ProviderAttemptStatus.Failed)]
    [InlineData(ProviderAttemptStatus.RateLimited)]
    public async Task RetrySweep_SkipsSong_WhoseOnlyDueAttemptIsFromADisabledProvider(ProviderAttemptStatus status)
    {
        await using var db = CreateDb();
        var song = AddSong(db, EnrichmentStatus.Matched);
        AddAttempt(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.Matched);
        // Deezer is disabled in Pipeline(withDeezer: false); its due attempt can never be advanced.
        AddAttempt(song, EnrichmentProvider.Deezer, status, retryDue: true);
        await db.SaveChangesAsync();
        var pipeline = new Pipeline(db);

        await pipeline.Service.SweepRetryableSongsAsync(CancellationToken.None);

        Assert.False(pipeline.Channel.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData(ProviderAttemptStatus.NoMatch)]
    [InlineData(ProviderAttemptStatus.Failed)]
    [InlineData(ProviderAttemptStatus.RateLimited)]
    public async Task RetrySweep_SkipsSong_WhoseOnlyDueAttemptIsFromAProviderThatCannotHandleIt(ProviderAttemptStatus status)
    {
        await using var db = CreateDb();
        var song = AddSong(db, EnrichmentStatus.Matched);
        AddAttempt(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.Matched);
        // YeTracker is enabled, but CanHandle returns false for this song (e.g. after a heal or
        // re-tag moved the artist off the allowlist), so ProcessSongAsync skips it before checking
        // the attempt and never moves its retry time forward.
        AddAttempt(song, EnrichmentProvider.YeTracker, status, retryDue: true);
        await db.SaveChangesAsync();
        var pipeline = new Pipeline(db);

        await pipeline.Service.SweepRetryableSongsAsync(CancellationToken.None);

        Assert.False(pipeline.Channel.Reader.TryRead(out _));
    }

    [Fact]
    public async Task RetrySweep_AdvancesApplicableAttemptOnce_AndDoesNotReenqueueOverStaleInapplicableAttempt()
    {
        await using var db = CreateDb();
        var song = AddSong(db, EnrichmentStatus.NeedsReview);
        // Both attempts are due: SpotifyAPI can handle the song, YeTracker cannot.
        AddAttempt(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.NoMatch, retryDue: true);
        AddAttempt(song, EnrichmentProvider.YeTracker, ProviderAttemptStatus.NoMatch, retryDue: true);
        await db.SaveChangesAsync();
        var pipeline = new Pipeline(db);

        // First sweep must enqueue because SpotifyAPI is applicable and due.
        await pipeline.Service.SweepRetryableSongsAsync(CancellationToken.None);
        await using var reader = pipeline.Channel.ReadAllPrioritizedAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(song.Id, reader.Current);

        // Processing advances SpotifyAPI's cooldown but leaves YeTracker's due attempt untouched.
        await pipeline.Orchestrator.ProcessSongAsync(song.Id);
        pipeline.Channel.MarkProcessed();

        // Second sweep must not re-enqueue the song forever over the unadvanceable YeTracker attempt.
        await pipeline.Service.SweepRetryableSongsAsync(CancellationToken.None);
        Assert.False(pipeline.Channel.Reader.TryRead(out _));
    }

    [Fact]
    public async Task RetrySweep_SkipsDeletedSyntheticAndDemoSongs()
    {
        await using var db = CreateDb();
        var deleted = AddSong(db, EnrichmentStatus.NeedsReview);
        deleted.DeletedAtUtc = DateTime.UtcNow.AddHours(-1);
        AddAttempt(deleted, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.NoMatch, retryDue: true);

        var synthetic = AddSong(db, EnrichmentStatus.NeedsReview);
        synthetic.IsSynthetic = true;
        AddAttempt(synthetic, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.NoMatch, retryDue: true);

        var demo = AddSong(db, EnrichmentStatus.NeedsReview);
        demo.OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.DemoId;
        AddAttempt(demo, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.NoMatch, retryDue: true);

        await db.SaveChangesAsync();
        var pipeline = new Pipeline(db);

        await pipeline.Service.SweepRetryableSongsAsync(CancellationToken.None);

        Assert.False(pipeline.Channel.Reader.TryRead(out _));
    }

    [Fact]
    public async Task StartupBackfill_SkipsRetryableAttemptsFromDisabledOrInapplicableProviders()
    {
        await using var db = CreateDb();
        var disabledAttemptSong = AddSong(db, EnrichmentStatus.Matched);
        AddAttempt(disabledAttemptSong, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.Matched);
        AddAttempt(disabledAttemptSong, EnrichmentProvider.Deezer, ProviderAttemptStatus.NoMatch, retryDue: true);

        var inapplicableAttemptSong = AddSong(db, EnrichmentStatus.Matched);
        AddAttempt(inapplicableAttemptSong, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.Matched);
        AddAttempt(inapplicableAttemptSong, EnrichmentProvider.YeTracker, ProviderAttemptStatus.Failed, retryDue: true);

        var dueApplicableSong = AddSong(db, EnrichmentStatus.NeedsReview);
        AddAttempt(dueApplicableSong, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.NoMatch, retryDue: true);

        await db.SaveChangesAsync();
        var pipeline = new Pipeline(db);

        await pipeline.Service.BackfillPendingSongsAsync(CancellationToken.None);

        Assert.True(pipeline.Channel.Reader.TryRead(out var enqueued));
        Assert.Equal(dueApplicableSong.Id, enqueued);
        Assert.False(pipeline.Channel.Reader.TryRead(out _));
    }

    [Fact]
    public async Task RetryableSongIds_PagesAcrossMultipleBatches_FilteringInapplicableAttempts()
    {
        await using var db = CreateDb();
        var s1 = AddSong(db, EnrichmentStatus.NeedsReview);
        AddAttempt(s1, EnrichmentProvider.YeTracker, ProviderAttemptStatus.NoMatch, retryDue: true);

        var s2 = AddSong(db, EnrichmentStatus.NeedsReview);
        AddAttempt(s2, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.NoMatch, retryDue: true);

        var s3 = AddSong(db, EnrichmentStatus.NeedsReview);
        AddAttempt(s3, EnrichmentProvider.YeTracker, ProviderAttemptStatus.RateLimited, retryDue: true);

        var s4 = AddSong(db, EnrichmentStatus.NeedsReview);
        AddAttempt(s4, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.Failed, retryDue: true);

        var s5 = AddSong(db, EnrichmentStatus.NeedsReview);
        AddAttempt(s5, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.RateLimited, retryDue: true);

        await db.SaveChangesAsync();
        var pipeline = new Pipeline(db);

        var ids = await EnrichmentQueries.RetryableSongIdsAsync(
            db, pipeline.Orchestrator, DateTime.UtcNow, ignoreQueryFilters: true, CancellationToken.None, pageSize: 2);

        Assert.Equal([s2.Id, s4.Id, s5.Id], ids);
    }

    [Fact]
    public async Task RetrySweep_SkipsRealTrackerAttempt_AfterArtistMovesOffAllowlist()
    {
        await using var db = CreateDb();
        // Song previously matched YeTracker's allowlist ("Kanye West") and got a NoMatch whose
        // cooldown has since elapsed, then a heal/re-tag changed its artist off the allowlist.
        var offAllowlist = AddSong(db, EnrichmentStatus.Matched);
        offAllowlist.Artist = "Daft Punk";
        AddAttempt(offAllowlist, EnrichmentProvider.YeTracker, ProviderAttemptStatus.NoMatch, retryDue: true);

        var onAllowlist = AddSong(db, EnrichmentStatus.NeedsReview);
        onAllowlist.Artist = "Kanye West";
        AddAttempt(onAllowlist, EnrichmentProvider.YeTracker, ProviderAttemptStatus.NoMatch, retryDue: true);

        await db.SaveChangesAsync();

        var options = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/source",
            DestinationDirectory = "/dest",
            AutoStartPipeline = false,
            EnableAcoustIdProvider = false,
            EnableMusicBrainzWebProvider = false,
            EnableSpotifyApiProvider = false,
            EnableTrackerProvider = false,
            EnableYeTrackerProvider = true,
            EnableDeezerProvider = false,
            EnableAppleMusicProvider = false,
            YeTrackerArtistAllowlist = ["Kanye West"],
        });
        IEnrichmentProvider[] providers =
        [
            new YeTrackerEnrichmentProvider(null!, options, NullLogger<YeTrackerEnrichmentProvider>.Instance),
        ];
        var scopes = new DbScopeFactory(db);
        var jobs = new JobManager();
        var channel = new EnrichmentPipelineChannel(jobs, new EnrichmentProgressTracker());
        var meterFactory = new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>();
        var orchestrator = new EnrichmentOrchestrator(
            scopes, providers, new NoLyrics(), new NoLyrics(), options,
            new FixedRuntimeSettings(options.Value), new PipelineMetrics(meterFactory),
            NullLogger<EnrichmentOrchestrator>.Instance);
        var service = new EnrichmentBackgroundService(
            scopes, jobs, new EnrichmentProgressTracker(), channel, orchestrator, options,
            NullLogger<EnrichmentBackgroundService>.Instance);

        await service.SweepRetryableSongsAsync(CancellationToken.None);

        Assert.True(channel.Reader.TryRead(out var enqueued));
        Assert.Equal(onAllowlist.Id, enqueued);
        Assert.False(channel.Reader.TryRead(out _));
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

    // The sweeps ask CanHandle of rows that carry only CanHandleColumns. Every other column of these
    // rows holds a value, so the day a provider's CanHandle starts reading one, it answers differently
    // over the whole row than over the copy.
    [Fact]
    public async Task CanHandle_ReadsOnlyCanHandleColumns()
    {
        await using var db = CreateDb();
        var options = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/source",
            DestinationDirectory = "/dest",
            YeTrackerArtistAllowlist = ["Kanye West"],
            TrackerArtistAllowlist = ["Juice WRLD"],
        });
        IEnrichmentProvider[] providers =
        [
            new AcoustIdEnrichmentProvider(null!, null!, NullLogger<AcoustIdEnrichmentProvider>.Instance),
            new AppleMusicEnrichmentProvider(null!, null!, options, NullLogger<AppleMusicEnrichmentProvider>.Instance),
            new MusicBrainzWebEnrichmentProvider(null!, options, NullLogger<MusicBrainzWebEnrichmentProvider>.Instance),
            new SpotifyApiEnrichmentProvider(null!, null!, null!, options,
                Microsoft.Extensions.Options.Options.Create(new SpotifyOptions()),
                NullLogger<SpotifyApiEnrichmentProvider>.Instance),
            new DeezerEnrichmentProvider(null!, options, NullLogger<DeezerEnrichmentProvider>.Instance),
            new TrackerEnrichmentProvider(null!, options, NullLogger<TrackerEnrichmentProvider>.Instance),
            new YeTrackerEnrichmentProvider(null!, options, NullLogger<YeTrackerEnrichmentProvider>.Instance),
        ];
        SongMetadata[] songs =
        [
            Columns(db, 0, "/source/a.mp3", artist: "Kanye West", title: "Runaway", fingerprint: "fp", duration: 548),
            Columns(db, 1, "/source/b.mp3", artist: "Juice WRLD", title: "Lucid Dreams"),
            Columns(db, 2, "/source/Juice WRLD/Goodbye & Good Riddance/04 Lucid Dreams.mp3"),
            Columns(db, 3, "/x", isrc: "USUM71807062"),
            Columns(db, 4, "/x", musicBrainzId: "mb-1"),
            Columns(db, 5, "/x"),
        ];

        foreach (var provider in providers)
        {
            var overWholeRows = songs.Select(provider.CanHandle).ToArray();
            var overCopies = songs.Select(s => provider.CanHandle(IEnrichmentProvider.CanHandleColumns.Copy(s))).ToArray();
            Assert.Equal(overWholeRows, overCopies);
            Assert.Contains(true, overWholeRows);
            Assert.Contains(false, overWholeRows);
        }
    }

    private static SongMetadata Columns(
        MusicHoarderDbContext db, int seed, string sourcePath, string? artist = null, string? title = null,
        string? fingerprint = null, int? duration = null, string? isrc = null, string? musicBrainzId = null)
    {
        var song = SongColumnFill.Blank().FillExcept(db, seed, IEnrichmentProvider.CanHandleColumns.Names);
        song.SourcePath = sourcePath;
        song.Artist = artist;
        song.Title = title;
        song.Fingerprint = fingerprint;
        song.DurationSeconds = duration;
        song.Isrc = isrc;
        song.MusicBrainzId = musicBrainzId;
        return song;
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
                Provider("Deezer", canHandle: true),
            ];
            var scopes = new DbScopeFactory(db);
            Metrics = new PipelineMetrics(_meterFactory);
            Channel = new EnrichmentPipelineChannel(_jobs, new EnrichmentProgressTracker());
            Orchestrator = new EnrichmentOrchestrator(
                scopes,
                providers,
                new NoLyrics(),
                new NoLyrics(),
                options,
                new FixedRuntimeSettings(options.Value),
                Metrics,
                NullLogger<EnrichmentOrchestrator>.Instance);
            Service = new EnrichmentBackgroundService(
                scopes, _jobs, new EnrichmentProgressTracker(), Channel, Orchestrator, options,
                NullLogger<EnrichmentBackgroundService>.Instance);
        }

        public PipelineMetrics Metrics { get; }
        public EnrichmentPipelineChannel Channel { get; }
        public EnrichmentOrchestrator Orchestrator { get; }
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

    private static void AddAttempt(
        SongMetadata song, EnrichmentProvider provider, ProviderAttemptStatus status, bool retryDue = false) =>
        song.ProviderAttempts.Add(new SongProviderAttempt
        {
            Provider = provider,
            Status = status,
            AttemptedAtUtc = DateTime.UtcNow,
            RetryAfterUtc = status == ProviderAttemptStatus.RateLimited
                ? DateTime.UtcNow.AddMinutes(retryDue ? -1 : 30)
                : null,
            NextRetryAfterUtc = status is ProviderAttemptStatus.NoMatch or ProviderAttemptStatus.Failed
                ? DateTime.UtcNow.AddDays(retryDue ? -1 : 30)
                : null,
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

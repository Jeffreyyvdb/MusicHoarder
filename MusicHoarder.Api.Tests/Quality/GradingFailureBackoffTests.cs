using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Quality;
using MusicHoarder.Api.Settings;
using MusicHoarder.Api.Snapshots;

namespace MusicHoarder.Api.Tests.Quality;

/// <summary>
/// A grade that fails persists no grade row, so without a backoff the auto-sweep would re-enqueue the
/// same song/album every pass — flooding the log and burning API credits on a reply that keeps
/// failing. Both grading workers therefore remember a failed id for
/// <see cref="QualityGradingOptions.FailureBackoffSeconds"/>, the sweep skips it for exactly that
/// window, and a later successful grade clears it. These tests drive the real worker loops through
/// the channel and observe the sweep's behaviour, so they hold whichever class owns the bookkeeping.
/// </summary>
public class GradingFailureBackoffTests
{
    private static readonly TimeSpan CycleTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task SongGrading_FailedSong_IsSkippedByTheSweepWhileInsideTheBackoffWindow()
    {
        using var db = NewContext();
        var song = AddSong(db, 1);
        var tracker = new QualityGradingProgressTracker();
        var channel = new QualityGradingChannel(tracker);
        var opts = new QualityGradingOptions { FailureBackoffSeconds = 3600 };
        var sut = NewSongService(db, channel, tracker, new ScriptedSongGrader(GradeOutcome.Failed), opts);

        await sut.StartAsync(CancellationToken.None);
        try
        {
            channel.Enqueue(song.Id, force: true);
            await WaitForCycleAsync(tracker);

            Assert.Equal(0, await sut.EnqueueUngradedAsync(opts, CancellationToken.None));
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task SongGrading_FailedSong_IsSweptAgainOnceTheBackoffWindowHasPassed()
    {
        using var db = NewContext();
        var song = AddSong(db, 1);
        var tracker = new QualityGradingProgressTracker();
        var channel = new QualityGradingChannel(tracker);
        // A zero-second window expires the instant it is set, so the very next sweep sees the song.
        var opts = new QualityGradingOptions { FailureBackoffSeconds = 0 };
        var sut = NewSongService(db, channel, tracker, new ScriptedSongGrader(GradeOutcome.Failed), opts);

        await sut.StartAsync(CancellationToken.None);
        try
        {
            channel.Enqueue(song.Id, force: true);
            await WaitForCycleAsync(tracker);

            Assert.Equal(1, await sut.EnqueueUngradedAsync(opts, CancellationToken.None));
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task SongGrading_SuccessfulGradeClearsTheBackoff_SoTheSweepSeesTheSongAgain()
    {
        using var db = NewContext();
        var song = AddSong(db, 1);
        var tracker = new QualityGradingProgressTracker();
        var channel = new QualityGradingChannel(tracker);
        var opts = new QualityGradingOptions { FailureBackoffSeconds = 3600 };
        var grader = new ScriptedSongGrader(GradeOutcome.Failed, GradeOutcome.Graded);
        var sut = NewSongService(db, channel, tracker, grader, opts);

        await sut.StartAsync(CancellationToken.None);
        try
        {
            channel.Enqueue(song.Id, force: true);
            await WaitForCycleAsync(tracker);
            Assert.Equal(0, await sut.EnqueueUngradedAsync(opts, CancellationToken.None)); // backing off

            channel.Enqueue(song.Id, force: true); // manual "grade now" bypasses the sweep's backoff
            await WaitForCycleAsync(tracker);

            // The fake grader persists no row, so the song is still "never graded" — only the
            // cleared backoff explains it being enqueued again.
            Assert.Equal(1, await sut.EnqueueUngradedAsync(opts, CancellationToken.None));
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task SongGrading_WorkerException_BacksOffTheSongLikeAFailedOutcome()
    {
        using var db = NewContext();
        var song = AddSong(db, 1);
        var tracker = new QualityGradingProgressTracker();
        var channel = new QualityGradingChannel(tracker);
        var opts = new QualityGradingOptions { FailureBackoffSeconds = 3600 };
        var sut = NewSongService(db, channel, tracker, new ThrowingSongGrader(), opts);

        await sut.StartAsync(CancellationToken.None);
        try
        {
            channel.Enqueue(song.Id, force: true);
            await WaitForCycleAsync(tracker);

            Assert.Equal(0, await sut.EnqueueUngradedAsync(opts, CancellationToken.None));
            Assert.Equal(1, tracker.GetCurrent()?.Failed);
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AlbumGrading_FailedAlbum_IsSkippedByTheSweepWhileInsideTheBackoffWindow()
    {
        using var db = NewContext();
        var album = AddFetchedAlbum(db, 1);
        var tracker = new AlbumGradingProgressTracker();
        var channel = new AlbumGradingChannel(tracker);
        var opts = new QualityGradingOptions { FailureBackoffSeconds = 3600 };
        var sut = NewAlbumService(db, channel, tracker, new ScriptedAlbumGrader(GradeOutcome.Failed), opts);

        await sut.StartAsync(CancellationToken.None);
        try
        {
            channel.Enqueue(album.Id, force: true);
            await WaitForCycleAsync(tracker);

            Assert.Equal(0, await sut.EnqueueUngradedAsync(opts, CancellationToken.None));
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AlbumGrading_SuccessfulGradeClearsTheBackoff_SoTheSweepSeesTheAlbumAgain()
    {
        using var db = NewContext();
        var album = AddFetchedAlbum(db, 1);
        var tracker = new AlbumGradingProgressTracker();
        var channel = new AlbumGradingChannel(tracker);
        var opts = new QualityGradingOptions { FailureBackoffSeconds = 3600 };
        var grader = new ScriptedAlbumGrader(GradeOutcome.Failed, GradeOutcome.Graded);
        var sut = NewAlbumService(db, channel, tracker, grader, opts);

        await sut.StartAsync(CancellationToken.None);
        try
        {
            channel.Enqueue(album.Id, force: true);
            await WaitForCycleAsync(tracker);
            Assert.Equal(0, await sut.EnqueueUngradedAsync(opts, CancellationToken.None)); // backing off

            channel.Enqueue(album.Id, force: true);
            await WaitForCycleAsync(tracker);

            Assert.Equal(1, await sut.EnqueueUngradedAsync(opts, CancellationToken.None));
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task SongGrading_FailedRegradeOfABlankGradeOutsideTheWindow_IsSkippedWhileBackingOff()
    {
        // A blank grade on an old song reaches the sweep through its own query, not the newest-songs
        // window — a failed regrade must back it off there too rather than re-enqueue it every pass.
        using var db = NewContext();
        var blank = AddSong(db, 1);
        AddSongGrade(db, blank.Id, SongQualityVerdict.Ungradeable, raw: "{ }");
        foreach (var n in new[] { 2, 3 }) // two newer, up-to-date songs fill the window
        {
            var newer = AddSong(db, n);
            newer.EnrichedAtUtc = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
            db.SaveChanges();
            AddSongGrade(db, newer.Id, SongQualityVerdict.Good);
        }

        var tracker = new QualityGradingProgressTracker();
        var channel = new QualityGradingChannel(tracker);
        var opts = new QualityGradingOptions { FailureBackoffSeconds = 3600, BatchSize = 2 };
        var sut = NewSongService(db, channel, tracker, new ScriptedSongGrader(GradeOutcome.Failed), opts);

        await sut.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(1, await sut.EnqueueUngradedAsync(opts, CancellationToken.None)); // the blank grade
            await WaitForCycleAsync(tracker);

            Assert.Equal(0, await sut.EnqueueUngradedAsync(opts, CancellationToken.None));
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AlbumGrading_FailedRegradeOfABlankGradeOutsideTheWindow_IsSkippedWhileBackingOff()
    {
        using var db = NewContext();
        var blank = AddFetchedAlbum(db, 1);
        AddAlbumGrade(db, blank.Id, SongQualityVerdict.Ungradeable, raw: "{ }");
        var newer = AddFetchedAlbum(db, 2); // the only album in the window, up to date
        newer.FetchedAtUtc = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        db.SaveChanges();
        AddAlbumGrade(db, newer.Id, SongQualityVerdict.Good);

        var tracker = new AlbumGradingProgressTracker();
        var channel = new AlbumGradingChannel(tracker);
        var opts = new QualityGradingOptions { FailureBackoffSeconds = 3600, BatchSize = 2 };
        var sut = NewAlbumService(db, channel, tracker, new ScriptedAlbumGrader(GradeOutcome.Failed), opts);

        await sut.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(1, await sut.EnqueueUngradedAsync(opts, CancellationToken.None)); // the blank grade
            await WaitForCycleAsync(tracker);

            Assert.Equal(0, await sut.EnqueueUngradedAsync(opts, CancellationToken.None));
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    private static QualityGradingBackgroundService NewSongService(
        MusicHoarderDbContext db, QualityGradingChannel channel, QualityGradingProgressTracker tracker,
        IQualityGradingService grader, QualityGradingOptions opts) =>
        new(
            new SimpleScopeFactory(db), channel, tracker, grader,
            new StubRuntimeSettings(), new OwnerLookupService(),
            new TestOptionsMonitor(opts), NullLogger<QualityGradingBackgroundService>.Instance);

    private static AlbumGradingBackgroundService NewAlbumService(
        MusicHoarderDbContext db, AlbumGradingChannel channel, AlbumGradingProgressTracker tracker,
        IAlbumGradingService grader, QualityGradingOptions opts) =>
        new(
            new SimpleScopeFactory(db), channel, tracker, grader,
            new StubRuntimeSettings(), new TestOptionsMonitor(opts),
            NullLogger<AlbumGradingBackgroundService>.Instance);

    /// <summary>Waits for the worker to drain the current grading cycle (the channel completes it on the last item).</summary>
    private static async Task WaitForCycleAsync(QualityGradingProgressTracker tracker)
    {
        var deadline = DateTime.UtcNow + CycleTimeout;
        while (tracker.GetCurrent() is not { IsComplete: true })
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The grading worker did not complete the cycle in time.");
            await Task.Delay(10);
        }
    }

    private static SongMetadata AddSong(MusicHoarderDbContext db, int n)
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
            EnrichedAtUtc = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Songs.Add(song);
        db.SaveChanges();
        return song;
    }

    private static CanonicalAlbum AddFetchedAlbum(MusicHoarderDbContext db, int n)
    {
        var album = new CanonicalAlbum
        {
            ArtistKey = $"artist{n}",
            AlbumKey = $"album{n}",
            Status = CanonicalAlbumStatus.Fetched,
            FetchedAtUtc = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.CanonicalAlbums.Add(album);
        db.SaveChanges();
        return album;
    }

    /// <summary>A grade written after every fixture's enrichment/fetch, so only its verdict decides.</summary>
    private static readonly DateTime GradedAt = new(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);

    private static void AddSongGrade(MusicHoarderDbContext db, int songId, SongQualityVerdict verdict, string? raw = null)
    {
        db.SongQualityGrades.Add(new SongQualityGrade
        {
            SongId = songId,
            OwnerUserId = WellKnownUsers.OwnerId,
            Verdict = verdict,
            PromptVersion = QualityGradingPrompt.Version,
            RawResponseJson = raw,
            GradedAtUtc = GradedAt,
        });
        db.SaveChanges();
    }

    private static void AddAlbumGrade(MusicHoarderDbContext db, int albumId, SongQualityVerdict verdict, string? raw = null)
    {
        db.CanonicalAlbumQualityGrades.Add(new CanonicalAlbumQualityGrade
        {
            CanonicalAlbumId = albumId,
            OwnerUserId = WellKnownUsers.OwnerId,
            Verdict = verdict,
            PromptVersion = AlbumGradingPrompt.Version,
            RawResponseJson = raw,
            GradedAtUtc = GradedAt,
        });
        db.SaveChanges();
    }

    private static MusicHoarderDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    /// <summary>Returns the scripted outcomes in order, repeating the last one once the script runs out.</summary>
    private sealed class ScriptedSongGrader(params GradeOutcome[] outcomes) : IQualityGradingService
    {
        private int _calls;

        public Task<GradeSongResult> GradeSongAsync(int songId, bool force = false, CancellationToken ct = default)
        {
            var outcome = outcomes[Math.Min(_calls++, outcomes.Length - 1)];
            return Task.FromResult(new GradeSongResult(outcome, null, outcome == GradeOutcome.Failed ? "scripted failure" : null, "scripted"));
        }
    }

    private sealed class ThrowingSongGrader : IQualityGradingService
    {
        public Task<GradeSongResult> GradeSongAsync(int songId, bool force = false, CancellationToken ct = default)
            => throw new InvalidOperationException("scripted crash");
    }

    private sealed class ScriptedAlbumGrader(params GradeOutcome[] outcomes) : IAlbumGradingService
    {
        private int _calls;

        public Task<GradeAlbumResult> GradeAlbumAsync(int canonicalAlbumId, bool force = false, CancellationToken ct = default)
        {
            var outcome = outcomes[Math.Min(_calls++, outcomes.Length - 1)];
            return Task.FromResult(new GradeAlbumResult(outcome, null, outcome == GradeOutcome.Failed ? "scripted failure" : null, "scripted"));
        }

        public Task<AlbumGradingDossier?> BuildDossierAsync(int canonicalAlbumId, CancellationToken ct = default)
            => Task.FromResult<AlbumGradingDossier?>(null);
    }

    private sealed class StubRuntimeSettings : IRuntimeSettingsService
    {
        private static readonly EffectiveSettings Settings = new(
            true, true, true, true, true, true,
            QualityGradingEnabled: true, AutoDownloadWishlist: false, AlbumCompletionEnabled: false,
            ReleaseStagedSourcesEnabled: false, UpdatedAtUtc: null);

        public Task<EffectiveSettings> GetAsync(CancellationToken ct = default) => Task.FromResult(Settings);
        public Task<EffectiveSettings> UpdateAsync(RuntimeSettingsUpdate update, CancellationToken ct = default) => Task.FromResult(Settings);
    }

    /// <summary>The song worker captures a timeline snapshot when a run drains; nothing to persist here.</summary>
    private sealed class NoOpSnapshotService : IEnrichmentSnapshotService
    {
        public Task<EnrichmentSnapshot?> CaptureAsync(Guid ownerId, SnapshotTrigger trigger, string? triggerLabel, CancellationToken ct = default)
            => Task.FromResult<EnrichmentSnapshot?>(null);
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
                serviceType == typeof(MusicHoarderDbContext) ? db
                : serviceType == typeof(IEnrichmentSnapshotService) ? new NoOpSnapshotService()
                : null;
        }
    }
}

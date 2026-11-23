using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Enrichment;

/// <summary>
/// The background timing sweep's choice of WHICH songs to spend audio on: probes reach songs the free checks
/// merely passed, and a probe that hears the timing drift leads to an automatic re-sync — within the daily
/// cap, without overwriting a transcription that already exists, and without counting our own throttling as
/// a failure. The transcriber is faked; the probe's own reasoning is covered by <see cref="LyricsTimingProbeTests"/>.
/// </summary>
public class LyricsTimingCheckServiceTests : IDisposable
{
    private readonly DirectoryInfo _tmpDir = Directory.CreateTempSubdirectory("mh-timing-sweep-tests");

    public void Dispose()
    {
        try { _tmpDir.Delete(recursive: true); }
        catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    private static readonly string Lrc = string.Join(
        '\n', Enumerable.Range(0, 10).Select(i => $"[00:{10 + (i * 5):00}.00]line {i} words here"));

    private const string AlignedLrc = "[00:12.00]line 0 words here\n[00:17.00]line 1 words here";

    // --- Probe pass: which songs get a window ---

    [Fact]
    public async Task probe_pass_listens_to_songs_only_the_free_checks_passed_after_the_suspect_ones()
    {
        await using var db = CreateDbContext();
        var suspect = AddSong(db, 1, LyricsSyncStatus.Suspect);
        var unconfirmed = AddSong(db, 2, LyricsSyncStatus.Ok);
        var alreadyProbed = AddSong(db, 3, LyricsSyncStatus.Ok, probeAttempts: 1);
        var showingAi = AddSong(db, 4, LyricsSyncStatus.Ok, preferred: PreferredLyricsSource.Transcribed);
        await db.SaveChangesAsync();

        var transcriber = new FakeTranscriber();
        var service = NewService(db, transcriber, new LyricsTimingOptions { SweepBatchSize = 10 });

        Assert.True(await service.RunProbePassAsync(CancellationToken.None));

        // Suspect first, then the never-probed Ok song; nothing else.
        Assert.Equal([suspect.SourcePath!, unconfirmed.SourcePath!], transcriber.ClipCalls);
        Assert.Equal(1, unconfirmed.LyricsSyncProbeAttempts);
        Assert.Equal(1, alreadyProbed.LyricsSyncProbeAttempts);
        Assert.Equal(0, showingAi.LyricsSyncProbeAttempts);
    }

    [Fact]
    public async Task probe_pass_fills_the_batch_with_suspects_before_any_unconfirmed_song()
    {
        await using var db = CreateDbContext();
        var suspect = AddSong(db, 1, LyricsSyncStatus.Suspect);
        AddSong(db, 2, LyricsSyncStatus.Ok);
        await db.SaveChangesAsync();

        var transcriber = new FakeTranscriber();
        var service = NewService(db, transcriber, new LyricsTimingOptions { SweepBatchSize = 1 });

        await service.RunProbePassAsync(CancellationToken.None);

        Assert.Equal([suspect.SourcePath!], transcriber.ClipCalls);
    }

    [Fact]
    public async Task probe_pass_leaves_unconfirmed_songs_alone_when_switched_off()
    {
        await using var db = CreateDbContext();
        AddSong(db, 1, LyricsSyncStatus.Ok);
        await db.SaveChangesAsync();

        var transcriber = new FakeTranscriber();
        var service = NewService(db, transcriber, new LyricsTimingOptions { ProbeUnconfirmedSongs = false });

        Assert.False(await service.RunProbePassAsync(CancellationToken.None));
        Assert.Empty(transcriber.ClipCalls);
    }

    // --- Re-sync pass ---

    [Fact]
    public async Task resync_pass_retimes_a_drifting_song_and_shows_it()
    {
        await using var db = CreateDbContext();
        var song = AddSong(db, 1, LyricsSyncStatus.Suspect, probeAttempts: 1, built: true);
        await db.SaveChangesAsync();

        var transcriber = new FakeTranscriber();
        var service = NewService(db, transcriber, new LyricsTimingOptions());

        Assert.True(await service.RunResyncPassAsync(CancellationToken.None));

        Assert.Equal([song.SourcePath!], transcriber.FullCalls);
        Assert.Equal(TranscriptionStatus.Completed, song.TranscriptionStatus);
        Assert.Equal(AlignedLrc, song.TranscribedSyncedLyrics);
        Assert.Equal(PreferredLyricsSource.Transcribed, song.PreferredLyricsSource);
        Assert.Equal(LyricsProvenance.AiEnhanced, song.LyricsProvenance);
        // The built file carries the lyrics as a tag, so it is queued to be re-tagged.
        Assert.Equal(LibraryBuildStatus.Pending, song.LibraryBuildStatus);
        // The LRCLIB lyrics are kept for the compare view.
        Assert.Equal(Lrc, song.SyncedLyrics);
    }

    [Fact]
    public async Task resync_pass_keeps_lrclib_shown_when_the_lyrics_could_not_be_aligned()
    {
        await using var db = CreateDbContext();
        var song = AddSong(db, 1, LyricsSyncStatus.Suspect, probeAttempts: 1);
        await db.SaveChangesAsync();

        var transcriber = new FakeTranscriber { Aligned = false };
        var service = NewService(db, transcriber, new LyricsTimingOptions());

        await service.RunResyncPassAsync(CancellationToken.None);

        Assert.Equal(TranscriptionStatus.Completed, song.TranscriptionStatus);
        Assert.Equal(PreferredLyricsSource.Lrclib, song.PreferredLyricsSource);
        Assert.Equal(LyricsProvenance.Human, song.LyricsProvenance);
    }

    [Fact]
    public async Task resync_pass_only_takes_songs_a_probe_heard_drifting_that_were_never_transcribed()
    {
        await using var db = CreateDbContext();
        AddSong(db, 1, LyricsSyncStatus.Suspect); // free checks only, no probe yet
        AddSong(db, 2, LyricsSyncStatus.Ok, probeAttempts: 1);
        AddSong(db, 3, LyricsSyncStatus.Corrected, probeAttempts: 1);
        AddSong(db, 4, LyricsSyncStatus.Suspect, probeAttempts: 1, transcription: TranscriptionStatus.Completed);
        AddSong(db, 5, LyricsSyncStatus.Suspect, probeAttempts: 1, transcription: TranscriptionStatus.Failed);
        AddSong(db, 6, LyricsSyncStatus.Suspect, probeAttempts: 1, plainLyrics: null);
        AddSong(db, 7, LyricsSyncStatus.Suspect, probeAttempts: 1, instrumental: true);
        AddSong(db, 8, LyricsSyncStatus.Suspect, probeAttempts: 1, owner: WellKnownUsers.DemoId);
        AddSong(db, 9, LyricsSyncStatus.Suspect, probeAttempts: 1, deleted: true);
        await db.SaveChangesAsync();

        var transcriber = new FakeTranscriber();
        var service = NewService(db, transcriber, new LyricsTimingOptions());

        Assert.False(await service.RunResyncPassAsync(CancellationToken.None));
        Assert.Empty(transcriber.FullCalls);
    }

    [Fact]
    public async Task resync_pass_stops_at_the_daily_cap()
    {
        await using var db = CreateDbContext();
        for (var id = 1; id <= 5; id++)
            AddSong(db, id, LyricsSyncStatus.Suspect, probeAttempts: 1);
        await db.SaveChangesAsync();

        var transcriber = new FakeTranscriber();
        var service = NewService(db, transcriber, new LyricsTimingOptions { AutoResyncPerDay = 2 });

        await service.RunResyncPassAsync(CancellationToken.None);
        Assert.False(await service.RunResyncPassAsync(CancellationToken.None));

        Assert.Equal(2, transcriber.FullCalls.Count);
    }

    [Fact]
    public async Task resync_pass_does_nothing_when_the_cap_is_zero_or_it_is_switched_off()
    {
        await using var db = CreateDbContext();
        AddSong(db, 1, LyricsSyncStatus.Suspect, probeAttempts: 1);
        await db.SaveChangesAsync();

        var transcriber = new FakeTranscriber();
        Assert.False(await NewService(db, transcriber, new LyricsTimingOptions { AutoResyncPerDay = 0 })
            .RunResyncPassAsync(CancellationToken.None));
        Assert.False(await NewService(db, transcriber, new LyricsTimingOptions { EnableAutoResync = false })
            .RunResyncPassAsync(CancellationToken.None));
        Assert.Empty(transcriber.FullCalls);
    }

    [Fact]
    public async Task resync_pass_spends_the_songs_length_from_the_audio_budget_and_waits_when_it_is_gone()
    {
        await using var db = CreateDbContext();
        AddSong(db, 1, LyricsSyncStatus.Suspect, probeAttempts: 1, durationSeconds: 200);
        AddSong(db, 2, LyricsSyncStatus.Suspect, probeAttempts: 1, durationSeconds: 200);
        await db.SaveChangesAsync();

        var transcriber = new FakeTranscriber();
        var opts = new LyricsTimingOptions { AudioSecondsPerHour = 300, AudioSecondsPerDay = 300 };
        var service = NewService(db, transcriber, opts);

        await service.RunResyncPassAsync(CancellationToken.None);

        // One song fits; the second would take the budget past its limit.
        Assert.Single(transcriber.FullCalls);
        Assert.Equal(200, Assert.Single(await db.TranscriptionAudioSpends.ToListAsync()).AudioSeconds, precision: 3);
    }

    [Theory]
    [InlineData(1_000, 20_000)]
    [InlineData(6_000, 1_000)]
    public async Task resync_pass_passes_over_a_song_longer_than_the_background_ceiling(int perHour, int perDay)
    {
        await using var db = CreateDbContext();
        var older = AddSong(db, 1, LyricsSyncStatus.Suspect, probeAttempts: 1, durationSeconds: 200);
        var tooLong = AddSong(db, 2, LyricsSyncStatus.Suspect, probeAttempts: 1, durationSeconds: 1_200);
        await db.SaveChangesAsync();

        var transcriber = new FakeTranscriber();
        var opts = new LyricsTimingOptions { AudioSecondsPerHour = perHour, AudioSecondsPerDay = perDay };
        var service = NewService(db, transcriber, opts);

        Assert.True(await service.RunResyncPassAsync(CancellationToken.None));

        Assert.Equal([older.SourcePath!], transcriber.FullCalls);
        Assert.Equal(TranscriptionStatus.Completed, older.TranscriptionStatus);
        Assert.Equal(TranscriptionStatus.NotRequested, tooLong.TranscriptionStatus);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public async Task resync_pass_passes_over_a_song_of_unknown_length_when_the_ceiling_is_below_its_charge(int? duration)
    {
        await using var db = CreateDbContext();
        var older = AddSong(db, 1, LyricsSyncStatus.Suspect, probeAttempts: 1, durationSeconds: 100);
        var unknown = AddSong(db, 2, LyricsSyncStatus.Suspect, probeAttempts: 1, durationSeconds: duration);
        await db.SaveChangesAsync();

        var transcriber = new FakeTranscriber();
        var opts = new LyricsTimingOptions { AudioSecondsPerHour = 250, AudioSecondsPerDay = 20_000 };
        var service = NewService(db, transcriber, opts);

        Assert.True(await service.RunResyncPassAsync(CancellationToken.None));

        Assert.Equal([older.SourcePath!], transcriber.FullCalls);
        Assert.Equal(TranscriptionStatus.NotRequested, unknown.TranscriptionStatus);
    }

    [Fact]
    public async Task a_rate_limited_resync_is_not_recorded_and_gives_its_audio_back()
    {
        await using var db = CreateDbContext();
        var song = AddSong(db, 1, LyricsSyncStatus.Suspect, probeAttempts: 1);
        await db.SaveChangesAsync();

        var transcriber = new FakeTranscriber { Throw = new TranscriptionRateLimitedException("busy") };
        var service = NewService(db, transcriber, new LyricsTimingOptions());

        Assert.False(await service.RunResyncPassAsync(CancellationToken.None));

        Assert.Equal(TranscriptionStatus.NotRequested, song.TranscriptionStatus);
        Assert.Empty(await db.TranscriptionAudioSpends.ToListAsync());
    }

    [Fact]
    public async Task a_failed_resync_is_recorded_so_it_is_not_retried_every_pass()
    {
        await using var db = CreateDbContext();
        var song = AddSong(db, 1, LyricsSyncStatus.Suspect, probeAttempts: 1);
        await db.SaveChangesAsync();

        var transcriber = new FakeTranscriber { Throw = new InvalidOperationException("ffmpeg exploded") };
        var service = NewService(db, transcriber, new LyricsTimingOptions());

        await service.RunResyncPassAsync(CancellationToken.None);

        Assert.Equal(TranscriptionStatus.Failed, song.TranscriptionStatus);
        Assert.False(await service.RunResyncPassAsync(CancellationToken.None));
        Assert.Single(transcriber.FullCalls);
    }

    // --- Helpers ---

    private SongMetadata AddSong(
        MusicHoarderDbContext db,
        int id,
        LyricsSyncStatus status,
        int probeAttempts = 0,
        PreferredLyricsSource preferred = PreferredLyricsSource.Lrclib,
        TranscriptionStatus transcription = TranscriptionStatus.NotRequested,
        string? plainLyrics = "line 0 words here\nline 1 words here",
        bool instrumental = false,
        Guid? owner = null,
        bool deleted = false,
        bool built = false,
        int? durationSeconds = 120)
    {
        var path = Path.Combine(_tmpDir.FullName, $"{id}.mp3");
        File.WriteAllBytes(path, [0x00]);
        var song = new SongMetadata
        {
            Id = id,
            OwnerUserId = owner ?? WellKnownUsers.OwnerId,
            SourcePath = path,
            FileName = Path.GetFileName(path),
            Extension = ".mp3",
            FileSizeBytes = 1,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Artist = "Artist",
            Title = $"Song {id}",
            DurationSeconds = durationSeconds,
            LyricsStatus = LyricsStatus.Fetched,
            SyncedLyrics = Lrc,
            PlainLyrics = plainLyrics,
            IsInstrumental = instrumental,
            LyricsSyncStatus = status,
            LyricsSyncProbeAttempts = probeAttempts,
            PreferredLyricsSource = preferred,
            TranscriptionStatus = transcription,
            LibraryBuildStatus = built ? LibraryBuildStatus.Done : LibraryBuildStatus.Pending,
            DestinationPath = null,
            DeletedAtUtc = deleted ? DateTime.UtcNow : null,
        };
        db.Songs.Add(song);
        return song;
    }

    private static LyricsTimingCheckService NewService(
        MusicHoarderDbContext db, FakeTranscriber transcriber, LyricsTimingOptions opts)
    {
        var monitor = new StaticOptionsMonitor<LyricsTimingOptions>(opts);
        var budget = TestBudget.Over(db, opts);
        var probe = new LyricsTimingProbe(transcriber, budget, monitor, NullLogger<LyricsTimingProbe>.Instance);
        return new LyricsTimingCheckService(
            new FixedScopeFactory(db),
            probe,
            transcriber,
            budget,
            monitor,
            NullLogger<LyricsTimingCheckService>.Instance);
    }

    private static MusicHoarderDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private sealed class FakeTranscriber : ILyricsTranscriptionService
    {
        public bool Aligned { get; init; } = true;
        public Exception? Throw { get; init; }
        public List<string> ClipCalls { get; } = [];
        public List<string> FullCalls { get; } = [];

        public bool IsConfigured => true;

        public Task<TranscriptionResult> TranscribeAsync(SongMetadata song, string audioFilePath, CancellationToken ct = default)
        {
            FullCalls.Add(audioFilePath);
            if (Throw is not null)
                throw Throw;
            return Task.FromResult(Aligned
                ? new TranscriptionResult(AlignedLrc, "line 0 words here\nline 1 words here", "whisper-test", AlignedToReference: true)
                : new TranscriptionResult("[00:12.00]something else", "something else", "whisper-test"));
        }

        // An instrumental window: the probe records an attempt with an inconclusive verdict, which is all the
        // probe-pass tests need to see which songs were listened to.
        public Task<IReadOnlyList<TimedWord>> TranscribeClipAsync(
            string audioFilePath, double startSeconds, double lengthSeconds, string? promptText = null, CancellationToken ct = default)
        {
            ClipCalls.Add(audioFilePath);
            return Task.FromResult<IReadOnlyList<TimedWord>>([]);
        }
    }
}

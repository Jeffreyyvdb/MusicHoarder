using MusicHoarder.Api.BackgroundWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Settings;
using MusicHoarder.Api.Tests.Persistence;

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

    private const string HeardLrc =
        "[00:12.00]these are the words the model heard\n[00:17.00]in a song nobody wrote the lyrics down for";

    // --- Probe pass: which songs get a window ---

    [Fact]
    public async Task a_sweep_with_nothing_to_do_reports_idle_and_keeps_running()
    {
        await using var db = CreateDbContext();
        var status = new BackgroundWorkStatus(TimeProvider.System);
        var service = NewService(db, new FakeTranscriber(), new LyricsTimingOptions(), status);

        await service.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (status.Get(BackgroundJobIds.Lyrics)?.State != BackgroundJobState.Idle && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        Assert.Equal(BackgroundJobState.Idle, status.Get(BackgroundJobIds.Lyrics)?.State);
        Assert.False(service.ExecuteTask!.IsFaulted);
        await service.StopAsync(CancellationToken.None);
    }

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

    [Fact]
    public async Task probe_pass_reaches_past_a_full_batch_of_songs_whose_audio_is_missing()
    {
        await using var db = CreateDbContext();
        await AssertProbeReachesPastMissingAudio(db);
    }

    [PostgresFact]
    public async Task probe_pass_reaches_past_a_full_batch_of_songs_whose_audio_is_missing_on_postgres()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var db = database.NewContext();
        await AssertProbeReachesPastMissingAudio(db);
    }

    private async Task AssertProbeReachesPastMissingAudio(MusicHoarderDbContext db)
    {
        var older = AddSong(db, 1, LyricsSyncStatus.Suspect);
        AddSong(db, 2, LyricsSyncStatus.Suspect, fileMissing: true);
        AddSong(db, 3, LyricsSyncStatus.Suspect, fileMissing: true);
        await db.SaveChangesAsync();

        var transcriber = new FakeTranscriber();
        var service = NewService(db, transcriber, new LyricsTimingOptions { SweepBatchSize = 2 });

        Assert.True(await service.RunProbePassAsync(CancellationToken.None));
        Assert.Equal([older.SourcePath!], transcriber.ClipCalls);
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
    public async Task resync_pass_reaches_an_older_song_past_newer_ones_whose_audio_is_missing()
    {
        await using var db = CreateDbContext();
        await AssertResyncReachesPastMissingAudio(db);
    }

    [PostgresFact]
    public async Task resync_pass_reaches_an_older_song_past_newer_ones_whose_audio_is_missing_on_postgres()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var db = database.NewContext();
        await AssertResyncReachesPastMissingAudio(db);
    }

    private async Task AssertResyncReachesPastMissingAudio(MusicHoarderDbContext db)
    {
        var older = AddSong(db, 1, LyricsSyncStatus.Suspect, probeAttempts: 1);
        var missing = Enumerable.Range(2, 3)
            .Select(id => AddSong(db, id, LyricsSyncStatus.Suspect, probeAttempts: 1, fileMissing: true))
            .ToList();
        await db.SaveChangesAsync();

        var transcriber = new FakeTranscriber();
        var service = NewService(db, transcriber, new LyricsTimingOptions());

        Assert.True(await service.RunResyncPassAsync(CancellationToken.None));

        Assert.Equal([older.SourcePath!], transcriber.FullCalls);
        Assert.Equal(TranscriptionStatus.Completed, older.TranscriptionStatus);
        Assert.All(missing, s => Assert.Equal(TranscriptionStatus.NotRequested, s.TranscriptionStatus));
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

    // --- Probe share ---

    [Fact]
    public async Task probe_pass_stops_at_its_daily_share_of_the_audio()
    {
        await using var db = CreateDbContext();
        for (var id = 1; id <= 5; id++)
            AddSong(db, id, LyricsSyncStatus.Suspect);
        await db.SaveChangesAsync();

        var transcriber = new FakeTranscriber();
        var service = NewService(db, transcriber, new LyricsTimingOptions { ProbeAudioSecondsPerDay = 60, ProbeWindowSeconds = 30 });

        await service.RunProbePassAsync(CancellationToken.None);
        Assert.False(await service.RunProbePassAsync(CancellationToken.None));

        Assert.Equal(2, transcriber.ClipCalls.Count);
    }

    // --- AI sync pass ---

    [Fact]
    public async Task ai_sync_pass_works_inwards_from_the_newest_and_the_oldest_songs()
    {
        await using var db = CreateDbContext();
        for (var id = 1; id <= 6; id++)
            AddUnsynced(db, id, acquiredDaysAgo: 10 - id);
        await db.SaveChangesAsync();

        var transcriber = new FakeTranscriber();
        var service = NewService(db, transcriber, new LyricsTimingOptions());

        Assert.True(await service.RunAutoSyncPassAsync(CancellationToken.None));
        Assert.True(await service.RunAutoSyncPassAsync(CancellationToken.None));
        Assert.False(await service.RunAutoSyncPassAsync(CancellationToken.None));

        Assert.Equal([PathOf(6), PathOf(1), PathOf(5), PathOf(2), PathOf(4), PathOf(3)], transcriber.FullCalls);
    }

    [Fact]
    public async Task ai_sync_pass_times_official_words_and_shows_them()
    {
        await using var db = CreateDbContext();
        var song = AddUnsynced(db, 1, built: true);
        await db.SaveChangesAsync();

        await NewService(db, new FakeTranscriber(), new LyricsTimingOptions()).RunAutoSyncPassAsync(CancellationToken.None);

        Assert.Equal(AlignedLrc, song.DisplaySyncedLyrics);
        Assert.Equal(LyricsProvenance.AiEnhanced, song.LyricsProvenance);
        Assert.Equal(LibraryBuildStatus.Pending, song.LibraryBuildStatus);
        Assert.Equal(1, await db.TranscriptionAudioSpends.CountAsync(e => e.Purpose == TranscriptionPurpose.AutoSync));
    }

    [Fact]
    public async Task ai_sync_pass_shows_the_ai_words_for_a_song_with_no_official_lyrics()
    {
        await using var db = CreateDbContext();
        var song = AddUnsynced(db, 1, plainLyrics: null, lyricsStatus: LyricsStatus.NotFound);
        await db.SaveChangesAsync();

        var transcriber = new FakeTranscriber { Aligned = false };
        await NewService(db, transcriber, new LyricsTimingOptions()).RunAutoSyncPassAsync(CancellationToken.None);

        Assert.Equal(HeardLrc, song.DisplaySyncedLyrics);
        Assert.Equal(LyricsProvenance.AiGenerated, song.LyricsProvenance);
    }

    [Fact]
    public async Task ai_sync_pass_marks_a_song_nobody_sings_in_instrumental_and_does_not_try_again()
    {
        await using var db = CreateDbContext();
        var song = AddUnsynced(db, 1, plainLyrics: null, lyricsStatus: LyricsStatus.NotFound);
        await db.SaveChangesAsync();

        var transcriber = new FakeTranscriber { Aligned = false, Heard = "[00:12.00]Thank you." };
        var service = NewService(db, transcriber, new LyricsTimingOptions());

        await service.RunAutoSyncPassAsync(CancellationToken.None);

        Assert.True(song.IsInstrumental);
        Assert.Equal(TranscriptionStatus.Completed, song.TranscriptionStatus);
        Assert.Null(song.DisplaySyncedLyrics);
        Assert.Null(song.DisplayPlainLyrics);
        Assert.True(song.IsLyricsRecheckCandidate);
        Assert.False(await service.RunAutoSyncPassAsync(CancellationToken.None));
        Assert.Single(transcriber.FullCalls);
    }

    [Fact]
    public async Task ai_sync_pass_syncs_a_song_it_heard_no_singing_in_once_a_recheck_finds_lyrics()
    {
        await using var db = CreateDbContext();
        var song = AddUnsynced(db, 1, plainLyrics: null, lyricsStatus: LyricsStatus.NotFound);
        await db.SaveChangesAsync();

        await NewService(db, new FakeTranscriber { Aligned = false, Heard = "[00:12.00]Thank you." }, new LyricsTimingOptions())
            .RunAutoSyncPassAsync(CancellationToken.None);
        Assert.True(song.TryApplyLyricsUpgrade(null, "line 0 words here\nline 1 words here", instrumental: false));
        await db.SaveChangesAsync();

        Assert.False(song.IsInstrumental);
        Assert.True(await NewService(db, new FakeTranscriber(), new LyricsTimingOptions()).RunAutoSyncPassAsync(CancellationToken.None));
        Assert.Equal(AlignedLrc, song.DisplaySyncedLyrics);
    }

    [Fact]
    public async Task ai_sync_pass_hears_singing_in_a_language_written_without_spaces()
    {
        await using var db = CreateDbContext();
        var song = AddUnsynced(db, 1, plainLyrics: null, lyricsStatus: LyricsStatus.NotFound);
        await db.SaveChangesAsync();

        var japanese = string.Join('\n', Enumerable.Range(0, 6).Select(i => $"[00:{10 + (i * 5):00}.00]君のことが好きだよ"));
        await NewService(db, new FakeTranscriber { Aligned = false, Heard = japanese }, new LyricsTimingOptions())
            .RunAutoSyncPassAsync(CancellationToken.None);

        Assert.False(song.IsInstrumental);
        Assert.Equal(japanese, song.DisplaySyncedLyrics);
    }

    [Fact]
    public async Task ai_sync_pass_hears_singing_in_a_transcript_that_came_back_without_timestamps()
    {
        await using var db = CreateDbContext();
        var song = AddUnsynced(db, 1, plainLyrics: null, lyricsStatus: LyricsStatus.NotFound);
        await db.SaveChangesAsync();

        var transcriber = new FakeTranscriber { Aligned = false, Heard = null, HeardPlain = "these are the words the model heard in a song nobody wrote the lyrics down for" };
        await NewService(db, transcriber, new LyricsTimingOptions()).RunAutoSyncPassAsync(CancellationToken.None);

        Assert.False(song.IsInstrumental);
        Assert.Equal(TranscriptionStatus.Completed, song.TranscriptionStatus);
    }

    [Fact]
    public async Task ai_sync_pass_drops_its_result_when_the_song_gained_an_lrc_while_it_was_transcribed()
    {
        await using var db = CreateDbContext();
        var song = AddUnsynced(db, 1, plainLyrics: null, lyricsStatus: LyricsStatus.NotFound);
        await db.SaveChangesAsync();

        var transcriber = new FakeTranscriber
        {
            Aligned = false,
            During = transcribing =>
            {
                transcribing.TryApplyLyricsUpgrade(Lrc, "line 0 words here", instrumental: false);
                db.SaveChanges();
            },
        };
        Assert.True(await NewService(db, transcriber, new LyricsTimingOptions()).RunAutoSyncPassAsync(CancellationToken.None));

        Assert.Equal(TranscriptionStatus.NotRequested, song.TranscriptionStatus);
        Assert.Equal(Lrc, song.DisplaySyncedLyrics);
    }

    [Fact]
    public async Task ai_sync_pass_drops_a_guess_made_before_official_words_arrived_and_aligns_them_next()
    {
        await using var db = CreateDbContext();
        var song = AddUnsynced(db, 1, plainLyrics: null, lyricsStatus: LyricsStatus.NotFound);
        await db.SaveChangesAsync();

        var guessing = new FakeTranscriber
        {
            Aligned = false,
            During = transcribing =>
            {
                transcribing.TryApplyLyricsUpgrade(null, "line 0 words here\nline 1 words here", instrumental: false);
                db.SaveChanges();
            },
        };
        await NewService(db, guessing, new LyricsTimingOptions()).RunAutoSyncPassAsync(CancellationToken.None);
        Assert.Equal(TranscriptionStatus.NotRequested, song.TranscriptionStatus);

        await NewService(db, new FakeTranscriber(), new LyricsTimingOptions()).RunAutoSyncPassAsync(CancellationToken.None);
        Assert.Equal(AlignedLrc, song.DisplaySyncedLyrics);
        Assert.Equal(LyricsProvenance.AiEnhanced, song.LyricsProvenance);
    }

    [Fact]
    public void a_transcription_aligned_to_words_the_song_did_not_hold_adopts_them_as_its_lyrics()
    {
        var song = new SongMetadata
        {
            SourcePath = "/x.mp3",
            FileName = "x.mp3",
            Extension = ".mp3",
            FileSizeBytes = 1,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            LyricsStatus = LyricsStatus.NotFound,
        };

        LyricsResync.Apply(
            song, new TranscriptionResult(AlignedLrc, "line 0 words here", "whisper-test", AlignedToReference: true), wordsStampUtc: null);

        Assert.Equal("line 0 words here", song.PlainLyrics);
        Assert.Equal(LyricsStatus.Fetched, song.LyricsStatus);
        Assert.Equal(PreferredLyricsSource.Transcribed, song.PreferredLyricsSource);
        Assert.Equal(song.LyricsTextChangedAtUtc, song.TranscriptionWordsStampUtc);
        // History dates the words it found by the lookup, so it must be the lookup just made.
        Assert.True(song.LyricsLastAttemptedAtUtc >= DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void words_the_transcriber_found_keep_their_lrclib_entry_so_a_correction_of_it_is_taken()
    {
        var song = new SongMetadata
        {
            SourcePath = "/x.mp3",
            FileName = "x.mp3",
            Extension = ".mp3",
            FileSizeBytes = 1,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            LyricsStatus = LyricsStatus.NotFound,
        };

        LyricsResync.Apply(
            song,
            new TranscriptionResult(AlignedLrc, "line 0 words here", "whisper-test", AlignedToReference: true, ReferenceLrclibId: 42),
            wordsStampUtc: null);

        Assert.Equal("42", song.LrclibId);
        Assert.True(song.TryApplyLyricsUpgrade(null, "line 0 corrected words", instrumental: false, lrclibId: 42));
    }

    [Fact]
    public async Task ai_sync_pass_keeps_the_words_the_transcriber_found_so_a_recheck_does_not_sync_them_again()
    {
        await using var db = CreateDbContext();
        var song = AddUnsynced(db, 1, plainLyrics: null, lyricsStatus: LyricsStatus.NotFound);
        await db.SaveChangesAsync();

        var transcriber = new FakeTranscriber();
        var service = NewService(db, transcriber, new LyricsTimingOptions());
        await service.RunAutoSyncPassAsync(CancellationToken.None);

        Assert.Equal("line 0 words here\nline 1 words here", song.PlainLyrics);
        Assert.Equal(AlignedLrc, song.DisplaySyncedLyrics);
        Assert.False(song.TryApplyLyricsUpgrade(null, "line 0 words here\nline 1 words here", instrumental: false));
        Assert.False(await service.RunAutoSyncPassAsync(CancellationToken.None));
        Assert.Single(transcriber.FullCalls);
    }

    [Fact]
    public async Task a_transcription_that_hears_words_lifts_the_ai_instrumental_mark_but_not_lrclibs()
    {
        await using var db = CreateDbContext();
        var heardNothing = AddUnsynced(db, 1, plainLyrics: null, lyricsStatus: LyricsStatus.NotFound);
        var lrclibSays = AddUnsynced(db, 2, plainLyrics: null, lyricsStatus: LyricsStatus.Instrumental, instrumental: true);
        heardNothing.ApplyHeardNoSinging("whisper-test");

        heardNothing.ApplyTranscriptionResult(HeardLrc, null, "whisper-test");
        lrclibSays.ApplyTranscriptionResult(HeardLrc, null, "whisper-test");

        Assert.False(heardNothing.IsInstrumental);
        Assert.True(lrclibSays.IsInstrumental);
    }

    [Fact]
    public async Task ai_sync_pass_never_marks_a_song_with_official_words_instrumental()
    {
        await using var db = CreateDbContext();
        var song = AddUnsynced(db, 1);
        await db.SaveChangesAsync();

        await NewService(db, new FakeTranscriber { Aligned = false, Heard = "[00:12.00]Thank you." }, new LyricsTimingOptions())
            .RunAutoSyncPassAsync(CancellationToken.None);

        Assert.False(song.IsInstrumental);
        Assert.Equal("line 0 words here\nline 1 words here", song.DisplayPlainLyrics);
    }

    [Fact]
    public async Task ai_sync_pass_takes_only_songs_without_an_lrc_that_were_never_transcribed()
    {
        await using var db = CreateDbContext();
        await AssertAutoSyncTakesOnlyDueSongs(db);
    }

    [PostgresFact]
    public async Task ai_sync_pass_takes_only_songs_without_an_lrc_that_were_never_transcribed_on_postgres()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var db = database.NewContext();
        await AssertAutoSyncTakesOnlyDueSongs(db);
    }

    [PostgresFact]
    public async Task the_song_row_lock_holds_off_another_writer_until_the_result_is_saved()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var db = database.NewContext();
        AddUnsynced(db, 1);
        await db.SaveChangesAsync();

        await using var recheck = database.NewContext();
        await recheck.Database.OpenConnectionAsync();
        await recheck.Database.ExecuteSqlRawAsync("SET lock_timeout = '300ms'");

        var blocked = await db.WithSongLockedAsync(1, async () =>
        {
            try
            {
                await recheck.Database.ExecuteSqlRawAsync("UPDATE \"Songs\" SET \"PlainLyrics\" = 'new words' WHERE \"Id\" = 1");
                return false;
            }
            catch (Npgsql.PostgresException ex) when (ex.SqlState == "55P03")
            {
                return true;
            }
        }, CancellationToken.None);

        Assert.True(blocked);
        Assert.Equal(1, await recheck.Database.ExecuteSqlRawAsync("UPDATE \"Songs\" SET \"PlainLyrics\" = 'new words' WHERE \"Id\" = 1"));
    }

    private async Task AssertAutoSyncTakesOnlyDueSongs(MusicHoarderDbContext db)
    {
        AddUnsynced(db, 1);
        AddUnsynced(db, 2, plainLyrics: null, lyricsStatus: LyricsStatus.NotFound);
        AddUnsynced(db, 3, plainLyrics: null, lyricsStatus: LyricsStatus.NotFetched, title: null);
        AddSong(db, 4, LyricsSyncStatus.Ok);
        AddUnsynced(db, 5, transcription: TranscriptionStatus.Completed);
        AddUnsynced(db, 6, transcription: TranscriptionStatus.Failed);
        AddUnsynced(db, 7, plainLyrics: null, lyricsStatus: LyricsStatus.Instrumental, instrumental: true);
        AddUnsynced(db, 8, plainLyrics: null, lyricsStatus: LyricsStatus.NotFetched);
        AddUnsynced(db, 9, owner: WellKnownUsers.DemoId);
        AddUnsynced(db, 10, deleted: true);
        AddUnsynced(db, 11, enrichment: EnrichmentStatus.Pending);
        AddUnsynced(db, 12, fileMissing: true);
        AddUnsynced(db, 13).IsDuplicate = true;
        AddUnsynced(db, 14, plainLyrics: null, lyricsStatus: LyricsStatus.Failed);
        AddUnsynced(db, 15, durationSeconds: null);
        var msOnly = AddUnsynced(db, 16, durationSeconds: null);
        msOnly.DurationMs = 200_000;
        // The AI's mark with official words beside it is a leftover; the words are what count.
        AddUnsynced(db, 17, instrumental: true);
        await db.SaveChangesAsync();

        var transcriber = new FakeTranscriber();
        var service = NewService(db, transcriber, new LyricsTimingOptions());
        while (await service.RunAutoSyncPassAsync(CancellationToken.None))
        {
        }

        Assert.Equal(
            new[] { 1, 2, 3, 14, 16, 17 }.Select(PathOf).Order(StringComparer.Ordinal).ToList(),
            transcriber.FullCalls.Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public async Task ai_sync_pass_retimes_ai_words_once_a_recheck_brings_official_ones()
    {
        await using var db = CreateDbContext();
        var song = AddUnsynced(db, 1, plainLyrics: null, lyricsStatus: LyricsStatus.NotFound);
        await db.SaveChangesAsync();

        var guessing = NewService(db, new FakeTranscriber { Aligned = false }, new LyricsTimingOptions());
        await guessing.RunAutoSyncPassAsync(CancellationToken.None);
        Assert.Equal(LyricsProvenance.AiGenerated, song.LyricsProvenance);
        Assert.False(await guessing.RunAutoSyncPassAsync(CancellationToken.None));

        Assert.True(song.TryApplyLyricsUpgrade(null, "line 0 words here\nline 1 words here", instrumental: false));
        await db.SaveChangesAsync();

        var aligning = new FakeTranscriber();
        Assert.True(await NewService(db, aligning, new LyricsTimingOptions()).RunAutoSyncPassAsync(CancellationToken.None));

        Assert.Single(aligning.FullCalls);
        Assert.Equal(AlignedLrc, song.DisplaySyncedLyrics);
        Assert.Equal(LyricsProvenance.AiEnhanced, song.LyricsProvenance);
    }

    [Fact]
    public async Task a_result_aligned_to_words_replaced_meanwhile_keeps_the_new_words_and_is_timed_again()
    {
        await using var db = CreateDbContext();
        var song = AddUnsynced(db, 1, plainLyrics: null, lyricsStatus: LyricsStatus.NotFound);
        await db.SaveChangesAsync();
        var stampWhenStarted = song.LyricsTextChangedAtUtc;

        // A lyrics re-check saves official words while the audio is out.
        Assert.True(song.TryApplyLyricsUpgrade(null, "line 0 words here\nline 1 words here", instrumental: false));
        LyricsResync.Apply(
            song,
            new TranscriptionResult(AlignedLrc, "older words the transcriber found", "whisper-test", AlignedToReference: true),
            stampWhenStarted);
        await db.SaveChangesAsync();

        Assert.Equal("line 0 words here\nline 1 words here", song.PlainLyrics);
        Assert.Equal(PreferredLyricsSource.Lrclib, song.PreferredLyricsSource);

        var transcriber = new FakeTranscriber();
        Assert.True(await NewService(db, transcriber, new LyricsTimingOptions()).RunAutoSyncPassAsync(CancellationToken.None));
        Assert.Single(transcriber.FullCalls);
        Assert.Equal(PreferredLyricsSource.Transcribed, song.PreferredLyricsSource);
    }

    [Fact]
    public async Task a_failed_alignment_to_new_official_words_shows_and_tags_the_words_not_the_ai_guess()
    {
        await using var db = CreateDbContext();
        var song = AddUnsynced(db, 1);
        await db.SaveChangesAsync();
        await NewService(db, new FakeTranscriber(), new LyricsTimingOptions()).RunAutoSyncPassAsync(CancellationToken.None);
        Assert.Equal(PreferredLyricsSource.Transcribed, song.PreferredLyricsSource);

        song.SetOfficialLyricsText(null, "corrected words here\nsecond corrected line");
        await db.SaveChangesAsync();
        await NewService(db, new FakeTranscriber { Aligned = false }, new LyricsTimingOptions())
            .RunAutoSyncPassAsync(CancellationToken.None);

        Assert.Equal(PreferredLyricsSource.Lrclib, song.PreferredLyricsSource);
        Assert.Equal("corrected words here\nsecond corrected line", song.DisplayPlainLyrics);
        Assert.Equal("corrected words here\nsecond corrected line", song.EffectivePlainLyrics);
    }

    [Fact]
    public async Task a_new_transcription_that_cannot_time_the_words_gives_way_to_them_and_retags_the_file()
    {
        await using var db = CreateDbContext();
        var song = AddUnsynced(db, 1);
        await db.SaveChangesAsync();
        await NewService(db, new FakeTranscriber(), new LyricsTimingOptions()).RunAutoSyncPassAsync(CancellationToken.None);
        song.LibraryBuildStatus = LibraryBuildStatus.Done;

        LyricsResync.Apply(
            song, new TranscriptionResult(HeardLrc, "what the ai heard", "whisper-test"), song.LyricsTextChangedAtUtc);

        Assert.Equal(PreferredLyricsSource.Lrclib, song.PreferredLyricsSource);
        Assert.Equal("line 0 words here\nline 1 words here", song.EffectivePlainLyrics);
        Assert.Equal(LibraryBuildStatus.Pending, song.LibraryBuildStatus);
    }

    [Fact]
    public async Task a_song_left_with_the_ai_mark_beside_official_words_is_synced_and_the_mark_lifted()
    {
        await using var db = CreateDbContext();
        var song = AddUnsynced(db, 1, plainLyrics: null, lyricsStatus: LyricsStatus.NotFound);
        song.ApplyHeardNoSinging("whisper-test");
        song.ApplyLyricsResult(null, "line 0 words here\nline 1 words here", instrumental: false);
        // What a re-check saved before the sweep's mark landed on top of it would leave.
        song.IsInstrumental = true;
        await db.SaveChangesAsync();

        Assert.False(song.ShowsAsInstrumental);
        Assert.True(await NewService(db, new FakeTranscriber(), new LyricsTimingOptions())
            .RunAutoSyncPassAsync(CancellationToken.None));

        Assert.False(song.IsInstrumental);
        Assert.Equal(AlignedLrc, song.DisplaySyncedLyrics);
    }

    [Fact]
    public void an_lrc_arriving_with_the_same_words_still_outdates_an_alignment_made_before_it()
    {
        var song = new SongMetadata
        {
            SourcePath = "/x.mp3",
            FileName = "x.mp3",
            Extension = ".mp3",
            FileSizeBytes = 1,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
        };
        song.ApplyLyricsResult(null, "line 0 words here", instrumental: false);
        var stampWhenStarted = song.LyricsTextChangedAtUtc;

        song.SetOfficialLyricsText("[00:01.00]line 0 words here", "line 0 words here");
        LyricsResync.Apply(
            song, new TranscriptionResult(AlignedLrc, "line 0 words here", "whisper-test", AlignedToReference: true), stampWhenStarted);

        Assert.NotEqual(stampWhenStarted, song.LyricsTextChangedAtUtc);
        Assert.Equal("[00:01.00]line 0 words here", song.DisplaySyncedLyrics);
    }

    [Fact]
    public async Task ai_sync_pass_leaves_out_a_song_a_millisecond_too_long_for_the_ceiling()
    {
        await using var db = CreateDbContext();
        var tooLong = AddUnsynced(db, 1, durationSeconds: null, acquiredDaysAgo: 0);
        tooLong.DurationMs = 60_001;
        var fits = AddUnsynced(db, 2, durationSeconds: 30, acquiredDaysAgo: 5);
        await db.SaveChangesAsync();

        var transcriber = new FakeTranscriber();
        await NewService(db, transcriber, new LyricsTimingOptions { AudioSecondsPerHour = 60, AudioSecondsPerDay = 60 })
            .RunAutoSyncPassAsync(CancellationToken.None);

        Assert.Equal([PathOf(2)], transcriber.FullCalls);
        Assert.Equal(TranscriptionStatus.Completed, fits.TranscriptionStatus);
    }

    [Fact]
    public async Task resync_pass_does_not_overwrite_a_resync_the_button_stored_meanwhile()
    {
        await using var db = CreateDbContext();
        var song = AddSong(db, 1, LyricsSyncStatus.Suspect, probeAttempts: 1);
        await db.SaveChangesAsync();

        var options = (DbContextOptions<MusicHoarderDbContext>)db.GetService<IDbContextOptions>();
        var transcriber = new FakeTranscriber
        {
            Aligned = false,
            During = _ =>
            {
                using var button = new MusicHoarderDbContext(options);
                var row = button.Songs.IgnoreQueryFilters().Single(s => s.Id == 1);
                row.ApplyTranscriptionResult(AlignedLrc, "line 0 words here", "whisper-button", alignedToReference: true);
                row.PreferredLyricsSource = PreferredLyricsSource.Transcribed;
                button.SaveChanges();
            },
        };

        Assert.False(await NewService(db, transcriber, new LyricsTimingOptions()).RunResyncPassAsync(CancellationToken.None));

        Assert.Equal("whisper-button", song.TranscriptionModel);
        Assert.Equal(PreferredLyricsSource.Transcribed, song.PreferredLyricsSource);
    }

    [Fact]
    public async Task ai_sync_pass_follows_the_choice_stored_in_settings_over_the_config()
    {
        await using var db = CreateDbContext();
        AddUnsynced(db, 1);
        db.RuntimeSettings.Add(new RuntimeSettings { LyricsAutoSyncEnabled = false });
        await db.SaveChangesAsync();

        var transcriber = new FakeTranscriber();
        Assert.False(await NewService(db, transcriber, new LyricsTimingOptions { EnableAutoSync = true })
            .RunAutoSyncPassAsync(CancellationToken.None));
        Assert.Empty(transcriber.FullCalls);
    }

    [Fact]
    public async Task ai_sync_pass_does_nothing_when_switched_off()
    {
        await using var db = CreateDbContext();
        AddUnsynced(db, 1);
        await db.SaveChangesAsync();

        var transcriber = new FakeTranscriber();
        Assert.False(await NewService(db, transcriber, new LyricsTimingOptions { EnableAutoSync = false })
            .RunAutoSyncPassAsync(CancellationToken.None));
        Assert.Empty(transcriber.FullCalls);
    }

    [Fact]
    public async Task ai_sync_pass_waits_when_the_background_audio_is_spent_and_records_nothing()
    {
        await using var db = CreateDbContext();
        var first = AddUnsynced(db, 1, durationSeconds: 200);
        var second = AddUnsynced(db, 2, durationSeconds: 200);
        await db.SaveChangesAsync();

        var transcriber = new FakeTranscriber();
        var service = NewService(db, transcriber, new LyricsTimingOptions { AudioSecondsPerHour = 300, AudioSecondsPerDay = 300 });

        await service.RunAutoSyncPassAsync(CancellationToken.None);

        Assert.Single(transcriber.FullCalls);
        Assert.Equal(
            [TranscriptionStatus.NotRequested, TranscriptionStatus.Completed],
            new[] { first, second }.Select(s => s.TranscriptionStatus).Order().ToArray());
        Assert.False(await service.RunAutoSyncPassAsync(CancellationToken.None));
    }

    // --- Helpers ---

    private string PathOf(int id) => Path.Combine(_tmpDir.FullName, $"{id}.mp3");

    private SongMetadata AddUnsynced(
        MusicHoarderDbContext db,
        int id,
        string? plainLyrics = "line 0 words here\nline 1 words here",
        LyricsStatus lyricsStatus = LyricsStatus.Fetched,
        TranscriptionStatus transcription = TranscriptionStatus.NotRequested,
        string? title = "Song",
        bool instrumental = false,
        Guid? owner = null,
        bool deleted = false,
        bool built = false,
        int? durationSeconds = 120,
        bool fileMissing = false,
        double acquiredDaysAgo = 1,
        EnrichmentStatus enrichment = EnrichmentStatus.Matched)
    {
        var song = AddSong(
            db, id, LyricsSyncStatus.NotChecked, transcription: transcription, plainLyrics: plainLyrics,
            instrumental: instrumental, owner: owner, deleted: deleted, built: built,
            durationSeconds: durationSeconds, fileMissing: fileMissing);
        song.SyncedLyrics = null;
        song.LyricsStatus = lyricsStatus;
        song.Title = title;
        song.EnrichmentStatus = enrichment;
        song.AcquiredAtUtc = DateTime.UtcNow.AddDays(-acquiredDaysAgo);
        return song;
    }

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
        int? durationSeconds = 120,
        bool fileMissing = false)
    {
        var path = Path.Combine(_tmpDir.FullName, $"{id}.mp3");
        if (!fileMissing)
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

    /// <summary>The sweep as production wires it. AI sync is on unless the test says otherwise.</summary>
    private static LyricsTimingCheckService NewService(
        MusicHoarderDbContext db, FakeTranscriber transcriber, LyricsTimingOptions opts, BackgroundWorkStatus? status = null)
    {
        opts.EnableAutoSync ??= true;
        var monitor = new StaticOptionsMonitor<LyricsTimingOptions>(opts);
        var budget = TestBudget.Over(db, opts);
        var probe = new LyricsTimingProbe(transcriber, budget, monitor, NullLogger<LyricsTimingProbe>.Instance);
        var runtimeSettings = new RuntimeSettingsService(
            new FixedScopeFactory(db),
            new StaticOptionsMonitor<MusicEnricherOptions>(new MusicEnricherOptions
            {
                SourceDirectory = "/src",
                DestinationDirectory = "/dst",
            }),
            new StaticOptionsMonitor<QualityGradingOptions>(new QualityGradingOptions()),
            monitor);
        return new LyricsTimingCheckService(
            new FixedScopeFactory(db),
            probe,
            transcriber,
            budget,
            runtimeSettings,
            monitor,
            NullLogger<LyricsTimingCheckService>.Instance,
            status);
    }

    private static MusicHoarderDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private sealed class FakeTranscriber : ILyricsTranscriptionService
    {
        public bool Aligned { get; init; } = true;
        public string? Heard { get; init; } = HeardLrc;
        public string? HeardPlain { get; init; }
        public Action<SongMetadata>? During { get; init; }
        public Exception? Throw { get; init; }
        public List<string> ClipCalls { get; } = [];
        public List<string> FullCalls { get; } = [];

        public bool IsConfigured => true;

        public Task<TranscriptionResult> TranscribeAsync(SongMetadata song, string audioFilePath, CancellationToken ct = default, Func<CancellationToken, Task>? beforeRetry = null)
        {
            FullCalls.Add(audioFilePath);
            During?.Invoke(song);
            if (Throw is not null)
                throw Throw;
            return Task.FromResult(Aligned
                ? new TranscriptionResult(AlignedLrc, "line 0 words here\nline 1 words here", "whisper-test", AlignedToReference: true)
                : new TranscriptionResult(Heard, HeardPlain, "whisper-test"));
        }

        // An instrumental window: the probe records an attempt with an inconclusive verdict, which is all the
        // probe-pass tests need to see which songs were listened to.
        public Task<IReadOnlyList<TimedWord>> TranscribeClipAsync(
            string audioFilePath, double startSeconds, double lengthSeconds, string? promptText = null, CancellationToken ct = default,
            Func<CancellationToken, Task>? beforeRetry = null)
        {
            ClipCalls.Add(audioFilePath);
            return Task.FromResult<IReadOnlyList<TimedWord>>([]);
        }
    }
}

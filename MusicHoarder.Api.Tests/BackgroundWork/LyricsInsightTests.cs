using MusicHoarder.Api.Auth;
using MusicHoarder.Api.BackgroundWork;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.BackgroundWork;

public class LyricsInsightTests
{
    private const string Lrc = "[00:01.00] words";

    private static void Add(BackgroundWorkHarness h, int n, Action<SongMetadata>? configure = null)
        => h.Seed(db => db.Songs.Add(BackgroundWorkTestData.NewSong(n, configure)));

    [Fact]
    public async Task Every_live_song_is_counted_once_by_where_its_lyrics_come_from()
    {
        using var h = new BackgroundWorkHarness();
        Add(h, 1, s => { s.LyricsStatus = LyricsStatus.Fetched; s.SyncedLyrics = Lrc; s.PlainLyrics = "words"; });
        Add(h, 2, s =>
        {
            s.LyricsStatus = LyricsStatus.Fetched; s.PlainLyrics = "words";
            s.TranscribedSyncedLyrics = Lrc; s.TranscribedPlainLyrics = "words";
            s.PreferredLyricsSource = PreferredLyricsSource.Transcribed;
            s.TranscriptionStatus = TranscriptionStatus.Completed; s.TranscriptionAlignedToReference = true;
        });
        Add(h, 3, s =>
        {
            s.LyricsStatus = LyricsStatus.NotFound; s.TranscribedSyncedLyrics = Lrc; s.TranscribedPlainLyrics = "words";
            s.TranscriptionStatus = TranscriptionStatus.Completed;
        });
        Add(h, 4, s => { s.LyricsStatus = LyricsStatus.Fetched; s.PlainLyrics = "words"; });
        Add(h, 5, s => { s.LyricsStatus = LyricsStatus.Instrumental; s.IsInstrumental = true; });
        Add(h, 6, s =>
        {
            s.LyricsStatus = LyricsStatus.NotFound; s.IsInstrumental = true; s.TranscriptionStatus = TranscriptionStatus.Completed;
        });
        Add(h, 7, s => s.LyricsStatus = LyricsStatus.NotFound);
        Add(h, 8, s => s.LyricsStatus = LyricsStatus.Failed);
        Add(h, 9, s => s.LyricsStatus = LyricsStatus.NotFetched);
        Add(h, 10, s => { s.LyricsStatus = LyricsStatus.NotFetched; s.Title = ""; });
        // Not the lyrics sweep's business: deleted, duplicate, synthetic, and the demo tenant's.
        Add(h, 11, s => s.DeletedAtUtc = h.Now);
        Add(h, 12, s => s.IsDuplicate = true);
        Add(h, 13, s => s.IsSynthetic = true);
        Add(h, 14, s => s.OwnerUserId = WellKnownUsers.DemoId);

        var detail = await h.DescribeAsync(BackgroundJobIds.Lyrics);

        var breakdown = detail.Panel<BreakdownPanelDto>("Where every song's lyrics come from");
        Assert.Equal(10, breakdown.Total);
        Assert.Equal(
            ["lrclib-synced", "ai-timed-official", "ai-written", "plain-only", "instrumental-lrclib", "no-singing-ai",
                "not-found", "lookup-failed", "cant-look-up", "not-looked-up"],
            breakdown.Segments.Select(s => s.Key));
        Assert.All(breakdown.Segments, s => Assert.Equal(1, s.Count));

        // Synced: the LRC and both AI versions. Of: everything but the two with no words to time.
        Assert.Equal(3, detail.Summary.Headline.Value);
        Assert.Equal(8, detail.Summary.Headline.Of);
    }

    [Fact]
    public async Task The_timing_verdicts_tell_a_listened_to_ok_from_an_arithmetic_one()
    {
        using var h = new BackgroundWorkHarness();
        void Lrc_(int n, LyricsSyncStatus status, int attempts)
            => Add(h, n, s => { s.SyncedLyrics = Lrc; s.LyricsStatus = LyricsStatus.Fetched; s.LyricsSyncStatus = status; s.LyricsSyncProbeAttempts = attempts; });
        Lrc_(1, LyricsSyncStatus.NotChecked, 0);
        Lrc_(2, LyricsSyncStatus.Ok, 0);
        Lrc_(3, LyricsSyncStatus.Ok, 1);
        Lrc_(4, LyricsSyncStatus.Corrected, 1);
        Lrc_(5, LyricsSyncStatus.Suspect, 0);
        Lrc_(6, LyricsSyncStatus.Suspect, 1);
        Lrc_(7, LyricsSyncStatus.Unverifiable, 1);
        Add(h, 8, s => s.LyricsStatus = LyricsStatus.NotFound);

        var panel = (await h.DescribeAsync(BackgroundJobIds.Lyrics)).Panel<BreakdownPanelDto>("Timing verdicts");

        Assert.Equal(7, panel.Total);
        Assert.All(panel.Segments, s => Assert.Equal(1, s.Count));
        Assert.Equal(
            ["not-checked", "ok-free", "ok-probed", "corrected", "suspect", "suspect-probed", "unverifiable"],
            panel.Segments.Select(s => s.Key));
    }

    [Fact]
    public async Task What_each_pass_has_left_is_what_the_pass_would_pick()
    {
        using var h = new BackgroundWorkHarness();
        h.Lyrics.AudioSecondsPerHour = 1000;
        h.Lyrics.MaxProbeAttempts = 2;
        // AI sync: one fits, one is longer than the ceiling, one already has an LRC, one is not looked up yet.
        Add(h, 1, s => { s.LyricsStatus = LyricsStatus.NotFound; s.DurationSeconds = 300; });
        Add(h, 2, s => { s.LyricsStatus = LyricsStatus.NotFound; s.DurationSeconds = 1500; });
        Add(h, 3, s => { s.LyricsStatus = LyricsStatus.Fetched; s.SyncedLyrics = Lrc; s.LyricsSyncStatus = LyricsSyncStatus.Ok; s.LyricsSyncProbeAttempts = 1; });
        Add(h, 4, s => s.LyricsStatus = LyricsStatus.NotFetched);
        // Timing: a never-judged LRC, a flagged one, a given-up one, and an unconfirmed ok.
        Add(h, 5, s => { s.SyncedLyrics = Lrc; s.LyricsStatus = LyricsStatus.Fetched; });
        Add(h, 6, s => { s.SyncedLyrics = Lrc; s.LyricsStatus = LyricsStatus.Fetched; s.LyricsSyncStatus = LyricsSyncStatus.Suspect; });
        Add(h, 7, s => { s.SyncedLyrics = Lrc; s.LyricsStatus = LyricsStatus.Fetched; s.LyricsSyncStatus = LyricsSyncStatus.Suspect; s.LyricsSyncProbeAttempts = 2; });
        Add(h, 8, s => { s.SyncedLyrics = Lrc; s.LyricsStatus = LyricsStatus.Fetched; s.LyricsSyncStatus = LyricsSyncStatus.Ok; });

        var figures = (await h.DescribeAsync(BackgroundJobIds.Lyrics)).Panel<FiguresPanelDto>("What each pass has left");

        Assert.Equal(1, figures.Item("AI sync queue").Value);
        Assert.Equal(300, figures.Item("AI sync audio").Value);
        Assert.Equal(1, figures.Item("Too long for the audio ceiling").Value);
        Assert.Equal(1, figures.Item("Free timing check").Value);
        Assert.Equal(2, figures.Item("Timing probes").Value);
        Assert.Equal(60, figures.Item("Probe audio").Value);
        Assert.Equal(1, figures.Item("Probes given up").Value);
    }

    [Fact]
    public async Task The_ai_sync_map_runs_oldest_first_and_marks_how_far_each_end_has_reached()
    {
        using var h = new BackgroundWorkHarness();
        void Transcribed(SongMetadata s) { s.LyricsStatus = LyricsStatus.NotFound; s.TranscriptionStatus = TranscriptionStatus.Completed; s.TranscribedSyncedLyrics = Lrc; }
        void Due(SongMetadata s) => s.LyricsStatus = LyricsStatus.NotFound;
        Add(h, 10, s => { s.LyricsStatus = LyricsStatus.Fetched; s.SyncedLyrics = Lrc; s.LyricsSyncStatus = LyricsSyncStatus.Ok; });
        Add(h, 5, Transcribed);
        Add(h, 4, Transcribed);
        Add(h, 3, Due);
        Add(h, 2, Due);
        Add(h, 1, Transcribed);

        var map = (await h.DescribeAsync(BackgroundJobIds.Lyrics)).Panel<LibraryMapPanelDto>("Where AI sync has reached");

        Assert.Equal(
            [(0, 0, 1), (1, 0, 0), (1, 0, 0), (0, 1, 0), (0, 1, 0), (1, 0, 0)],
            map.Bins.Select(b => (b.Done, b.Waiting, b.Excluded)));
        Assert.Equal(h.Now.AddDays(-3), map.Markers.Single(m => m.Label.StartsWith("Oldest", StringComparison.Ordinal)).AtUtc);
        Assert.Equal(h.Now.AddDays(-2), map.Markers.Single(m => m.Label.StartsWith("Newest", StringComparison.Ordinal)).AtUtc);
    }

    [Fact]
    public async Task The_probe_map_marks_songs_a_probe_has_heard_and_the_ones_still_queued()
    {
        using var h = new BackgroundWorkHarness();
        void Lrc_(int n, LyricsSyncStatus status, int attempts, PreferredLyricsSource preferred = PreferredLyricsSource.Lrclib)
            => Add(h, n, s =>
            {
                s.SyncedLyrics = Lrc; s.LyricsStatus = LyricsStatus.Fetched; s.LyricsSyncStatus = status;
                s.LyricsSyncProbeAttempts = attempts; s.PreferredLyricsSource = preferred;
            });
        Lrc_(1, LyricsSyncStatus.Ok, 1);
        Lrc_(2, LyricsSyncStatus.Suspect, 0);
        Lrc_(3, LyricsSyncStatus.Ok, 0);
        Lrc_(4, LyricsSyncStatus.Ok, 0, PreferredLyricsSource.Transcribed);

        var map = (await h.DescribeAsync(BackgroundJobIds.Lyrics)).Panel<LibraryMapPanelDto>("Where the timing probes");

        Assert.Equal(
            [(1, 0, 0), (0, 1, 0), (0, 1, 0), (0, 0, 1)],
            map.Bins.Select(b => (b.Done, b.Waiting, b.Excluded)));
        Assert.Single(map.Markers);
    }

    [Fact]
    public async Task The_eta_is_the_ai_sync_audio_left_over_the_audio_it_has_been_sending_a_day()
    {
        using var h = new BackgroundWorkHarness();
        h.Settings.AutoSync = true;
        Add(h, 1, s => { s.LyricsStatus = LyricsStatus.NotFound; s.DurationSeconds = 600; });
        Add(h, 2, s => { s.LyricsStatus = LyricsStatus.NotFound; s.DurationSeconds = 600; });
        h.Seed(db => db.TranscriptionAudioSpends.AddRange(
            new TranscriptionAudioSpend { AtUtc = h.Now.AddDays(-2), AudioSeconds = 1800, Purpose = TranscriptionPurpose.AutoSync },
            new TranscriptionAudioSpend { AtUtc = h.Now.AddHours(-1), AudioSeconds = 1800, Purpose = TranscriptionPurpose.AutoSync },
            new TranscriptionAudioSpend { AtUtc = h.Now.AddHours(-1), AudioSeconds = 9999, Purpose = TranscriptionPurpose.ManualTranscribe }));

        var headline = (await h.SummarizeAsync(BackgroundJobIds.Lyrics)).Headline;

        // 3,600 s over the 2 days the ledger covers is 1,800 s a day; 1,200 s are left.
        Assert.Equal(h.Now.AddDays(1200.0 / 1800.0), headline.EtaUtc);
        Assert.Contains("rate of 30m a day", headline.EtaLabel);
    }

    [Fact]
    public async Task There_is_no_eta_while_ai_sync_is_off_or_has_sent_nothing()
    {
        using var h = new BackgroundWorkHarness();
        Add(h, 1, s => { s.LyricsStatus = LyricsStatus.NotFound; s.DurationSeconds = 600; });
        h.Seed(db => db.TranscriptionAudioSpends.Add(
            new TranscriptionAudioSpend { AtUtc = h.Now.AddHours(-1), AudioSeconds = 600, Purpose = TranscriptionPurpose.AutoSync }));

        Assert.Null((await h.SummarizeAsync(BackgroundJobIds.Lyrics)).Headline.EtaUtc);

        h.Settings.AutoSync = true;
        Assert.NotNull((await h.SummarizeAsync(BackgroundJobIds.Lyrics)).Headline.EtaUtc);

        using var quiet = new BackgroundWorkHarness();
        quiet.Settings.AutoSync = true;
        Add(quiet, 1, s => { s.LyricsStatus = LyricsStatus.NotFound; s.DurationSeconds = 600; });
        var headline = (await quiet.SummarizeAsync(BackgroundJobIds.Lyrics)).Headline;
        Assert.Null(headline.EtaUtc);
        Assert.Contains("no rate", headline.EtaLabel);
    }

    [Fact]
    public async Task The_daily_series_and_trend_count_each_song_on_the_day_of_its_last_transcription()
    {
        using var h = new BackgroundWorkHarness();
        Add(h, 1, s =>
        {
            s.TranscriptionStatus = TranscriptionStatus.Completed; s.TranscribedAtUtc = h.Now.AddHours(-1);
            s.TranscribedSyncedLyrics = Lrc; s.TranscriptionAlignedToReference = true;
        });
        Add(h, 2, s => { s.TranscriptionStatus = TranscriptionStatus.Failed; s.TranscribedAtUtc = h.Now; });
        Add(h, 3, s =>
        {
            s.TranscriptionStatus = TranscriptionStatus.Completed; s.TranscribedAtUtc = h.Now.AddDays(-1);
            s.TranscribedSyncedLyrics = Lrc;
        });

        var detail = await h.DescribeAsync(BackgroundJobIds.Lyrics);

        var series = detail.Panel<SeriesPanelDto>("What the sweep finished each day");
        Assert.Equal(30, series.Points.Count);
        Assert.Equal([1.0, 0, 0, 1, 0], series.Points[^1].Values);
        Assert.Equal([0.0, 1, 0, 0, 0], series.Points[^2].Values);
        Assert.Equal([1.0, 1], detail.Summary.Trend.TakeLast(2).Reverse());
    }

    [Fact]
    public async Task The_lyrics_job_carries_what_its_service_reported()
    {
        using var h = new BackgroundWorkHarness();
        h.Status.Waiting(BackgroundJobIds.Lyrics, "AI sync waits for the transcription audio allowance.", h.Now.AddHours(2));

        var summary = await h.SummarizeAsync(BackgroundJobIds.Lyrics);

        Assert.Equal(BackgroundJobState.Waiting, summary.State);
        Assert.Equal(h.Now.AddHours(2), summary.NextRunAtUtc);
        Assert.DoesNotContain(":", summary.StateLabel);
    }

    [Fact]
    public async Task An_idle_sweep_with_songs_queued_says_why_they_are_not_moving()
    {
        using var h = new BackgroundWorkHarness();
        Add(h, 1, s => { s.LyricsStatus = LyricsStatus.NotFound; s.DurationSeconds = 200; });
        h.Status.Idle(BackgroundJobIds.Lyrics, h.Now.AddMinutes(5));

        h.Transcriber.IsConfigured = false;
        Assert.Contains("no transcription provider", (await h.SummarizeAsync(BackgroundJobIds.Lyrics)).StateLabel);

        h.Transcriber.IsConfigured = true;
        Assert.Contains("AI sync is switched off", (await h.SummarizeAsync(BackgroundJobIds.Lyrics)).StateLabel);

        h.Settings.AutoSync = true;
        Assert.Equal("Resting between passes.", (await h.SummarizeAsync(BackgroundJobIds.Lyrics)).StateLabel);
    }
}

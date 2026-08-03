using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Persistence;

/// <summary>
/// LRCLIB is community-contributed and keeps growing, so a song's lyrics outcome is re-checked on a
/// backoff. These cover the two rules that make that safe: a re-check may only ever *improve* the
/// stored lyrics, and a resolved song must drop out of the schedule entirely.
/// </summary>
public class SongMetadataLyricsRecheckTests
{
    private static readonly DateTime Now = new(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);

    private static SongMetadata NewSong() => new()
    {
        SourcePath = "/src/a.flac",
        FileName = "a.flac",
        Extension = ".flac",
        FileSizeBytes = 1234L,
        LastModifiedUtc = Now,
        IndexedAtUtc = Now,
        Artist = "Artist",
        Title = "Title",
        EnrichmentStatus = EnrichmentStatus.Matched,
    };

    private static SongMetadata NotFoundSong()
    {
        var song = NewSong();
        song.MarkLyricsNotFound();
        return song;
    }

    private static SongMetadata PlainOnlySong()
    {
        var song = NewSong();
        song.ApplyLyricsResult(null, "a line", instrumental: false);
        return song;
    }

    // --- Candidacy ---

    [Fact]
    public void NotFoundAndFailedAndPlainOnly_AreRecheckCandidates()
    {
        Assert.True(NotFoundSong().IsLyricsRecheckCandidate);
        Assert.True(PlainOnlySong().IsLyricsRecheckCandidate);

        var failed = NewSong();
        failed.MarkLyricsFailed();
        Assert.True(failed.IsLyricsRecheckCandidate);
    }

    [Fact]
    public void SyncedAndInstrumentalAndNotFetched_AreNotRecheckCandidates()
    {
        var synced = NewSong();
        synced.ApplyLyricsResult("[00:01.00]a line", "a line", instrumental: false);
        Assert.False(synced.IsLyricsRecheckCandidate);

        var instrumental = NewSong();
        instrumental.ApplyLyricsResult(null, null, instrumental: true);
        Assert.False(instrumental.IsLyricsRecheckCandidate);

        // Owned by the backfill sweep, which fetches immediately rather than after a cooldown.
        Assert.False(NewSong().IsLyricsRecheckCandidate);
    }

    [Fact]
    public void UnmatchedOrUnnamedSongs_AreNotRecheckCandidates()
    {
        var pending = NotFoundSong();
        pending.EnrichmentStatus = EnrichmentStatus.Pending;
        Assert.False(pending.IsLyricsRecheckCandidate);

        var unnamed = NotFoundSong();
        unnamed.Title = null;
        Assert.False(unnamed.IsLyricsRecheckCandidate);
    }

    // --- Upgrade-only application ---

    [Fact]
    public void Upgrade_AppliesSyncedLyricsOverPlainOnly()
    {
        var song = PlainOnlySong();

        Assert.True(song.TryApplyLyricsUpgrade("[00:01.00]a line", "a line", instrumental: false, lrclibId: 42));

        Assert.Equal("[00:01.00]a line", song.SyncedLyrics);
        Assert.Equal(LyricsStatus.Fetched, song.LyricsStatus);
        Assert.Equal("42", song.LrclibId);
        // Now terminal — nothing better for LRCLIB to give.
        Assert.False(song.IsLyricsRecheckCandidate);
    }

    [Fact]
    public void Upgrade_KeepsExistingPlainLyricsWhenResponseCarriesOnlySynced()
    {
        var song = PlainOnlySong();

        Assert.True(song.TryApplyLyricsUpgrade("[00:01.00]a line", null, instrumental: false));

        Assert.Equal("a line", song.PlainLyrics);
    }

    [Fact]
    public void Upgrade_AppliesFirstLyricsToANotFoundSong()
    {
        var song = NotFoundSong();

        Assert.True(song.TryApplyLyricsUpgrade(null, "a line", instrumental: false));

        Assert.Equal("a line", song.PlainLyrics);
        Assert.Equal(LyricsStatus.Fetched, song.LyricsStatus);
        // Still improvable: an LRC may show up later.
        Assert.True(song.IsLyricsRecheckCandidate);
    }

    [Fact]
    public void Upgrade_IgnoresAPlainOnlyResponseForASongThatAlreadyHasPlainLyrics()
    {
        var song = PlainOnlySong();

        Assert.False(song.TryApplyLyricsUpgrade(null, "a different line", instrumental: false));

        Assert.Equal("a line", song.PlainLyrics);
    }

    [Fact]
    public void Upgrade_NeverClearsLyricsOnAnInstrumentalVerdict()
    {
        var song = PlainOnlySong();

        Assert.False(song.TryApplyLyricsUpgrade(null, null, instrumental: true));

        Assert.Equal("a line", song.PlainLyrics);
        Assert.Equal(LyricsStatus.Fetched, song.LyricsStatus);
    }

    [Fact]
    public void Upgrade_AcceptsAnInstrumentalVerdictWhenNoLyricsAreStored()
    {
        var song = NotFoundSong();

        Assert.True(song.TryApplyLyricsUpgrade(null, null, instrumental: true));

        Assert.Equal(LyricsStatus.Instrumental, song.LyricsStatus);
        Assert.False(song.IsLyricsRecheckCandidate);
    }

    [Fact]
    public void Upgrade_IsANoOpForASongThatAlreadyHasSyncedLyrics()
    {
        var song = NewSong();
        song.ApplyLyricsResult("[00:01.00]mine", "mine", instrumental: false);

        Assert.False(song.TryApplyLyricsUpgrade("[00:02.00]theirs", "theirs", instrumental: false));

        Assert.Equal("[00:01.00]mine", song.SyncedLyrics);
    }

    // --- Backoff ---

    [Fact]
    public void RecordAttempt_SchedulesAnExponentiallyGrowingRecheck()
    {
        var song = NotFoundSong();

        song.RecordLyricsAttempt(Now, baseCooldownDays: 7, maxCooldownDays: 90);
        Assert.Equal(1, song.LyricsFetchAttempts);
        Assert.Equal(Now, song.LyricsLastAttemptedAtUtc);
        Assert.Equal(Now.AddDays(7), song.LyricsNextRecheckAfterUtc);

        song.RecordLyricsAttempt(Now, baseCooldownDays: 7, maxCooldownDays: 90);
        Assert.Equal(Now.AddDays(14), song.LyricsNextRecheckAfterUtc);

        song.RecordLyricsAttempt(Now, baseCooldownDays: 7, maxCooldownDays: 90);
        Assert.Equal(Now.AddDays(28), song.LyricsNextRecheckAfterUtc);
    }

    [Theory]
    [InlineData(1, 7)]
    [InlineData(4, 56)]
    [InlineData(5, 90)]      // capped
    [InlineData(400, 90)]    // exponent clamped, no overflow
    public void ComputeRecheckDelay_IsCappedAndOverflowSafe(int attempts, int expectedDays)
    {
        Assert.Equal(expectedDays, SongMetadata.ComputeLyricsRecheckDelayDays(attempts, 7, 90));
    }

    [Fact]
    public void RecordAttempt_ClearsTheScheduleOnceTheOutcomeIsTerminal()
    {
        var song = NotFoundSong();
        song.RecordLyricsAttempt(Now, 7, 90);
        Assert.NotNull(song.LyricsNextRecheckAfterUtc);

        song.TryApplyLyricsUpgrade("[00:01.00]a line", "a line", instrumental: false);
        song.RecordLyricsAttempt(Now, 7, 90);

        Assert.Null(song.LyricsNextRecheckAfterUtc);
    }

    [Fact]
    public void ResetLyrics_ClearsTheRecheckBookkeeping()
    {
        var song = NotFoundSong();
        song.RecordLyricsAttempt(Now, 7, 90);

        song.ResetLyrics();

        Assert.Equal(0, song.LyricsFetchAttempts);
        Assert.Null(song.LyricsLastAttemptedAtUtc);
        Assert.Null(song.LyricsNextRecheckAfterUtc);
        Assert.Equal(LyricsStatus.NotFetched, song.LyricsStatus);
    }
}

using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Enrichment;

/// <summary>
/// The free (no-API) half of lyrics timing validation. These are the checks that run on every song, so their
/// job is to be decisive when they fire and silent when they cannot tell — a false Suspect costs a paid AI
/// probe, and a false Ok hides genuinely broken lyrics.
/// </summary>
public class LyricsTimingValidatorTests
{
    /// <summary>An LRC with one line every <paramref name="everySeconds"/> from <paramref name="firstAt"/>.</summary>
    private static string Lrc(int lines, double firstAt = 5, double everySeconds = 10)
        => string.Join('\n', Enumerable.Range(0, lines).Select(i => Tag(firstAt + (i * everySeconds)) + $"line {i}"));

    private static string Tag(double seconds)
        => string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"[{(int)(seconds / 60):00}:{seconds % 60:00.00}]");

    [Fact]
    public void well_formed_lrc_covering_the_track_is_ok()
    {
        var verdict = LyricsTimingValidator.Check(Lrc(18), trackDurationSeconds: 200, lrclibDurationSeconds: 200);

        Assert.Equal(LyricsSyncStatus.Ok, verdict.Status);
        Assert.Null(verdict.Issue);
    }

    [Fact]
    public void lrclib_entry_timed_against_a_different_length_recording_is_suspect()
    {
        // The exact shape of the bug: LRCLIB's search fallback handed us a longer edit of the same song.
        var verdict = LyricsTimingValidator.Check(Lrc(18), trackDurationSeconds: 200, lrclibDurationSeconds: 260);

        Assert.Equal(LyricsSyncStatus.Suspect, verdict.Status);
        Assert.Contains("recording", verdict.Issue);
    }

    [Fact]
    public void small_duration_disagreements_are_tolerated()
    {
        // Encoders and taggers disagree by a second or two on the same recording all the time.
        var verdict = LyricsTimingValidator.Check(Lrc(18), trackDurationSeconds: 200, lrclibDurationSeconds: 202);

        Assert.Equal(LyricsSyncStatus.Ok, verdict.Status);
    }

    [Fact]
    public void lyrics_running_past_the_end_of_the_track_are_suspect()
    {
        // 18 lines every 10s from 0:05 runs to 2:55; a 100s track cannot contain that.
        var verdict = LyricsTimingValidator.Check(Lrc(18), trackDurationSeconds: 100, lrclibDurationSeconds: null);

        Assert.Equal(LyricsSyncStatus.Suspect, verdict.Status);
        Assert.Contains("past the end", verdict.Issue);
    }

    [Fact]
    public void lyrics_stopping_less_than_halfway_through_a_long_track_are_suspect()
    {
        // Lines end at 0:45; the track runs 6 minutes. Five minutes of unaccounted-for audio is not an outro.
        var verdict = LyricsTimingValidator.Check(Lrc(5), trackDurationSeconds: 360, lrclibDurationSeconds: null);

        Assert.Equal(LyricsSyncStatus.Suspect, verdict.Status);
        Assert.Contains("stop", verdict.Issue);
    }

    [Fact]
    public void a_real_instrumental_outro_does_not_trip_the_coverage_check()
    {
        // Lines end at 2:35 of a 3:20 track: under half the track is NOT uncovered, so this must stay Ok.
        var verdict = LyricsTimingValidator.Check(Lrc(16), trackDurationSeconds: 200, lrclibDurationSeconds: null);

        Assert.Equal(LyricsSyncStatus.Ok, verdict.Status);
    }

    [Fact]
    public void lines_collapsed_onto_one_timestamp_are_suspect()
    {
        var collapsed = string.Join('\n', Enumerable.Range(0, 10).Select(i => $"[00:30.00]line {i}"));

        var verdict = LyricsTimingValidator.Check(collapsed, trackDurationSeconds: 200, lrclibDurationSeconds: 200);

        Assert.Equal(LyricsSyncStatus.Suspect, verdict.Status);
        Assert.Contains("single timestamp", verdict.Issue);
    }

    [Fact]
    public void nothing_to_judge_is_reported_as_unverifiable_not_as_ok()
    {
        Assert.Equal(LyricsSyncStatus.Unverifiable, LyricsTimingValidator.Check(null, 200, 200).Status);
        Assert.Equal(LyricsSyncStatus.Unverifiable, LyricsTimingValidator.Check(Lrc(2), 200, null).Status);
        Assert.Equal(LyricsSyncStatus.Unverifiable, LyricsTimingValidator.Check(Lrc(18), null, null).Status);
    }

    [Fact]
    public void a_uniformly_shifted_lrc_still_passes__which_is_why_the_probe_exists()
    {
        // Every line 20s late, same length, same coverage shape. Arithmetic alone cannot see this; only
        // listening to the audio can. Documented as a test so the limitation stays deliberate.
        var verdict = LyricsTimingValidator.Check(Lrc(15, firstAt: 25), trackDurationSeconds: 200, lrclibDurationSeconds: 200);

        Assert.Equal(LyricsSyncStatus.Ok, verdict.Status);
    }

    [Theory]
    [InlineData("[00:12.34]hello", 12.34)]
    [InlineData("[00:12]hello", 12)]
    [InlineData("[00:12:34]hello", 12.34)]      // some taggers use a colon before the fraction
    [InlineData("[00:12.5]hello", 12.5)]        // one-digit fraction is tenths, not thousandths
    [InlineData("[101:02.00]hello", 6062)]      // minutes past 99 on a long track
    public void parses_every_lrc_timestamp_dialect(string line, double expectedSeconds)
    {
        var parsed = LyricsTimingValidator.ParseLrc(line);

        Assert.Equal(expectedSeconds, Assert.Single(parsed).Start, precision: 2);
    }

    [Fact]
    public void metadata_tags_are_not_mistaken_for_timestamps()
    {
        var parsed = LyricsTimingValidator.ParseLrc("[ar:Some Artist]\n[al:Some Album]\n[00:05.00]real line");

        Assert.Equal("real line", Assert.Single(parsed).Text);
    }

    [Fact]
    public void a_line_tagged_several_times_yields_one_entry_per_tag()
    {
        var parsed = LyricsTimingValidator.ParseLrc("[00:10.00][01:20.00]the hook");

        Assert.Equal(2, parsed.Count);
        Assert.All(parsed, l => Assert.Equal("the hook", l.Text));
    }

    [Fact]
    public void shifting_moves_every_timestamp_and_leaves_the_words_alone()
    {
        var shifted = LyricsTimingValidator.ShiftLrc("[00:05.00]one\n[00:15.00]two", 3.5);

        Assert.Equal("[00:08.50]one\n[00:18.50]two", shifted);
    }

    [Fact]
    public void shifting_backwards_clamps_at_the_start_of_the_track()
    {
        var shifted = LyricsTimingValidator.ShiftLrc("[00:02.00]one\n[00:20.00]two", -10);

        Assert.Equal("[00:00.00]one\n[00:10.00]two", shifted);
    }
}

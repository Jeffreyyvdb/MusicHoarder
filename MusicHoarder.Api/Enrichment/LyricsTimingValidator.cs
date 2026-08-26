using System.Globalization;
using System.Text.RegularExpressions;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment;

/// <summary>One parsed LRC line: when it starts (seconds into the track) and the words on it.</summary>
public readonly record struct LrcLine(double Start, string Text);

/// <summary>A free-check verdict plus the reason a human would want to read on the badge.</summary>
public record LyricsTimingVerdict(LyricsSyncStatus Status, string? Issue);

/// <summary>
/// Decides whether a stored LRC's timestamps can possibly belong to the audio we hold — using nothing but
/// arithmetic. No network call, no API key, no cost, so it can run over the entire library in one sweep and
/// on every new fetch.
///
/// The failure it exists to catch: LRCLIB's <c>/api/search</c> fallback is keyed on track name only, so a
/// song whose <c>/api/get</c> lookup missed can come back with an LRC belonging to a <b>different recording</b>
/// — a live cut, a sped-up edit, an extended mix. Those lyrics are the right words with timestamps that drift
/// further apart the longer the song plays, which is exactly what "the timestamps are very off" looks like.
/// A length disagreement between the two recordings is the cheapest possible tell, and it is decisive.
///
/// Everything here is deliberately conservative: a Suspect verdict costs a paid AI probe, and an <see
/// cref="LyricsSyncStatus.Ok"/> verdict is only ever "nothing provably wrong", never "verified correct" —
/// a uniformly shifted LRC passes every check below and only the probe can see it.
/// </summary>
public static class LyricsTimingValidator
{
    /// <summary>How far the LRCLIB entry's own track length may differ from ours before the LRC is suspect.</summary>
    public const double DurationToleranceSeconds = 4.0;

    /// <summary>How far past the end of the track a line may start before the LRC is provably not ours.</summary>
    private const double OverrunToleranceSeconds = 5.0;

    /// <summary>Below this many lines the checks have nothing to reason about.</summary>
    private const int MinLinesToJudge = 4;

    /// <summary>An LRC that stops before this fraction of the track is only suspect if the tail is also long.</summary>
    private const double MinCoverageRatio = 0.5;

    /// <summary>...and "long" means this many seconds of unaccounted-for track, so real outros stay Ok.</summary>
    private const double MinUncoveredTailSeconds = 60.0;

    /// <summary>
    /// Runs every free check against a song's stored synced lyrics. Returns <see cref="LyricsSyncStatus.Unverifiable"/>
    /// when there is nothing to judge (no LRC, no known track length, too few lines) rather than guessing.
    /// </summary>
    public static LyricsTimingVerdict Check(SongMetadata song)
        => Check(song.SyncedLyrics, song.DurationSeconds, song.LrclibDurationSeconds);

    public static LyricsTimingVerdict Check(string? syncedLyrics, int? trackDurationSeconds, double? lrclibDurationSeconds)
    {
        var lines = ParseLrc(syncedLyrics);
        if (lines.Count == 0)
            return new LyricsTimingVerdict(LyricsSyncStatus.Unverifiable, "no synced lyrics to check");

        // Check 1 — the two recordings disagree on length. Decisive and independent of the line data: LRCLIB
        // told us how long the track it timed these words against is, and it is not the track we hold.
        if (trackDurationSeconds is > 0 && lrclibDurationSeconds is > 0)
        {
            var delta = Math.Abs(lrclibDurationSeconds.Value - trackDurationSeconds.Value);
            if (delta > DurationToleranceSeconds)
            {
                return new LyricsTimingVerdict(
                    LyricsSyncStatus.Suspect,
                    FormatIssue(
                        "the LRCLIB entry was timed against a {0} recording; ours is {1} ({2} apart)",
                        FormatDuration(lrclibDurationSeconds.Value),
                        FormatDuration(trackDurationSeconds.Value),
                        FormatDuration(delta)));
            }
        }

        if (lines.Count < MinLinesToJudge)
            return new LyricsTimingVerdict(LyricsSyncStatus.Unverifiable, "too few timed lines to judge");

        // Check 2 — lines collapsed onto one timestamp. A hand-broken LRC never looks like this; a botched
        // alignment does, and so does an LRC whose tags failed to parse into distinct times.
        var mostOnOneStamp = lines.GroupBy(l => Math.Round(l.Start, 1)).Max(g => g.Count());
        if (mostOnOneStamp > lines.Count * 0.4)
        {
            return new LyricsTimingVerdict(
                LyricsSyncStatus.Suspect,
                FormatIssue("{0} of {1} lines share a single timestamp", mostOnOneStamp, lines.Count));
        }

        if (trackDurationSeconds is not > 0)
            return new LyricsTimingVerdict(LyricsSyncStatus.Unverifiable, "track duration unknown");

        var duration = (double)trackDurationSeconds.Value;
        var last = lines[^1].Start;

        // Check 3 — a line starts after the song has ended. Nothing legitimate does this; the LRC belongs to
        // a longer edit of the track.
        if (last > duration + OverrunToleranceSeconds)
        {
            return new LyricsTimingVerdict(
                LyricsSyncStatus.Suspect,
                FormatIssue("the lyrics run {0} past the end of the track", FormatDuration(last - duration)));
        }

        // Check 4 — the lyrics stop absurdly early. Long instrumental outros are real, so both a proportional
        // AND an absolute gate must trip: half the track unaccounted for *and* at least a minute of it.
        var tail = duration - last;
        if (last < duration * MinCoverageRatio && tail > MinUncoveredTailSeconds)
        {
            return new LyricsTimingVerdict(
                LyricsSyncStatus.Suspect,
                FormatIssue("the lyrics stop {0} before the end of the track", FormatDuration(tail)));
        }

        // Check 5 — the first line lands past the halfway mark. Intros are long sometimes, not that long.
        if (lines[0].Start > duration * 0.5)
        {
            return new LyricsTimingVerdict(
                LyricsSyncStatus.Suspect,
                FormatIssue("the first line does not start until {0} into the track", FormatDuration(lines[0].Start)));
        }

        return new LyricsTimingVerdict(LyricsSyncStatus.Ok, null);
    }

    /// <summary>
    /// Matches one LRC timestamp tag: <c>[mm:ss]</c>, <c>[mm:ss.xx]</c> or <c>[mm:ss:xx]</c> (LRCLIB and some
    /// taggers use a colon before the fraction). Minutes run 1-3 digits for long tracks. Kept in lockstep with
    /// <c>frontend/src/lib/lyrics/parse-lrc.ts</c> and <c>android/.../data/Lyrics.kt</c>.
    /// </summary>
    private static readonly Regex TimestampTag = new(@"\[(\d{1,3}):([0-5]?\d)(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);

    /// <summary>
    /// Parses LRC text into time-ordered lines. Metadata-only tags (<c>[ar:...]</c>) carry no mm:ss and are
    /// skipped; a line tagged several times (an LRC's way of repeating a hook) yields one entry per tag.
    /// Returns an empty list when nothing parses.
    /// </summary>
    public static List<LrcLine> ParseLrc(string? lrc)
    {
        var result = new List<LrcLine>();
        if (string.IsNullOrWhiteSpace(lrc))
            return result;

        foreach (var raw in lrc.Split('\n'))
        {
            var stamps = new List<double>();
            var lastTagEnd = 0;
            foreach (Match m in TimestampTag.Matches(raw))
            {
                var minutes = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var seconds = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                var fractionText = m.Groups[3].Value;
                // ".5" is 500ms, ".50" is 500ms, ".500" is 500ms — pad, don't parse as an integer count.
                var fraction = fractionText.Length == 0
                    ? 0d
                    : double.Parse(fractionText.PadRight(3, '0'), CultureInfo.InvariantCulture) / 1000d;
                stamps.Add((minutes * 60) + seconds + fraction);
                lastTagEnd = m.Index + m.Length;
            }

            if (stamps.Count == 0)
                continue;

            var text = raw[lastTagEnd..].Trim('\r', ' ', '\t');
            foreach (var start in stamps)
                result.Add(new LrcLine(start, text));
        }

        result.Sort((a, b) => a.Start.CompareTo(b.Start));
        return result;
    }

    /// <summary>
    /// Re-emits an LRC with every timestamp moved by <paramref name="offsetSeconds"/> (positive = later),
    /// clamped at zero. The words are untouched, so the result is still the human lyric — only its clock
    /// changed. Returns null when there is nothing to shift.
    /// </summary>
    public static string? ShiftLrc(string? lrc, double offsetSeconds)
    {
        var lines = ParseLrc(lrc);
        if (lines.Count == 0)
            return null;
        return LrcBuilder.Format(lines.Select(l => (Math.Max(0, l.Start + offsetSeconds), l.Text)).ToList());
    }

    private static string FormatIssue(string format, params object[] args)
        => string.Format(CultureInfo.InvariantCulture, format, args);

    /// <summary>Renders a span of seconds the way the badge tooltip should read it: "4s", "1m12s".</summary>
    private static string FormatDuration(double seconds)
    {
        var total = (int)Math.Round(Math.Abs(seconds));
        return total < 60 ? $"{total}s" : $"{total / 60}m{total % 60:00}s";
    }
}

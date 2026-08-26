using System.Text;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment;

/// <summary>What a probe concluded, and (when it found a fixable one) the shift it measured.</summary>
/// <param name="OffsetSeconds">
/// How much later the audio actually is than the LRC claims. Positive means the stored lyrics run early and
/// every line must move later by this much. Only meaningful for
/// <see cref="LyricsSyncStatus.Corrected"/> and <see cref="LyricsSyncStatus.Suspect"/>.
/// </param>
/// <param name="Confidence">Fraction of the probed words the transcript agreed with, 0-1.</param>
public record LyricsProbeResult(
    LyricsSyncStatus Status,
    string? Issue,
    double OffsetSeconds,
    double? Confidence,
    bool SpentBudget);

/// <summary>
/// Checks a song's stored LRC against what the audio actually says, using one short Whisper window instead of
/// a whole transcription — and, when the whole LRC turns out to be uniformly early or late, measures the shift
/// so the timing can be repaired without transcribing anything further.
///
/// It runs in two steps, and the order matters:
///
/// <b>1. Confirm.</b> Take the lyric lines the LRC claims fall inside the window and forced-align just those
/// against the window's words. This tests the LRC's own hypothesis directly, which makes it immune to the
/// trap that catches every naive approach — a chorus sung six times matches all six places, so "find where
/// this audio appears in the lyrics" is ambiguous, while "are these specific lines here?" is not.
///
/// <b>2. Locate.</b> Only if the confirmation fails, find where the window's words really sit in the lyric
/// text (a Smith-Waterman local alignment) and compare each matched word's real time against the time the
/// LRC implies for it. A tight cluster of identical errors is a constant offset and is repairable by shifting.
/// Errors that scatter mean the LRC runs at a different pace — a sped-up or extended edit of the song — and no
/// shift fixes that, so the verdict is Suspect and the user is offered a full re-transcription instead.
///
/// The probe never rewrites words. Anything it changes is a timestamp, which is why a song it repairs is
/// still the human lyric and is labelled <see cref="LyricsProvenance.AiEnhanced"/> rather than AI-generated.
/// </summary>
public sealed class LyricsTimingProbe(
    ILyricsTranscriptionService transcriber,
    LyricsProbeBudget budget,
    IOptionsMonitor<LyricsTimingOptions> options,
    ILogger<LyricsTimingProbe> logger)
{
    /// <summary>True when a probe could run right now: enabled, transcription configured, budget left.</summary>
    public bool IsAvailable => options.CurrentValue.EnableAiProbe && transcriber.IsConfigured;

    /// <summary>
    /// Probes one song and returns the verdict. Does NOT write to <paramref name="song"/> — the caller decides
    /// whether to persist, because the sweep and the on-demand endpoint report differently. Returns null when
    /// the probe could not run at all (disabled, no budget, nothing to check).
    /// </summary>
    public async Task<LyricsProbeResult?> ProbeAsync(SongMetadata song, string audioFilePath, CancellationToken ct = default)
    {
        var opts = options.CurrentValue;
        if (!IsAvailable)
            return null;

        var lrcLines = LyricsTimingValidator.ParseLrc(song.SyncedLyrics);
        if (lrcLines.Count < 4)
            return new LyricsProbeResult(LyricsSyncStatus.Unverifiable, "too few timed lines to probe", 0, null, false);

        if (!File.Exists(audioFilePath))
            return new LyricsProbeResult(LyricsSyncStatus.Unverifiable, "audio file not found on disk", 0, null, false);

        var duration = song.DurationSeconds is > 0 ? (double)song.DurationSeconds.Value : lrcLines[^1].Start + 30;
        var window = opts.ProbeWindowSeconds;
        // Keep the whole window inside the track — a window that runs off the end is paid-for silence.
        var start = Math.Clamp(duration * opts.ProbeWindowPosition, 0, Math.Max(0, duration - window));

        if (!budget.TryReserve(window))
        {
            logger.LogDebug("Skipping lyrics timing probe for SongId={SongId}: probe budget exhausted.", song.Id);
            return null;
        }

        IReadOnlyList<TimedWord> words;
        try
        {
            // Prime Whisper with the words we expect to hear anywhere in the song. It cannot tell us WHERE
            // they are — that is the whole question — but it markedly improves which ones it gets right.
            words = await transcriber.TranscribeClipAsync(audioFilePath, start, window, BuildPrompt(song), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            budget.Refund(window);
            throw;
        }
        catch (TranscriptionRateLimitedException ex)
        {
            // Our own throttling, not a fact about this song. Returning a verdict here would spend one of
            // the song's bounded probe attempts on a request the provider refused to even look at, so two
            // busy sweeps would leave it permanently unverifiable. Null means "we did not look": no verdict,
            // no attempt recorded, and the sweep stops this batch rather than hammering a closed door.
            budget.Refund(window);
            logger.LogInformation(
                "Lyrics timing probe deferred for SongId={SongId}: {Reason}", song.Id, ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Lyrics timing probe failed for SongId={SongId}.", song.Id);
            return new LyricsProbeResult(LyricsSyncStatus.Unverifiable, "the timing probe could not be completed", 0, null, true);
        }

        if (words.Count == 0)
        {
            // A window of pure instrumental. Says nothing either way, and re-rolling it costs budget, so let
            // the caller record the attempt and move on.
            return new LyricsProbeResult(
                LyricsSyncStatus.Unverifiable, "no words were sung in the probed window", 0, null, true);
        }

        var end = start + window;

        // --- Step 1: does the LRC's own claim hold up here? ---
        var claimed = lrcLines
            .Select((line, index) => (line, index))
            .Where(x => x.line.Start >= start && x.line.Start < end && !string.IsNullOrWhiteSpace(x.line.Text))
            .ToList();

        if (claimed.Count >= 2)
        {
            var alignment = ForcedLyricsAligner.AlignDetailed(
                claimed.Select(c => c.line.Text).ToList(), words, minMatchRatio: 0.35);

            if (alignment is not null)
            {
                var errors = new List<double>();
                for (var i = 0; i < claimed.Count; i++)
                    if (alignment.AnchoredStarts[i] is double actual)
                        errors.Add(actual - claimed[i].line.Start);

                if (errors.Count >= 2 && Median(errors) is var median && Math.Abs(median) <= opts.OkToleranceSeconds)
                {
                    logger.LogDebug(
                        "Lyrics timing probe for SongId={SongId}: LRC confirmed (median error {Error:F2}s over {Lines} lines).",
                        song.Id, median, errors.Count);
                    return new LyricsProbeResult(LyricsSyncStatus.Ok, null, median, alignment.MatchRatio, true);
                }
            }
        }

        // --- Step 2: the LRC is wrong here. Where do these words really belong? ---
        var located = Locate(lrcLines, words, opts);
        if (located is null || located.Value.Matched < opts.MinMatchedWords)
        {
            return new LyricsProbeResult(
                LyricsSyncStatus.Unverifiable,
                "the probed window could not be matched to the lyrics",
                0,
                null,
                true);
        }

        var (offset, spread, matched, confidence) = located.Value;

        if (Math.Abs(offset) <= opts.OkToleranceSeconds && spread <= opts.ConstantOffsetSpreadSeconds)
        {
            return new LyricsProbeResult(LyricsSyncStatus.Ok, null, offset, confidence, true);
        }

        if (spread <= opts.ConstantOffsetSpreadSeconds)
        {
            logger.LogInformation(
                "Lyrics timing probe for SongId={SongId}: constant {Offset:F1}s offset over {Matched} words (spread {Spread:F2}s) — repairable by shifting.",
                song.Id, offset, matched, spread);
            return new LyricsProbeResult(LyricsSyncStatus.Corrected, null, offset, confidence, true);
        }

        return new LyricsProbeResult(
            LyricsSyncStatus.Suspect,
            $"the timing drifts rather than sitting {FormatSeconds(offset)} out throughout, so the lyrics were written for a different edit of the track",
            offset,
            confidence,
            true);
    }

    /// <summary>
    /// Finds where the probed words actually sit in the lyric text and reports how far that is from where the
    /// LRC puts them: the median error (the offset), how tightly the individual errors agree (the spread, as a
    /// median absolute deviation), how many words matched, and the share of the window that matched at all.
    /// </summary>
    private static (double Offset, double Spread, int Matched, double Confidence)? Locate(
        IReadOnlyList<LrcLine> lrcLines, IReadOnlyList<TimedWord> words, LyricsTimingOptions opts)
    {
        // Reference tokens, each remembering the time the LRC implies for it. A line's words are spread evenly
        // between its own timestamp and the next line's, so a word matched late in a line is not read as
        // evidence that the whole line is late.
        var refTokens = new List<(string Norm, double ImpliedTime)>();
        for (var i = 0; i < lrcLines.Count; i++)
        {
            var tokens = lrcLines[i].Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(Normalize)
                .Where(t => t.Length > 0)
                .ToList();
            if (tokens.Count == 0)
                continue;

            var lineStart = lrcLines[i].Start;
            var lineEnd = i + 1 < lrcLines.Count ? lrcLines[i + 1].Start : lineStart + (tokens.Count * 0.4);
            var span = Math.Max(0.1, Math.Min(lineEnd - lineStart, tokens.Count * 0.6));

            for (var t = 0; t < tokens.Count; t++)
                refTokens.Add((tokens[t], lineStart + (span * t / tokens.Count)));
        }

        var hyp = words.Select(w => (Norm: Normalize(w.Word), w.Start)).Where(w => w.Norm.Length > 0).ToList();
        if (refTokens.Count == 0 || hyp.Count == 0)
            return null;

        var pairs = SmithWaterman(refTokens, hyp);
        if (pairs.Count < opts.MinMatchedWords)
            return null;

        var errors = pairs.Select(p => p.Actual - p.Implied).ToList();
        var offset = Median(errors);
        var spread = Median(errors.Select(e => Math.Abs(e - offset)).ToList());
        return (offset, spread, pairs.Count, Math.Min(1.0, (double)pairs.Count / hyp.Count));
    }

    /// <summary>
    /// Smith-Waterman local alignment: finds the single best-matching stretch of the lyric text for the probed
    /// words, and returns only the pairs that genuinely matched. Local (not global) because the window is a few
    /// seconds of a whole song — almost all of the reference is irrelevant and must cost nothing to skip.
    /// </summary>
    private static List<(double Implied, double Actual)> SmithWaterman(
        IReadOnlyList<(string Norm, double ImpliedTime)> reference,
        IReadOnlyList<(string Norm, double Start)> hypothesis)
    {
        const int match = 3, mismatch = -2, gap = -2;
        var n = reference.Count;
        var m = hypothesis.Count;

        var f = new int[n + 1, m + 1];
        var bestScore = 0;
        int bestI = 0, bestJ = 0;

        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var diag = f[i - 1, j - 1] + (reference[i - 1].Norm == hypothesis[j - 1].Norm ? match : mismatch);
                var score = Math.Max(0, Math.Max(diag, Math.Max(f[i - 1, j] + gap, f[i, j - 1] + gap)));
                f[i, j] = score;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestI = i;
                    bestJ = j;
                }
            }
        }

        var pairs = new List<(double, double)>();
        var (ri, hj) = (bestI, bestJ);
        while (ri > 0 && hj > 0 && f[ri, hj] > 0)
        {
            var same = reference[ri - 1].Norm == hypothesis[hj - 1].Norm;
            var diag = f[ri - 1, hj - 1] + (same ? match : mismatch);
            if (f[ri, hj] == diag)
            {
                if (same)
                    pairs.Add((reference[ri - 1].ImpliedTime, hypothesis[hj - 1].Start));
                ri--;
                hj--;
            }
            else if (f[ri, hj] == f[ri - 1, hj] + gap)
            {
                ri--;
            }
            else
            {
                hj--;
            }
        }

        return pairs;
    }

    /// <summary>The song's own words, for Whisper's prompt. Plain lyrics if we have them, else the LRC's text.</summary>
    private static string? BuildPrompt(SongMetadata song)
    {
        var text = song.PlainLyrics;
        if (string.IsNullOrWhiteSpace(text))
        {
            var sb = new StringBuilder();
            foreach (var line in LyricsTimingValidator.ParseLrc(song.SyncedLyrics))
                sb.Append(line.Text).Append(' ');
            text = sb.ToString();
        }

        if (string.IsNullOrWhiteSpace(text))
            return null;

        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Take(150));
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
            return 0;
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    private static string Normalize(string word)
    {
        var sb = new StringBuilder(word.Length);
        foreach (var c in word)
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private static string FormatSeconds(double seconds)
    {
        var total = Math.Abs(seconds);
        return total < 60 ? $"{total:F0}s" : $"{(int)total / 60}m{(int)total % 60:00}s";
    }
}

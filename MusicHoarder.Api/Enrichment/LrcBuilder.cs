using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MusicHoarder.Api.Enrichment;

/// <summary>A coarse transcript segment (Whisper's segment granularity): a start time plus its text.</summary>
public record TranscriptSegment(double Start, string? Text);

/// <summary>
/// Pure, IO-free assembly of an LRC lyric file from a transcript's timed words/segments. Given the raw
/// timing signal (words with start/end times, or coarse segments) and, optionally, official reference
/// lyric lines, it decides where lines break, stamps each line, repairs collapsed repeated hooks, judges
/// whether an alignment is trustworthy, and renders the <c>[mm:ss.xx]</c> LRC text.
///
/// Deliberately free of ffmpeg, HTTP, and options so the line-splitting and formatting rules can be unit
/// tested in isolation — mirroring <see cref="ForcedLyricsAligner"/>. <see cref="LyricsTranscriptionService"/>
/// owns the IO (transcode, upload, reference-lyric fetch) and delegates every LRC decision here.
/// </summary>
public static class LrcBuilder
{
    /// <summary>
    /// Splits reference lyric text into non-empty lines, stripping any leading <c>[mm:ss.xx]</c> LRC tag so
    /// synced reference lyrics collapse to their plain text. Returns null when there is nothing usable.
    /// </summary>
    public static List<string>? SplitReferenceLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var lines = text.Replace("\r\n", "\n").Split('\n')
            .Select(l => Regex.Replace(l.Trim(), @"^\[\d{1,2}:\d{2}(?:[.:]\d{1,3})?\]\s*", ""))
            .Where(l => l.Length > 0)
            .ToList();
        return lines.Count > 0 ? lines : null;
    }

    /// <summary>
    /// Re-chunks the flat word list into LRC lines: starts a new line whenever the silent gap before a
    /// word reaches <paramref name="pauseThresholdSeconds"/> or the current line hits
    /// <paramref name="maxWordsPerLine"/>. Each line is stamped with its first word's start time.
    /// </summary>
    public static List<(double Start, string Text)>? BuildLinesFromWords(
        IReadOnlyList<TimedWord>? words, double pauseThresholdSeconds, int maxWordsPerLine)
    {
        if (words is not { Count: > 0 })
            return null;

        var lines = new List<(double, string)>();
        var current = new List<string>();
        double lineStart = 0;
        double? prevEnd = null;

        void Flush()
        {
            if (current.Count == 0) return;
            var text = string.Join(' ', current).Trim();
            if (text.Length > 0) lines.Add((lineStart, text));
            current.Clear();
        }

        foreach (var w in words)
        {
            var token = w.Word?.Trim();
            if (string.IsNullOrEmpty(token))
            {
                prevEnd = w.End;
                continue;
            }

            var gap = prevEnd is { } pe ? w.Start - pe : 0;
            var shouldBreak = current.Count > 0
                && ((pauseThresholdSeconds > 0 && gap >= pauseThresholdSeconds) || current.Count >= maxWordsPerLine);
            if (shouldBreak)
                Flush();

            if (current.Count == 0)
                lineStart = w.Start;
            current.Add(token);
            prevEnd = w.End;
        }

        Flush();
        return lines.Count > 0 ? lines : null;
    }

    /// <summary>Coarse fallback: one LRC line per non-empty transcript segment, stamped at the segment start.</summary>
    public static List<(double Start, string Text)>? BuildLinesFromSegments(IReadOnlyList<TranscriptSegment>? segments)
    {
        if (segments is not { Count: > 0 })
            return null;

        var lines = new List<(double, string)>();
        foreach (var segment in segments)
        {
            var text = segment.Text?.Trim();
            if (!string.IsNullOrEmpty(text))
                lines.Add((segment.Start, text));
        }

        return lines.Count > 0 ? lines : null;
    }

    /// <summary>
    /// Spreads runs of identical consecutive lines that collapsed onto the same start time (an alignment
    /// failure mode for repeated hooks) evenly across the gap up to the next distinct line. Only touches
    /// runs that actually share a timestamp, so genuinely distinct timings are left untouched.
    /// </summary>
    public static void SpreadRepeatedConsecutiveLines(List<(double Start, string Text)> lines)
    {
        var i = 0;
        while (i < lines.Count)
        {
            var j = i;
            while (j + 1 < lines.Count
                   && string.Equals(NormalizeLine(lines[j + 1].Text), NormalizeLine(lines[i].Text), StringComparison.OrdinalIgnoreCase)
                   && Math.Abs(lines[j + 1].Start - lines[i].Start) < 0.05)
            {
                j++;
            }

            var runLength = j - i + 1;
            if (runLength > 1)
            {
                var startT = lines[i].Start;
                // Spread up to the next distinct line; at song end, fall back to ~1s per line.
                var endT = j + 1 < lines.Count ? lines[j + 1].Start : startT + runLength;
                if (endT > startT)
                {
                    var step = (endT - startT) / runLength;
                    for (var k = 0; k < runLength; k++)
                        lines[i + k] = (startT + (k * step), lines[i + k].Text);
                }
            }

            i = j + 1;
        }
    }

    /// <summary>
    /// True when an alignment is unusable — null/empty, or so collapsed that &gt;40% of lines share a
    /// single timestamp (the LLM-on-repetitive-lyrics failure mode). The caller then falls back.
    /// </summary>
    public static bool IsDegenerate(IReadOnlyList<(double Start, string Text)>? lines)
    {
        if (lines is null || lines.Count == 0)
            return true;
        if (lines.Count < 4)
            return false;
        var mostOnOneStamp = lines.GroupBy(l => Math.Round(l.Start, 1)).Max(g => g.Count());
        return mostOnOneStamp > lines.Count * 0.4;
    }

    /// <summary>Renders timed lines as LRC text (one <c>[mm:ss.xx]</c>-tagged line each), no trailing newline.</summary>
    public static string Format(IReadOnlyList<(double Start, string Text)> lines)
    {
        var sb = new StringBuilder();
        foreach (var (start, text) in lines)
            sb.Append(FormatTimestamp(start)).Append(text).Append('\n');
        return sb.ToString().TrimEnd('\n');
    }

    private static string NormalizeLine(string text)
        => Regex.Replace(text.Trim(), @"\s+", " ");

    /// <summary>Formats seconds as an LRC <c>[mm:ss.xx]</c> tag (centisecond precision).</summary>
    private static string FormatTimestamp(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var minutes = (int)(seconds / 60);
        var secs = (int)(seconds % 60);
        var centis = (int)Math.Round((seconds - Math.Floor(seconds)) * 100);
        if (centis == 100) { centis = 0; secs++; }
        if (secs == 60) { secs = 0; minutes++; }
        return string.Format(CultureInfo.InvariantCulture, "[{0:00}:{1:00}.{2:00}]", minutes, secs, centis);
    }
}

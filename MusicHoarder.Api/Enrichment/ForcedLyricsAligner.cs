using System.Text;

namespace MusicHoarder.Api.Enrichment;

/// <summary>
/// Deterministic forced alignment of known lyric text (e.g. LRCLIB plain) to Whisper's word clock — the
/// technique real karaoke tools use. It computes a Needleman–Wunsch global alignment between the reference
/// word sequence and the transcript word sequence, then stamps each lyric line with the start time of its
/// first aligned word.
///
/// Because the alignment is strictly monotonic (in-order, no crossings), it is robust to heavily repeated
/// lyrics — a hook sung 20× maps to its 20 transcript occurrences in order, so it physically cannot jump
/// to a late occurrence and collapse the rest (the failure mode of LLM index-mapping). No network call,
/// so it's also fast and free. Returns null when the reference doesn't match the audio well enough to trust.
/// </summary>
public static class ForcedLyricsAligner
{
    private const int Match = 2;
    private const int Mismatch = -1;
    private const int Gap = -1;
    // Above this the DP matrix gets large; songs never reach it — bail to the LLM/heuristic if they do.
    private const int MaxTokens = 4000;
    // Reference must share at least this fraction of its words with the transcript to be trusted.
    private const double MinMatchRatio = 0.4;

    public static List<(double Start, string Text)>? Align(
        IReadOnlyList<string> referenceLines, IReadOnlyList<TimedWord> words)
    {
        if (referenceLines.Count == 0 || words.Count == 0)
            return null;

        // Reference tokens tagged with their source line; transcript tokens normalized for comparison.
        var refTokens = new List<(int Line, string Norm)>();
        for (var li = 0; li < referenceLines.Count; li++)
        {
            foreach (var raw in referenceLines[li].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var norm = Normalize(raw);
                if (norm.Length > 0)
                    refTokens.Add((li, norm));
            }
        }

        var n = refTokens.Count;
        var m = words.Count;
        if (n == 0 || n > MaxTokens || m > MaxTokens)
            return null;

        var hyp = new string[m];
        for (var j = 0; j < m; j++)
            hyp[j] = Normalize(words[j].Word);

        // Needleman–Wunsch score matrix.
        var f = new int[n + 1, m + 1];
        for (var i = 1; i <= n; i++) f[i, 0] = i * Gap;
        for (var j = 1; j <= m; j++) f[0, j] = j * Gap;
        for (var i = 1; i <= n; i++)
        {
            var a = refTokens[i - 1].Norm;
            for (var j = 1; j <= m; j++)
            {
                var diag = f[i - 1, j - 1] + (a == hyp[j - 1] ? Match : Mismatch);
                var up = f[i - 1, j] + Gap;
                var left = f[i, j - 1] + Gap;
                f[i, j] = Math.Max(diag, Math.Max(up, left));
            }
        }

        // Backtrack: record which transcript word each reference token aligned to (-1 = deleted).
        var alignedHyp = new int[n];
        Array.Fill(alignedHyp, -1);
        int ri = n, hj = m, matches = 0;
        while (ri > 0 && hj > 0)
        {
            var a = refTokens[ri - 1].Norm;
            var diag = f[ri - 1, hj - 1] + (a == hyp[hj - 1] ? Match : Mismatch);
            if (f[ri, hj] == diag)
            {
                alignedHyp[ri - 1] = hj - 1;
                if (a == hyp[hj - 1]) matches++;
                ri--;
                hj--;
            }
            else if (f[ri, hj] == f[ri - 1, hj] + Gap)
            {
                ri--; // reference word with no transcript match
            }
            else
            {
                hj--; // transcript word with no reference match
            }
        }

        // Quality gate: if the reference barely matches the audio, don't trust this alignment.
        if (matches < n * MinMatchRatio)
            return null;

        // Each line's start = the start time of its first aligned word.
        var lineStart = new double?[referenceLines.Count];
        for (var k = 0; k < refTokens.Count; k++)
        {
            var line = refTokens[k].Line;
            if (lineStart[line] is null && alignedHyp[k] >= 0)
                lineStart[line] = words[alignedHyp[k]].Start;
        }

        return BuildLines(referenceLines, lineStart);
    }

    /// <summary>Stamps every line, interpolating unanchored lines across the gap and forcing non-decreasing times.</summary>
    private static List<(double Start, string Text)> BuildLines(IReadOnlyList<string> referenceLines, double?[] lineStart)
    {
        var result = new List<(double, string)>(referenceLines.Count);
        double last = 0;
        for (var li = 0; li < referenceLines.Count; li++)
        {
            double t;
            if (lineStart[li] is double anchored)
            {
                t = Math.Max(anchored, last);
            }
            else
            {
                // Find the next anchored line and spread the unanchored run evenly up to it.
                double? next = null;
                var gap = 1;
                for (var p = li + 1; p < referenceLines.Count; p++)
                {
                    if (lineStart[p] is double ns) { next = Math.Max(ns, last); break; }
                    gap++;
                }
                t = next is double nx && nx > last ? last + ((nx - last) / (gap + 1)) : last;
            }

            result.Add((t, referenceLines[li]));
            last = t;
        }
        return result;
    }

    private static string Normalize(string word)
    {
        var sb = new StringBuilder(word.Length);
        foreach (var c in word)
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}

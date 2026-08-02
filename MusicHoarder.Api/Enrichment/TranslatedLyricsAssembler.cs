using System.Text.RegularExpressions;

namespace MusicHoarder.Api.Enrichment;

/// <summary>
/// A lyric line split into its verbatim leading LRC timestamp tag(s) and the sung text. For plain
/// (untimed) lyrics <see cref="TagPrefix"/> is empty.
/// </summary>
public record LyricSourceLine(string TagPrefix, string Text);

/// <summary>
/// Pure helpers for building a secondary lyrics document (pronunciation guide, translation) that stays
/// line-aligned with the original. <see cref="Parse"/> keeps the original timestamp tags VERBATIM
/// (including multi-tag lines) so re-attaching them in <see cref="Assemble"/> yields an LRC string that
/// the frontend's parser expands identically to the original — index alignment between the two documents
/// then holds by construction. IO-free and unit-testable, like <see cref="LrcBuilder"/>.
/// </summary>
public static partial class TranslatedLyricsAssembler
{
    // Tolerant cousin of LrcBuilder.SplitReferenceLines' pattern: one or more leading [mm:ss], [mm:ss.xx]
    // or [mm:ss:xx] tags (1-3 minute digits, optional .- or :-separated fraction).
    [GeneratedRegex(@"^(?:\[\d{1,3}:[0-5]?\d(?:[.:]\d{1,3})?\]\s*)+")]
    private static partial Regex LeadingTimestampTags();

    /// <summary>
    /// Splits lyrics (LRC or plain) into lines, capturing each line's verbatim leading timestamp tags.
    /// Lines with no sung text (blank, or timestamp-only) are dropped — they carry nothing to translate.
    /// </summary>
    public static List<LyricSourceLine> Parse(string lyrics)
    {
        var result = new List<LyricSourceLine>();
        foreach (var raw in lyrics.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;

            var match = LeadingTimestampTags().Match(line);
            var prefix = match.Success ? match.Value.TrimEnd() : string.Empty;
            var text = line[(match.Success ? match.Length : 0)..].Trim();
            if (text.Length == 0)
                continue;

            result.Add(new LyricSourceLine(prefix, text));
        }

        return result;
    }

    /// <summary>
    /// Re-attaches each source line's original timestamp tags to the corresponding secondary text.
    /// Returns the synced (LRC) document only when the source had timestamps, and the plain document
    /// always. Throws when the counts differ — a misaligned document is worse than none.
    /// </summary>
    public static (string? Synced, string Plain) Assemble(
        IReadOnlyList<LyricSourceLine> source, IReadOnlyList<string> secondary)
    {
        if (source.Count != secondary.Count)
            throw new ArgumentException(
                $"Secondary line count ({secondary.Count}) does not match source line count ({source.Count}).",
                nameof(secondary));

        var hasTimestamps = source.Any(l => l.TagPrefix.Length > 0);
        var synced = hasTimestamps
            ? string.Join('\n', source.Select((l, i) =>
                l.TagPrefix.Length > 0 ? $"{l.TagPrefix}{secondary[i]}" : secondary[i]))
            : null;
        var plain = string.Join('\n', secondary);
        return (synced, plain);
    }
}

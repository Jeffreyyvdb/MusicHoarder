namespace MusicHoarder.Api.Matching;

/// <summary>
/// Validates a provider candidate against the raw free-text of a file's path/filename by checking how
/// many of the candidate's tokens appear in that text. Used when a file has no embedded tags, so a
/// positional artist/title guess can't be trusted but the filename text can still corroborate a match.
/// <para>
/// Token-presence (order-free, subset) is deliberately more forgiving than a positional fuzzy ratio: a
/// "luie mannen hef" filename should corroborate a candidate whose artist is "Hef" and title "Luie
/// Mannen" regardless of the order they appear in the filename, and without committing to which side of
/// a dash is the artist. Matching is on whole normalized tokens (not substrings) so a short token like
/// "hef" only matches a standalone "hef", never the inside of another word.
/// </para>
/// </summary>
public static class CandidateTextMatch
{
    /// <summary>
    /// Fraction (0..1) of <paramref name="candidate"/>'s normalized tokens that also appear in
    /// <paramref name="haystack"/>. Returns 0 when either side has no usable tokens.
    /// </summary>
    public static double Containment(string? candidate, string? haystack)
    {
        var needle = Tokenize(candidate);
        if (needle.Count == 0)
            return 0;

        var hay = Tokenize(haystack);
        if (hay.Count == 0)
            return 0;

        var present = needle.Count(hay.Contains);
        return (double)present / needle.Count;
    }

    /// <summary>True when every token of <paramref name="candidate"/> appears in <paramref name="haystack"/>.</summary>
    public static bool AllPresent(string? candidate, string? haystack)
        => Containment(candidate, haystack) >= 1.0 - 1e-9;

    private static HashSet<string> Tokenize(string? value)
    {
        var normalized = TitleNormalizer.NormalizeForSearch(value);
        return string.IsNullOrWhiteSpace(normalized)
            ? []
            : normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
    }
}

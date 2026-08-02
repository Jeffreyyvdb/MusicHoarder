using System.Text.RegularExpressions;

namespace MusicHoarder.Api.Metadata;

public static partial class ArtistCreditNormalizer
{
    public static string? NormalizeDisplayCredit(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = MultiSpaceRegex().Replace(value.Trim(), " ");
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    public static string? GetPrimaryArtist(string? artistCredit)
    {
        var normalized = NormalizeDisplayCredit(artistCredit);
        if (normalized is null)
        {
            return null;
        }

        var candidates = SplitArtists(normalized);
        return candidates.Count > 0 ? candidates[0] : normalized;
    }

    /// <summary>
    /// Splits a display credit into its constituent artists using the first matching delimiter
    /// (";" → feat./ft./featuring/with → " &amp; " → " x " → ", "). Callers must treat the result as
    /// a guess for single-name credits containing a legitimate delimiter ("Earth, Wind &amp; Fire").
    /// </summary>
    public static List<string> SplitArtists(string artistCredit)
    {
        if (artistCredit.Contains(';'))
        {
            return artistCredit.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }

        var featSplit = FeatRegex().Split(artistCredit);
        if (featSplit.Length > 1)
        {
            return featSplit.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        }

        if (artistCredit.Contains(" & ", StringComparison.OrdinalIgnoreCase))
        {
            return artistCredit.Split(" & ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }

        if (artistCredit.Contains(" x ", StringComparison.OrdinalIgnoreCase))
        {
            return artistCredit.Split(" x ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }

        if (artistCredit.Contains(", "))
        {
            return artistCredit.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }

        return [artistCredit];
    }

    /// <summary>True when the credit contains an explicit featuring delimiter (feat./ft./featuring/
    /// with) — the one split form that is unambiguous, unlike "&amp;"/","-joined names.</summary>
    public static bool HasFeaturingDelimiter(string? value) =>
        !string.IsNullOrWhiteSpace(value) && FeatRegex().IsMatch(value);

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiSpaceRegex();

    // Dot optional: "Domo Genesis Ft Tyler The Creator" is a featuring credit too. Keeps this in
    // step with TitleNormalizer.FeaturingPattern, which already strips the dotless form — without
    // that parity a dotless credit normalizes to the primary artist's key while refusing to split,
    // and the artist-dedup view would offer a merge that deletes the featuring clause.
    [GeneratedRegex(@"\s+(feat\.?|ft\.?|featuring|with)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex FeatRegex();
}

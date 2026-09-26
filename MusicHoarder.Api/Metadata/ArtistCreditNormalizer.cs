using System.Text.RegularExpressions;
using MusicHoarder.Api.Matching;

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
    /// (";" → feat./ft./featuring/with, bare or bracketed → " &amp; " → " x " → ", "). Callers must
    /// treat the result as a guess for single-name credits containing a legitimate delimiter
    /// ("Earth, Wind &amp; Fire").
    /// </summary>
    public static List<string> SplitArtists(string artistCredit)
    {
        if (artistCredit.Contains(';'))
        {
            return artistCredit.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }

        // "Nas (featuring AZ)" / "Nas [feat. AZ]" read as the bare form. TitleNormalizer strips the
        // bracketed clause from the search key, so without this a bracketed credit keyed as its
        // primary artist while refusing to split — and the artist-dedup view offered it as a spelling.
        var unbracketed = BracketedFeatRegex().Replace(artistCredit, " feat. ${guests}").Trim();
        var featSplit = FeatRegex().Split(unbracketed);
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
    /// with, bare or bracketed) — the one split form that is unambiguous, unlike "&amp;"/","-joined
    /// names.</summary>
    public static bool HasFeaturingDelimiter(string? value) =>
        !string.IsNullOrWhiteSpace(value) && (FeatRegex().IsMatch(value) || BracketedFeatRegex().IsMatch(value));

    /// <summary>
    /// Splits on the joiners <see cref="SplitArtists"/> deliberately ignores because they are as often
    /// part of one name as a join between two ("Florence + the Machine", "Simon and Garfunkel", the
    /// Dutch "met"/"en"). DETECTION ONLY: a caller must corroborate every part (each exists as a
    /// standalone artist) before treating the credit as several artists, and must never route,
    /// group or key on this. Returns the single credit when no such joiner occurs.
    /// </summary>
    public static List<string> SplitOnAmbiguousJoiners(string artistCredit)
    {
        var parts = AmbiguousJoinerRegex().Split(artistCredit)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();
        return parts.Count > 1 ? parts : [artistCredit];
    }

    /// <summary>
    /// True when <paramref name="credit"/> is the joined display form of a multi-artist discrete list:
    /// at least two parts, each occurring in order as a whole-word run of the credit's artist key.
    /// This is how a provider's join phrase — "Hef met Jayh", "2Pac + Outlawz", "50 Cent and
    /// Olivia" — is recognized as several artists without knowing its language: the discrete list
    /// (MusicBrainz's own artist-credit split) already says so.
    /// </summary>
    public static bool IsJoinedCreditOf(string? credit, IReadOnlyList<string> discreteArtists)
    {
        if (discreteArtists.Count < 2)
            return false;

        var key = TitleNormalizer.NormalizeArtistKey(credit);
        if (key.Length == 0)
            return false;

        var padded = $" {key} ";
        var from = 0;
        foreach (var part in discreteArtists)
        {
            var partKey = TitleNormalizer.NormalizeArtistKey(part);
            if (partKey.Length == 0)
                return false;
            var at = padded.IndexOf($" {partKey} ", from, StringComparison.Ordinal);
            if (at < 0)
                return false;
            // Resume on the trailing space so the next part may start right after this one.
            from = at + partKey.Length + 1;
        }
        return true;
    }

    /// <summary>
    /// The lead artist when <paramref name="albumArtist"/> is the joined credit of the first two or
    /// more entries of <paramref name="discreteArtists"/> under a join phrase <see cref="SplitArtists"/>
    /// does not split — the value the old MusicBrainz mapping wrote ("Hef met Jayh" over Hef; Jayh).
    /// A prefix counts, so "2Pac + Outlawz" over 2Pac; Outlawz; Big Syke (the "feat. Big Syke" track
    /// of that album) resolves to the same lead as its siblings instead of stranding one track in the
    /// collab's folder. Null for anything else, including every credit SplitArtists already splits.
    /// </summary>
    public static string? LeadOfJoinedCredit(string? albumArtist, IReadOnlyList<string> discreteArtists)
    {
        if (string.IsNullOrWhiteSpace(albumArtist) || SplitArtists(albumArtist).Count != 1)
            return null;

        var parts = discreteArtists.Where(p => !IsDelimiterToken(p)).ToList();
        for (var take = parts.Count; take >= 2; take--)
        {
            if (IsJoinedCreditOf(albumArtist, parts.Take(take).ToList()))
                return parts[0];
        }
        return null;
    }

    /// <summary>True for a list segment that is a featuring delimiter on its own ("feat.", "ft",
    /// "featuring", "with") — the phantom artist an old capturing split wrote into ARTISTS.</summary>
    public static bool IsDelimiterToken(string? segment) =>
        !string.IsNullOrWhiteSpace(segment) && DelimiterTokenRegex().IsMatch(segment.Trim());

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiSpaceRegex();

    // Dot optional: "Domo Genesis Ft Tyler The Creator" is a featuring credit too. Keeps this in
    // step with TitleNormalizer.FeaturingPattern, which already strips the dotless form — without
    // that parity a dotless credit normalizes to the primary artist's key while refusing to split,
    // and the artist-dedup view would offer a merge that deletes the featuring clause. The group
    // must stay NON-capturing: Regex.Split returns captured groups as elements, which is how
    // "feat." itself ended up written into the ARTISTS tag as an artist.
    [GeneratedRegex(@"\s+(?:feat\.?|ft\.?|featuring|with)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex FeatRegex();

    // "(feat. X)", "[featuring X]", "(ft X)", "(with X)" — the clause TitleNormalizer's
    // ParenthesesPattern/BracketsPattern strip from the search key.
    [GeneratedRegex(@"\s*[\(\[]\s*(?:feat\.?|ft\.?|featuring|with)\s+(?<guests>[^\)\]]+?)\s*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex BracketedFeatRegex();

    [GeneratedRegex(@"\s+(?:\+|and|met|en|vs\.?)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex AmbiguousJoinerRegex();

    [GeneratedRegex(@"^(?:feat\.?|ft\.?|featuring|with)$", RegexOptions.IgnoreCase)]
    private static partial Regex DelimiterTokenRegex();
}

using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Metadata;

namespace MusicHoarder.Api.Enrichment.Providers;

/// <summary>
/// The artist gate every community-tracker source shares. A tracker is a single-artist database, so
/// anything it answers for an unrelated artist is a false match — and because a tracker hit also
/// drives release classification, a wrong one mislabels the song unreleased too.
/// <para>
/// Lives on its own rather than inside <see cref="CommunityTrackerEnrichmentProvider"/> because the
/// tracklist providers need the identical gate without sharing that base class. One implementation
/// only: a second copy would silently miss fixes like the short-entry rule below.
/// </para>
/// </summary>
public static class TrackerArtistAllowlist
{
    /// <summary>
    /// Allowlist entries whose normalized form is shorter than this are matched by exact equality
    /// only, never fuzzily. <see cref="FuzzyTextMatch.Ratio"/> is a weighted ratio: when one side is
    /// much shorter than the other it falls back to a 0.9-scaled partial ratio, so a two-letter
    /// entry like "Ye" scores 90 — above the identity threshold (85) — against <i>any</i> artist
    /// whose name merely contains those letters. That opened the Kanye tracker for "Yeat", "Yebba",
    /// "Yeule" and "Yeah Yeah Yeahs". Substring containment carries no signal at that length, so
    /// short entries must match a credited artist outright.
    /// </summary>
    public const int MinFuzzyEntryLength = 5;

    /// <summary>Whether an artist credit is covered by a tracker's allowlist.</summary>
    /// <param name="artist">The song's resolved artist credit, possibly a collaboration.</param>
    /// <param name="allowlist">Names/aliases the tracker covers.</param>
    /// <param name="identityArtistThreshold">Fuzzy ratio (0–100) a long entry must clear.</param>
    public static bool Matches(string? artist, IReadOnlyList<string> allowlist, double identityArtistThreshold)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return false;

        // Compare the whole credit AND each credited artist: a collaboration ("Ye, Ty Dolla $ign")
        // must still open the gate for the tracker's artist, which the exact-equality rule below
        // would otherwise reject.
        var credits = new List<string> { artist! };
        credits.AddRange(ArtistCreditNormalizer.SplitArtists(artist!));

        foreach (var allowed in allowlist)
        {
            var allowedKey = Key(allowed);
            if (allowedKey.Length == 0)
                continue;

            foreach (var credit in credits)
            {
                var matched = allowedKey.Length < MinFuzzyEntryLength
                    ? string.Equals(Key(credit), allowedKey, StringComparison.Ordinal)
                    : FuzzyTextMatch.Ratio(credit, allowed) is double ratio && ratio >= identityArtistThreshold;

                if (matched)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Comparison key for exact allowlist matching. Mirrors <see cref="FuzzyTextMatch.Ratio"/>'s
    /// fallback so a symbol-only alias ("¥$") — which normalizes away entirely — still compares on
    /// its casefolded raw text instead of collapsing to the empty string and matching everything.
    /// </summary>
    private static string Key(string? value)
    {
        var normalized = TitleNormalizer.NormalizeForSearch(value);
        return normalized.Length > 0 ? normalized : value?.Trim().ToLowerInvariant() ?? string.Empty;
    }
}

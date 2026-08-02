using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment;

/// <summary>
/// Whether a song is a commercially released recording or an unreleased one (a leak, snippet,
/// demo, stem or session file). Derived — see <see cref="ReleaseClassifier"/>.
/// </summary>
public enum ReleaseClassification
{
    /// <summary>Nothing in the row says either way (unmatched, or matched without release evidence).</summary>
    Unknown = 0,
    /// <summary>Present in a commercial catalog (Spotify/Deezer/Apple Music/MusicBrainz, or an ISRC).</summary>
    Released = 1,
    /// <summary>Catalogued by a community tracker as unreleased material.</summary>
    Unreleased = 2,
    /// <summary>
    /// Every enabled provider ran and not one of them produced a single candidate — no catalog
    /// anywhere has heard of this recording. Strong circumstantial evidence of unreleased material,
    /// but weaker than <see cref="Unreleased"/>: a garbage-tagged file or a genuinely obscure
    /// release lands here too.
    /// </summary>
    LikelyUnreleased = 3,
}

/// <summary>
/// Derives a song's release status from evidence the enrichment pipeline already persists — no new
/// column, so it classifies historic rows too.
/// <para>
/// The strong signal is the community trackers (<c>Tracker</c> / <c>YeTracker</c>): they exist to
/// catalog leaks, and every match carries the tracker's own curation as a
/// <c>category:&lt;name&gt;</c> entry in <see cref="Persistence.SongMetadata.MatchWarnings"/> (see
/// <see cref="Providers.CommunityTrackerEnrichmentProvider"/>). Only a handful of those categories
/// mean "this is the commercially released version"; the rest — <c>unreleased</c>, <c>stems</c>,
/// <c>ssc</c>, <c>misc</c>, <c>recent</c> — do not.
/// </para>
/// <para>
/// Absent a tracker match, a commercial identifier (ISRC or Spotify id) or a match from a
/// commercial catalog provider means released.
/// </para>
/// <para>
/// The weaker inference is <see cref="ReleaseClassification.LikelyUnreleased"/>: nobody found
/// anything at all. That is a narrower state than it sounds — see
/// <see cref="LookedEverywhereAndFoundNothing"/> for why it excludes the noisy failure modes.
/// </para>
/// </summary>
public static class ReleaseClassifier
{
    private const string CategoryPrefix = "category:";

    /// <summary>
    /// Tracker categories that describe commercially released material. Everything else a tracker
    /// catalogs is unreleased, so this list is the exception set rather than the other way around.
    /// </summary>
    private static readonly string[] ReleasedCategories = ["released", "album copies", "album copy"];

    /// <summary>Provider names (as written to <c>MatchedBy</c>) that only index released catalogs.</summary>
    private static readonly string[] CommercialProviders = ["SpotifyAPI", "Deezer", "AppleMusic", "MusicBrainzWeb"];

    /// <summary>
    /// Classifies a song. <paramref name="matchWarnings"/> is the deserialized
    /// <c>MatchWarnings</c> array; <paramref name="isUnreleasedFlag"/> is the stored
    /// <see cref="Persistence.SongMetadata.IsUnreleased"/> flag, which a sync peer may already have
    /// decided for us.
    /// </summary>
    public static ReleaseClassification Classify(
        bool isUnreleasedFlag,
        EnrichmentStatus enrichmentStatus,
        string? matchedBy,
        IReadOnlyList<string>? matchWarnings,
        string? isrc,
        string? spotifyId)
    {
        // A peer that already routed this to its "Unreleased" folder outranks our own inference.
        if (isUnreleasedFlag)
            return ReleaseClassification.Unreleased;

        if (IsTrackerMatch(matchedBy))
        {
            var category = FindCategory(matchWarnings);
            if (category is null)
                return ReleaseClassification.Unreleased; // a tracker hit with no category is still tracker material
            return ReleasedCategories.Contains(category, StringComparer.OrdinalIgnoreCase)
                ? ReleaseClassification.Released
                : ReleaseClassification.Unreleased;
        }

        if (!string.IsNullOrWhiteSpace(isrc) || !string.IsNullOrWhiteSpace(spotifyId))
            return ReleaseClassification.Released;

        if (!string.IsNullOrWhiteSpace(matchedBy)
            && CommercialProviders.Any(p => matchedBy.Contains(p, StringComparison.OrdinalIgnoreCase)))
        {
            return ReleaseClassification.Released;
        }

        if (LookedEverywhereAndFoundNothing(enrichmentStatus, matchedBy))
            return ReleaseClassification.LikelyUnreleased;

        return ReleaseClassification.Unknown;
    }

    /// <summary>
    /// True for the one state that means "every enabled provider ran and none of them produced a
    /// single candidate". <see cref="ConsensusEvaluator"/> reaches it only through a narrow path,
    /// which is what makes the inference usable:
    /// <list type="bullet">
    /// <item>Every enabled provider must have written a <i>terminal</i> attempt, so a provider that
    /// couldn't handle the song (no fingerprint → AcoustID skipped) leaves the row <c>Pending</c>
    /// instead.</item>
    /// <item>A provider that <i>errored</i> makes the row <c>Failed</c>, not <c>NeedsReview</c> —
    /// so outages and misconfiguration don't land here.</item>
    /// <item>A rate-limited provider defers the verdict while the limit is fresh.</item>
    /// <item><c>MatchedBy</c> stays null only when the candidate list was empty. A song that got
    /// candidates but couldn't clear the confidence bar — including the mis-tagged downloads whose
    /// blocking artist/title mismatches fill review — records a best candidate and is excluded.</item>
    /// </list>
    /// What still slips through: a file tagged so badly that name-based lookup finds nothing at all,
    /// and a genuinely obscure release that none of the four catalogs carry. Hence
    /// <see cref="ReleaseClassification.LikelyUnreleased"/> rather than
    /// <see cref="ReleaseClassification.Unreleased"/>.
    /// </summary>
    private static bool LookedEverywhereAndFoundNothing(EnrichmentStatus status, string? matchedBy)
        => status == EnrichmentStatus.NeedsReview && string.IsNullOrWhiteSpace(matchedBy);

    /// <summary>
    /// True when the winning provider was a community tracker. Matched by substring because
    /// instance sync rewrites the field to <c>"&lt;provider&gt;+sync"</c>.
    /// </summary>
    private static bool IsTrackerMatch(string? matchedBy)
        => !string.IsNullOrWhiteSpace(matchedBy)
            && matchedBy.Contains("Tracker", StringComparison.OrdinalIgnoreCase);

    private static string? FindCategory(IReadOnlyList<string>? warnings)
    {
        if (warnings is null)
            return null;

        foreach (var warning in warnings)
        {
            if (warning is not null && warning.StartsWith(CategoryPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var value = warning[CategoryPrefix.Length..].Trim();
                if (value.Length > 0)
                    return value;
            }
        }

        return null;
    }
}

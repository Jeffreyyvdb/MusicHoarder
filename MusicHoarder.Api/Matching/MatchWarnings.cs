namespace MusicHoarder.Api.Matching;

/// <summary>
/// The shared vocabulary of provider match warnings and the subset that <b>blocks</b> a
/// promotion to <see cref="Persistence.EnrichmentStatus.Matched"/>. A blocking warning means the
/// candidate contradicts the file's own identity (artist/title/version/duration/ISRC) or has no
/// usable signal to verify it — so even a high score should not auto-match.
/// <para>
/// Centralised here so the mainstream providers (MusicBrainzWeb, Spotify, Deezer, Apple Music) and
/// the cross-provider <see cref="Enrichment.ConsensusEvaluator"/> share one definition and can't
/// drift apart. (The community trackers gate on a slightly different set — they search title-only —
/// so they keep their own list.)
/// </para>
/// </summary>
public static class MatchWarnings
{
    /// <summary>
    /// Non-blocking marker: the candidate was validated against an identity <b>guessed from the file
    /// path</b> (folder/filename), not an embedded tag. A path guess can corroborate or boost a match
    /// but never <i>contradict</i> the file's identity, so this is deliberately not in
    /// <see cref="IsBlocking"/>. The <see cref="Enrichment.ConsensusEvaluator"/> treats it as "needs a
    /// second provider to agree before this can auto-match" — see its solo decision paths.
    /// </summary>
    public const string IdentityUnverified = "identity_unverified";

    /// <summary>Warnings that contradict the file's identity and must keep a candidate out of Matched.</summary>
    public static bool IsBlocking(string warning) => warning is
        "artist_mismatch"
        or "title_mismatch"
        or "version_mismatch"
        or "duration_mismatch"
        or "isrc_mismatch"
        or "artist_unknown";

    /// <summary>True when any warning in the set is blocking.</summary>
    public static bool AnyBlocking(IEnumerable<string> warnings) => warnings.Any(IsBlocking);
}

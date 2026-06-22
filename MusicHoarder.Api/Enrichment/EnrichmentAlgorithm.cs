namespace MusicHoarder.Api.Enrichment;

/// <summary>
/// Versions the enrichment matching algorithm so a meaningful change to how songs are scored/matched
/// can automatically re-process the rows that were left in <see cref="Persistence.EnrichmentStatus.NeedsReview"/>
/// or <see cref="Persistence.EnrichmentStatus.Failed"/> under an older version.
/// <para>
/// Each row records the version it was last processed under
/// (<see cref="Persistence.SongMetadata.LastEnrichmentAlgorithmVersion"/>); the orchestrator stamps the
/// current version whenever it writes a terminal status, and the startup sweep
/// (<c>EnqueueAlgorithmStaleSongsAsync</c>) re-enriches review/failed rows whose stamp is behind
/// <see cref="CurrentVersion"/>. This makes the re-process <b>idempotent</b> — it fires once per bump,
/// not on every restart — unlike a one-shot marker-gated backfill.
/// </para>
/// <para>
/// <b>Bump <see cref="CurrentVersion"/> whenever a change to the matching/scoring logic should heal the
/// existing review/failed backlog.</b> A bump spends provider API quota re-enriching those rows, so only
/// bump for changes that plausibly move stuck songs (not pure refactors).
/// </para>
/// </summary>
public static class EnrichmentAlgorithm
{
    /// <summary>
    /// Current enrichment-algorithm version.
    /// <list type="bullet">
    /// <item>1 — provenance-aware, free-text untagged matching: path-derived identities no longer emit
    /// blocking warnings; untagged files query on the cleaned filename free-text and are corroborated by
    /// token-presence (identity_unverified) requiring a second provider to auto-match.</item>
    /// <item>2 — download-origin duration relaxation: a lone <c>duration_mismatch</c> no longer blocks a
    /// strongly-corroborated wishlist / Spotify-Like download (AcoustID fingerprint or ≥2 providers
    /// sharing an ISRC). Heals the YouTube-rip backlog that piled up in NeedsReview despite a correct,
    /// multi-provider match — see <see cref="Options.MusicEnricherOptions.RelaxDownloadDurationMismatch"/>.</item>
    /// <item>3 — embedded-ISRC duration confirmation + filename-title fixes: an exact match between the
    /// file's own ISRC tag and a candidate's ISRC downgrades <c>duration_mismatch</c> to advisory, so a
    /// solo ISRC-confirmed catalog hit (e.g. a long 101Barz studiosessie rip Deezer carries under the
    /// same ISRC) auto-matches instead of stalling in NeedsReview. Also a leading title that is itself a
    /// number ("999 (Triple 9)") is no longer shredded as a track number, sharpening tracker/free-text
    /// queries for the leak backlog.</item>
    /// </list>
    /// </summary>
    public const int CurrentVersion = 3;
}

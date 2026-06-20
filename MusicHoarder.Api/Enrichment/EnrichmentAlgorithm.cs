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
    /// </list>
    /// </summary>
    public const int CurrentVersion = 2;
}

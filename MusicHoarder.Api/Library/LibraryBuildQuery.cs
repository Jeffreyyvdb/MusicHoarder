using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Library;

/// <summary>
/// The single source of truth for "which songs should the library builder act on". Both the builder's
/// batch query (<see cref="LibraryBuilderService"/>) and the pending-work poll
/// (<see cref="LibraryBuilderBackgroundService"/>) compose it, so they can never diverge — a divergence
/// would make the poll see work the batch query skips and busy-loop.
/// </summary>
public static class LibraryBuildQuery
{
    /// <summary>
    /// Real (non-synthetic, non-deleted, non-duplicate) Matched tracks the builder should process:
    /// anything not yet <see cref="LibraryBuildStatus.Done"/>, plus Done rows flagged for a forced
    /// re-tag/relocate (a non-null <see cref="SongMetadata.PreviousDestinationPath"/> or a missing
    /// destination). When <paramref name="lyricsWaitCutoff"/> is non-null, a *fresh* build additionally
    /// waits for its lyrics fetch to resolve before tagging — enrichment commits <c>Matched</c> before the
    /// LRCLIB fetch returns, so without the wait the file is tagged with no lyrics. The wait is bounded by
    /// <see cref="SongMetadata.EnrichedAtUtc"/> against the cutoff so a track whose lyrics never arrive
    /// (e.g. a manual approval, which doesn't fetch lyrics) still builds; forced re-tags and tracks that
    /// can't be searched (no title/artist) never wait.
    /// </summary>
    public static IQueryable<SongMetadata> BuildCandidates(
        IQueryable<SongMetadata> songs, DateTime? lyricsWaitCutoff, int maxBuildAttempts,
        bool buildNeedsReview = false)
    {
        var query = songs
            .Where(s => s.DeletedAtUtc == null && !s.IsSynthetic)
            // Demo songs stream straight off their read-only mount (DestinationPath == SourcePath);
            // building one would copy it into the owner's library and try to prune the mount path.
            .ExcludingDemoTenant()
            .Where(s => !s.IsDuplicate)
            // Normally only strict Matched tracks build. With MusicEnricher:EnableBuildNeedsReview on,
            // uncertain NeedsReview tracks build too (flagged GROUPING="Needs Review" by the tag writer)
            // so they still appear in the library; approving one later re-tags it and clears the flag.
            .Where(s => s.EnrichmentStatus == EnrichmentStatus.Matched
                || (buildNeedsReview && s.EnrichmentStatus == EnrichmentStatus.NeedsReview))
            .Where(s => s.LibraryBuildStatus != LibraryBuildStatus.Done
                || s.DestinationPath == null
                || s.PreviousDestinationPath != null)
            // Quarantine a track that's failed too many times so a persistently un-writable file can't
            // loop the builder forever (issue #239). Only Failed rows carry a non-zero counter; success
            // and any reset/requeue zero it, so a manual re-build always gets another run.
            .Where(s => s.LibraryBuildStatus != LibraryBuildStatus.Failed
                || s.LibraryBuildAttempts < maxBuildAttempts);

        if (lyricsWaitCutoff is { } cutoff)
        {
            query = query.Where(s =>
                s.LyricsStatus != LyricsStatus.NotFetched   // lyrics resolved (Fetched/Instrumental/NotFound/Failed)
                || s.PreviousDestinationPath != null        // forced re-tag — never wait
                || string.IsNullOrEmpty(s.Title)            // can't fetch lyrics anyway
                || string.IsNullOrEmpty(s.Artist)
                || s.EnrichedAtUtc == null                  // no match timestamp — don't hold
                || s.EnrichedAtUtc < cutoff);               // waited long enough; build with whatever lyrics exist
        }

        return query;
    }

    /// <summary>
    /// The cutoff a fresh build's <see cref="SongMetadata.EnrichedAtUtc"/> must predate to skip the lyrics
    /// wait, or null when the gate is disabled (<see cref="MusicEnricherOptions.LyricsBeforeBuildWaitMinutes"/> == 0).
    /// </summary>
    public static DateTime? LyricsWaitCutoff(MusicEnricherOptions options)
        => options.LyricsBeforeBuildWaitMinutes > 0
            ? DateTime.UtcNow - TimeSpan.FromMinutes(options.LyricsBeforeBuildWaitMinutes)
            : null;
}

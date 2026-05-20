using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment;

internal static class EnrichmentQueries
{
    /// <summary>
    /// Filters songs to those eligible for enrichment: not deleted, pending status,
    /// and having at least one set of metadata that a provider can act on.
    /// </summary>
    public static IQueryable<SongMetadata> WhereReadyForEnrichment(this IQueryable<SongMetadata> query)
    {
        return query
            .Where(s => s.DeletedAtUtc == null)
            .Where(s => s.EnrichmentStatus == EnrichmentStatus.Pending)
            .Where(s =>
                (!string.IsNullOrWhiteSpace(s.Fingerprint) && s.DurationSeconds != null)
                || (!string.IsNullOrWhiteSpace(s.Artist) && !string.IsNullOrWhiteSpace(s.Title))
                || !string.IsNullOrWhiteSpace(s.Isrc));
    }

    /// <summary>
    /// Returns IDs of songs with a retryable provider attempt: either a rate-limited attempt
    /// whose <see cref="SongProviderAttempt.RetryAfterUtc"/> has elapsed, or a terminal
    /// NoMatch/Failed attempt whose cooldown (<see cref="SongProviderAttempt.NextRetryAfterUtc"/>)
    /// has elapsed. Manually-approved (locked) songs are excluded.
    /// </summary>
    public static IQueryable<int> WhereRetryableProviderAttempts(this IQueryable<SongProviderAttempt> query, DateTime now)
    {
        return query
            .Where(a => !a.Song.IsManuallyApproved)
            .Where(a =>
                (a.Status == ProviderAttemptStatus.RateLimited && (a.RetryAfterUtc == null || a.RetryAfterUtc <= now))
                || ((a.Status == ProviderAttemptStatus.NoMatch || a.Status == ProviderAttemptStatus.Failed)
                    && a.NextRetryAfterUtc != null && a.NextRetryAfterUtc <= now))
            .Select(a => a.SongId)
            .Distinct();
    }
}

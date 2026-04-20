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
    /// Returns IDs of songs that have at least one <see cref="ProviderAttemptStatus.RateLimited"/>
    /// attempt whose <see cref="SongProviderAttempt.RetryAfterUtc"/> has elapsed.
    /// </summary>
    public static IQueryable<int> WhereRetryableProviderAttempts(this IQueryable<SongProviderAttempt> query, DateTime now)
    {
        return query
            .Where(a => a.Status == ProviderAttemptStatus.RateLimited)
            .Where(a => a.RetryAfterUtc == null || a.RetryAfterUtc <= now)
            .Select(a => a.SongId)
            .Distinct();
    }
}

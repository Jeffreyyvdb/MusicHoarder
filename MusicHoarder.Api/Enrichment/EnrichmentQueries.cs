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
                (s.Fingerprint != null && s.Fingerprint != string.Empty && s.DurationSeconds != null)
                || (s.Artist != null && s.Artist != string.Empty && s.Title != null && s.Title != string.Empty)
                || (s.Isrc != null && s.Isrc != string.Empty));
    }
}

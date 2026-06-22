using System.Linq.Expressions;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment;

internal static class EnrichmentQueries
{
    /// <summary>
    /// A song has at least one set of metadata a provider can act on: a usable
    /// fingerprint+duration, a name (artist+title), or an ISRC.
    /// </summary>
    private static readonly Expression<Func<SongMetadata, bool>> IsEnrichable = s =>
        (!string.IsNullOrWhiteSpace(s.Fingerprint) && s.DurationSeconds != null)
        || (!string.IsNullOrWhiteSpace(s.Artist) && !string.IsNullOrWhiteSpace(s.Title))
        || !string.IsNullOrWhiteSpace(s.Isrc);

    /// <summary>
    /// Filters songs to those eligible for enrichment: not deleted, pending status,
    /// and having at least one set of metadata that a provider can act on.
    /// </summary>
    public static IQueryable<SongMetadata> WhereReadyForEnrichment(this IQueryable<SongMetadata> query)
    {
        return query
            .Where(s => s.DeletedAtUtc == null)
            .Where(s => s.EnrichmentStatus == EnrichmentStatus.Pending)
            .Where(IsEnrichable);
    }

    /// <summary>
    /// Filters to enrichable, non-deleted, non-synthetic, non-manually-approved songs that are
    /// missing an attempt for at least one currently-enabled provider — i.e. a newly-added
    /// provider has never run against them. Used by the startup sweep that gives a freshly
    /// deployed provider a turn against existing songs (regardless of their current status).
    /// Relies on the one-attempt-per-provider invariant: counting only enabled-provider attempts
    /// and comparing to the enabled count detects a missing provider without being thrown off by
    /// attempts left behind by since-disabled providers.
    /// </summary>
    public static IQueryable<SongMetadata> WhereMissingEnabledProvider(
        this IQueryable<SongMetadata> query, IReadOnlyCollection<EnrichmentProvider> enabled)
    {
        var enabledCount = enabled.Count;
        return query
            .Where(s => s.DeletedAtUtc == null)
            .Where(s => !s.IsSynthetic)
            // Demo rows are seeded terminal-Matched with zero attempts, so without this they'd be
            // "missing every provider" and get re-enriched (overwriting the curated demo data).
            .ExcludingDemoTenant()
            .Where(s => !s.IsManuallyApproved)
            .Where(IsEnrichable)
            .Where(s => s.ProviderAttempts.Count(a => enabled.Contains(a.Provider)) < enabledCount);
    }

    /// <summary>
    /// Filters to songs eligible for an LRCLIB lyrics fetch that have not had one resolve yet —
    /// the DB-side mirror of <see cref="SongMetadata.IsReadyForLyricsFetch"/>: a terminally matched
    /// (or needs-review) song with a name but <see cref="LyricsStatus.NotFetched"/>. Excludes deleted,
    /// synthetic, and demo rows. Used by the backfill sweep that heals songs whose inline lyrics fetch
    /// never ran or was interrupted (so they reached <see cref="EnrichmentStatus.Matched"/> without lyrics).
    /// </summary>
    public static IQueryable<SongMetadata> WhereReadyForLyricsFetch(this IQueryable<SongMetadata> query)
    {
        return query
            .Where(s => s.DeletedAtUtc == null)
            .Where(s => !s.IsSynthetic)
            .ExcludingDemoTenant()
            .Where(s => s.EnrichmentStatus == EnrichmentStatus.Matched
                || s.EnrichmentStatus == EnrichmentStatus.NeedsReview)
            .Where(s => s.LyricsStatus == LyricsStatus.NotFetched)
            .Where(s => !string.IsNullOrWhiteSpace(s.Title))
            .Where(s => !string.IsNullOrWhiteSpace(s.Artist));
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

using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
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
    /// missing an attempt for at least one currently-enabled provider. This is a superset of the
    /// songs a provider still owes a turn: a provider that cannot handle a song (a community tracker
    /// outside its artist allowlist) never records an attempt on it, and that test is C#, so the
    /// startup sweeps check it on the rows this returns. Relies on the one-attempt-per-provider
    /// invariant: counting only enabled-provider attempts and comparing to the enabled count
    /// detects a missing provider without being thrown off by attempts left behind by
    /// since-disabled providers.
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
    /// Filters to songs whose lyrics outcome is worth asking LRCLIB about again and whose backoff has
    /// elapsed — the DB-side mirror of <see cref="SongMetadata.IsLyricsRecheckCandidate"/> combined with
    /// <see cref="SongMetadata.LyricsNextRecheckAfterUtc"/>. Covers the two states nothing else revisits:
    /// a song LRCLIB 404'd on (<see cref="LyricsStatus.NotFound"/>) and one that came back with unsynced
    /// lyrics only, both of which can gain an LRC later since LRCLIB is community-contributed. Failed
    /// fetches ride along. Excludes deleted, synthetic, and demo rows.
    ///
    /// A null <c>LyricsNextRecheckAfterUtc</c> counts as due: it means the song's lyrics resolved before
    /// this sweep existed, so the whole pre-existing library enters the backoff ladder on its first pass
    /// without needing a migration-time backfill. (Null also marks a *resolved* song — but those are not
    /// candidates in the first place, so it is unambiguous here.)
    /// </summary>
    public static IQueryable<SongMetadata> WhereReadyForLyricsRecheck(
        this IQueryable<SongMetadata> query, DateTime now)
    {
        return query
            .Where(s => s.LyricsNextRecheckAfterUtc == null || s.LyricsNextRecheckAfterUtc <= now)
            .Where(s => s.DeletedAtUtc == null)
            .Where(s => !s.IsSynthetic)
            .ExcludingDemoTenant()
            .Where(s => s.EnrichmentStatus == EnrichmentStatus.Matched
                || s.EnrichmentStatus == EnrichmentStatus.NeedsReview)
            .Where(s => !string.IsNullOrWhiteSpace(s.Title))
            .Where(s => !string.IsNullOrWhiteSpace(s.Artist))
            .Where(s => s.LyricsStatus == LyricsStatus.NotFound
                || s.LyricsStatus == LyricsStatus.Failed
                || (s.LyricsStatus == LyricsStatus.Fetched
                    && (s.SyncedLyrics == null || s.SyncedLyrics == string.Empty)));
    }

    /// <summary>
    /// Filters to retryable provider attempts for currently-enabled providers: either a rate-limited
    /// attempt whose <see cref="SongProviderAttempt.RetryAfterUtc"/> has elapsed, or a terminal
    /// NoMatch/Failed attempt whose cooldown (<see cref="SongProviderAttempt.NextRetryAfterUtc"/>)
    /// has elapsed. Deleted, synthetic, demo, and manually-approved (locked) songs are excluded.
    ///
    /// This is a superset of the attempts <see cref="IEnrichmentOrchestrator.ProcessSongAsync"/>
    /// will actually advance: an enabled provider that can no longer handle the song (for example a
    /// community tracker after the song's artist was healed or re-tagged off its allowlist) is
    /// skipped before its attempt is looked at, and that <see cref="IEnrichmentProvider.CanHandle"/>
    /// check is C#, so callers must narrow against
    /// <see cref="IEnrichmentOrchestrator.GetApplicableProvidersAsync"/> via
    /// <see cref="RetryableSongIdsAsync"/>.
    /// </summary>
    public static IQueryable<SongProviderAttempt> WhereRetryableProviderAttempts(
        this IQueryable<SongProviderAttempt> query,
        DateTime now,
        IReadOnlyCollection<EnrichmentProvider> enabled)
    {
        return query
            .WhereRetriesApply(enabled)
            .Where(a =>
                (a.Status == ProviderAttemptStatus.RateLimited && (a.RetryAfterUtc == null || a.RetryAfterUtc <= now))
                || ((a.Status == ProviderAttemptStatus.NoMatch || a.Status == ProviderAttemptStatus.Failed)
                    && a.NextRetryAfterUtc != null && a.NextRetryAfterUtc <= now));
    }

    /// <summary>
    /// The attempts a retry may ever touch: for currently-enabled providers, on songs that are live, real,
    /// not the demo tenant's and not locked by a manual approval.
    /// </summary>
    public static IQueryable<SongProviderAttempt> WhereRetriesApply(
        this IQueryable<SongProviderAttempt> query, IReadOnlyCollection<EnrichmentProvider> enabled)
    {
        return query
            .Where(a => a.Song.DeletedAtUtc == null)
            .Where(a => !a.Song.IsSynthetic)
            .ExcludingDemoTenant()
            .Where(a => !a.Song.IsManuallyApproved)
            .Where(a => enabled.Contains(a.Provider));
    }

    /// <summary>
    /// Attempts with a retry on the calendar, due or not: rate-limited ones, and NoMatch/Failed ones that carry
    /// a cooldown. <see cref="WhereRetryableProviderAttempts"/> is the due subset.
    /// </summary>
    public static IQueryable<SongProviderAttempt> WhereAwaitingRetry(this IQueryable<SongProviderAttempt> query)
    {
        return query.Where(a =>
            a.Status == ProviderAttemptStatus.RateLimited
            || ((a.Status == ProviderAttemptStatus.NoMatch || a.Status == ProviderAttemptStatus.Failed)
                && a.NextRetryAfterUtc != null));
    }

    /// <summary>
    /// Ids of songs with at least one due retryable attempt from a currently-enabled provider that
    /// can still handle the song — the set <see cref="IEnrichmentOrchestrator.ProcessSongAsync"/>
    /// will actually advance. Pages by song id and reads only
    /// <see cref="IEnrichmentProvider.CanHandleColumns"/> so neither heavy text columns nor
    /// unadvanceable attempts (disabled providers, trackers whose allowlist no longer matches) are
    /// repeatedly enqueued.
    /// </summary>
    public static async Task<List<int>> RetryableSongIdsAsync(
        MusicHoarderDbContext db,
        IEnrichmentOrchestrator orchestrator,
        DateTime now,
        bool ignoreQueryFilters,
        CancellationToken ct,
        int pageSize = 1000)
    {
        var enabled = await orchestrator.GetEnabledProviderEnumsAsync(ct).ConfigureAwait(false);
        if (enabled.Count == 0)
            return [];

        var attempts = (ignoreQueryFilters
                ? db.SongProviderAttempts.IgnoreQueryFilters()
                : db.SongProviderAttempts)
            .AsNoTracking();
        var songsQuery = ignoreQueryFilters
            ? db.Songs.IgnoreQueryFilters()
            : db.Songs;

        var columns = IEnrichmentProvider.CanHandleColumns.With(nameof(SongMetadata.Id));
        var retryable = new List<int>();
        var afterId = 0;

        while (true)
        {
            var pageIds = await attempts
                .WhereRetryableProviderAttempts(now, enabled)
                .Where(a => a.SongId > afterId)
                .Select(a => a.SongId)
                .Distinct()
                .OrderBy(id => id)
                .Take(pageSize)
                .ToListAsync(ct);
            if (pageIds.Count == 0)
                return retryable;
            afterId = pageIds[^1];

            var dueBySong = (await attempts
                    .WhereRetryableProviderAttempts(now, enabled)
                    .Where(a => pageIds.Contains(a.SongId))
                    .Select(a => new { a.SongId, a.Provider })
                    .ToListAsync(ct))
                .ToLookup(a => a.SongId, a => a.Provider);

            var songs = await songsQuery
                .Where(s => pageIds.Contains(s.Id))
                .OrderBy(s => s.Id)
                .WithOnly(db, columns)
                .ToListAsync(ct);

            foreach (var song in songs)
            {
                var applicable = await orchestrator.GetApplicableProvidersAsync(song, ct).ConfigureAwait(false);
                // Against the sweep's own snapshot of enabled providers, which the queries above used.
                if (dueBySong[song.Id].Any(p => enabled.Contains(p) && applicable.Contains(p)))
                    retryable.Add(song.Id);
            }

            if (pageIds.Count < pageSize)
                return retryable;
        }
    }
}

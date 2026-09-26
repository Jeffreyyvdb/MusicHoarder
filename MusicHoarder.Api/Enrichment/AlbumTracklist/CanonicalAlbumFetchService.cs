using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment.AlbumTracklist;

/// <summary>
/// Background sweep that builds a reconciled canonical tracklist for each album once it lands in the
/// library (≥1 <see cref="EnrichmentStatus.Matched"/> song). For each album identity it runs every
/// enabled <see cref="IAlbumTracklistProvider"/> concurrently, keeps the candidates that are this album
/// (<see cref="CanonicalAlbumMatch"/>), reconciles them via <see cref="AlbumTracklistReconciler"/>, and
/// persists a <see cref="CanonicalAlbum"/> + its tracks so the album view can show every real track and
/// grey out the ones the user is missing.
/// </summary>
public sealed class CanonicalAlbumFetchService(
    IServiceScopeFactory scopeFactory,
    IEnumerable<IAlbumTracklistProvider> providers,
    IOptions<MusicEnricherOptions> options,
    ILogger<CanonicalAlbumFetchService> logger) : BackgroundService
{
    private readonly IReadOnlyList<IAlbumTracklistProvider> _providers = providers.ToList();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        if (!opts.EnableCanonicalAlbumFetch)
        {
            logger.LogInformation("Canonical-album fetch service disabled (EnableCanonicalAlbumFetch=false)");
            return;
        }

        logger.LogInformation(
            "Canonical-album fetch service started. BatchSize={BatchSize}, IdleDelay={IdleDelay}s",
            opts.CanonicalAlbumFetchBatchSize, opts.CanonicalAlbumFetchIdleDelaySeconds);

        var baseIdle = Math.Max(1, opts.CanonicalAlbumFetchIdleDelaySeconds);
        var maxIdle = Math.Max(baseIdle, opts.CanonicalAlbumFetchMaxIdleDelaySeconds);
        var currentIdle = baseIdle;

        while (!stoppingToken.IsCancellationRequested)
        {
            int fetched;
            try
            {
                fetched = await RunSweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Canonical-album fetch sweep failed");
                fetched = 0;
            }

            if (fetched > 0)
            {
                // Active: reset the backoff and process the next batch promptly.
                currentIdle = baseIdle;
                continue;
            }

            if (!await DelayIdleAsync(currentIdle, stoppingToken))
                break;

            // Nothing to do — back off (doubling, capped) so an idle library doesn't re-scan every 30s.
            currentIdle = Math.Min(maxIdle, currentIdle * 2);
        }
    }

    internal async Task<int> RunSweepAsync(CancellationToken ct)
    {
        var opts = options.Value;
        var now = DateTime.UtcNow;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        // Matched songs that name an album+artist (background service bypasses the per-user filter).
        // Demo rows are excluded so the read-only demo library never spawns canonical-album fetches
        // (which would also feed AlbumGradingBackgroundService).
        // Materialize before grouping — the EF in-memory provider can't translate GroupBy here.
        var songs = await db.Songs
            .IgnoreQueryFilters()
            .AsNoTracking()
            .ExcludingDemoTenant()
            .Where(s => s.DeletedAtUtc == null && !s.IsSynthetic
                && s.EnrichmentStatus == EnrichmentStatus.Matched
                && s.Album != null && s.Album != "")
            .Select(s => new SongHint(
                s.AlbumArtist, s.Artist, s.Album, s.Title, s.AcquisitionIntent, s.MusicBrainzReleaseId, s.SpotifyId, s.Isrc))
            .ToListAsync(ct);

        if (songs.Count == 0)
            return 0;

        var groups = songs
            .Select(s => (Hint: s, ArtistRaw: s.AlbumArtist ?? s.Artist, Album: s.Album!))
            .Where(x => !string.IsNullOrWhiteSpace(x.ArtistRaw))
            .GroupBy(x => (
                ArtistKey: TitleNormalizer.NormalizeForSearch(x.ArtistRaw),
                AlbumKey: TitleNormalizer.NormalizeForSearch(x.Album)))
            .Where(g => g.Key.ArtistKey.Length > 0 && g.Key.AlbumKey.Length > 0)
            .ToList();

        var keys = groups.Select(g => g.Key.ArtistKey).ToList();
        var existing = await db.CanonicalAlbums
            .Include(a => a.Tracks)
            .Where(a => keys.Contains(a.ArtistKey))
            .ToListAsync(ct);
        var existingByKey = existing.ToDictionary(a => (a.ArtistKey, a.AlbumKey));

        await RetireMismatchedAsync(db, groups, existingByKey, now, ct);

        var toFetch = groups
            .Where(g => NeedsFetch(existingByKey.GetValueOrDefault(g.Key), now))
            .Take(opts.CanonicalAlbumFetchBatchSize)
            .ToList();

        if (toFetch.Count == 0)
            return 0;

        logger.LogInformation("Canonical-album sweep: {Count} album(s) to reconcile", toFetch.Count);

        var enabledProviders = _providers.Where(p => p.IsEnabled(opts)).ToList();
        var fetched = 0;

        foreach (var group in toFetch)
        {
            ct.ThrowIfCancellationRequested();

            var members = group.ToList();
            var query = BuildQuery(members);
            var row = existingByKey.GetValueOrDefault(group.Key);

            var ownedTitles = OwnedTitles(members);
            var candidates = (await GatherCandidatesAsync(enabledProviders, query, ct))
                .Where(c => IsThisAlbum(query, ownedTitles, c))
                .ToList();
            var reconciled = AlbumTracklistReconciler.Reconcile(candidates);

            if (reconciled is null)
            {
                UpsertFailure(db, ref row, group.Key, CanonicalAlbumStatus.NotFound,
                    opts.CanonicalAlbumNotFoundRetryDays > 0 ? now.AddDays(opts.CanonicalAlbumNotFoundRetryDays) : null);
            }
            else
            {
                UpsertReconciled(db, ref row, group.Key, query, reconciled, now);
                fetched++;
            }

            await db.SaveChangesAsync(ct);
        }

        return fetched;
    }

    /// <summary>
    /// A provider's answer counts only when it is the album these songs are tagged with: a search
    /// always returns something, and for an album the catalog does not carry that something is another
    /// album (see <see cref="CanonicalAlbumMatch"/>).
    /// </summary>
    private bool IsThisAlbum(AlbumQuery query, IReadOnlyList<string?> ownedTitles, AlbumTracklistCandidate candidate)
    {
        var opts = options.Value;
        if (CanonicalAlbumMatch.IsSameAlbum(
                query.Album, query.AlbumArtist, ownedTitles,
                candidate.Title, candidate.AlbumArtist, candidate.Tracks.Select(t => t.Title),
                opts.IdentityTitleThreshold, opts.IdentityArtistThreshold))
            return true;

        logger.LogDebug(
            "Album tracklist provider {Provider} answered {Artist} - {Album} with a different album ({CandidateArtist} - {CandidateTitle}); ignoring it",
            candidate.Source, query.AlbumArtist, query.Album, candidate.AlbumArtist, candidate.Title);
        return false;
    }

    /// <summary>
    /// Rows fetched before answers had to prove themselves can still describe another album. Each is
    /// retired the way a fetch that found nothing is — NotFound on the retry timer — so no reader trusts
    /// it any more, and the retry asks the providers again under the rule. Its display fields stay:
    /// album-fill wishlist items, and the provenance that explains them, still point at the row.
    /// <para>
    /// Every track counts here, contested or not, because the fetch counts every track of each
    /// candidate: a stricter test would retire rows the next fetch rebuilds, every retry period. The
    /// split-album heal asks more of a row before it rewrites an album artist from it.
    /// </para>
    /// </summary>
    private async Task RetireMismatchedAsync(
        MusicHoarderDbContext db,
        IEnumerable<IGrouping<(string ArtistKey, string AlbumKey), (SongHint Hint, string? ArtistRaw, string Album)>> groups,
        IReadOnlyDictionary<(string ArtistKey, string AlbumKey), CanonicalAlbum> existingByKey,
        DateTime now,
        CancellationToken ct)
    {
        var opts = options.Value;
        var retired = 0;
        foreach (var group in groups)
        {
            CanonicalAlbum? row = existingByKey.GetValueOrDefault(group.Key);
            if (row is not { Status: CanonicalAlbumStatus.Fetched })
                continue;

            var query = BuildQuery(group.ToList());
            if (CanonicalAlbumMatch.IsSameAlbum(
                    query.Album, query.AlbumArtist, OwnedTitles(group),
                    row.DisplayTitle, row.DisplayArtist, row.Tracks.Select(t => t.Title),
                    opts.IdentityTitleThreshold, opts.IdentityArtistThreshold))
                continue;

            logger.LogInformation(
                "Retiring the canonical album for {Artist} - {Album}: it was {DisplayArtist} - {DisplayTitle}, a different album",
                query.AlbumArtist, query.Album, row.DisplayArtist, row.DisplayTitle);
            UpsertFailure(db, ref row, group.Key, CanonicalAlbumStatus.NotFound,
                opts.CanonicalAlbumNotFoundRetryDays > 0 ? now.AddDays(opts.CanonicalAlbumNotFoundRetryDays) : null);
            retired++;
        }

        if (retired > 0)
            await db.SaveChangesAsync(ct);
    }

    private async Task<IReadOnlyList<AlbumTracklistCandidate>> GatherCandidatesAsync(
        IReadOnlyList<IAlbumTracklistProvider> enabledProviders, AlbumQuery query, CancellationToken ct)
    {
        var tasks = enabledProviders.Select(async p =>
        {
            try
            {
                return await p.FetchAsync(query, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A provider being down/rate-limited shouldn't sink the whole album — just one fewer source.
                logger.LogDebug(ex, "Album tracklist provider {Provider} failed for {Artist} - {Album}",
                    p.Source, query.AlbumArtist, query.Album);
                return null;
            }
        });

        var results = await Task.WhenAll(tasks);
        return results.Where(c => c is not null).Select(c => c!).ToList();
    }

    // What a candidate's tracklist is checked against. Album-fill downloads are left out: they exist
    // because of a canonical album, so they cannot vouch for one (see CanonicalAlbumMatch).
    private static List<string?> OwnedTitles(IEnumerable<(SongHint Hint, string? ArtistRaw, string Album)> members) =>
        members
            .Where(m => m.Hint.Intent != SongAcquisitionIntent.AlbumFill)
            .Select(m => m.Hint.Title)
            .ToList();

    private static AlbumQuery BuildQuery(List<(SongHint Hint, string? ArtistRaw, string Album)> members)
    {
        var artist = members.Select(m => m.ArtistRaw).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a)) ?? "";
        var album = members[0].Album;
        var hints = members.Select(m => m.Hint).ToList();

        return new AlbumQuery(
            AlbumArtist: artist,
            Album: album,
            MusicBrainzReleaseId: hints.Select(h => h.MusicBrainzReleaseId).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)),
            SpotifyTrackId: hints.Select(h => h.SpotifyId).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)),
            Isrcs: hints.Select(h => h.Isrc).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).Distinct().ToList(),
            TotalTracksHint: null);
    }

    private static void UpsertReconciled(
        MusicHoarderDbContext db, ref CanonicalAlbum? existing, (string ArtistKey, string AlbumKey) key,
        AlbumQuery query, AlbumTracklistReconciler.ReconciledTracklist r, DateTime now)
    {
        var row = existing;
        if (row is null)
        {
            row = new CanonicalAlbum { ArtistKey = key.ArtistKey, AlbumKey = key.AlbumKey };
            db.CanonicalAlbums.Add(row);
            existing = row;
        }
        else
        {
            db.CanonicalAlbumTracks.RemoveRange(row.Tracks);
            row.Tracks.Clear();
        }

        row.DisplayTitle = r.Title ?? query.Album;
        row.DisplayArtist = r.AlbumArtist ?? query.AlbumArtist;
        row.Year = r.Year;
        row.CoverArtUrl = r.CoverArtUrl;
        row.ResolvedTrackCount = r.ResolvedTrackCount;
        row.TrackCountContested = r.TrackCountContested;
        row.SourcesJson = CanonicalAlbumSources.Serialize(r.Sources);
        row.Status = CanonicalAlbumStatus.Fetched;
        row.FetchedAtUtc = now;
        row.NextRetryAfterUtc = null;

        foreach (var t in r.Tracks)
        {
            row.Tracks.Add(new CanonicalAlbumTrack
            {
                DiscNumber = t.DiscNumber,
                TrackNumber = t.TrackNumber,
                Title = t.Title,
                DurationMs = t.DurationMs,
                MusicBrainzRecordingId = t.MusicBrainzRecordingId,
                CorroboratingProviders = t.CorroboratingProviders.Count > 0
                    ? string.Join(",", t.CorroboratingProviders)
                    : null,
                CorroborationCount = t.CorroboratingProviders.Count,
                IsContested = t.IsContested,
            });
        }
    }

    private static void UpsertFailure(
        MusicHoarderDbContext db, ref CanonicalAlbum? existing, (string ArtistKey, string AlbumKey) key,
        CanonicalAlbumStatus status, DateTime? nextRetry)
    {
        var row = existing;
        if (row is null)
        {
            row = new CanonicalAlbum { ArtistKey = key.ArtistKey, AlbumKey = key.AlbumKey };
            db.CanonicalAlbums.Add(row);
            existing = row;
        }

        row.Status = status;
        row.FetchedAtUtc = DateTime.UtcNow;
        row.NextRetryAfterUtc = nextRetry;
    }

    private static bool NeedsFetch(CanonicalAlbum? row, DateTime now)
    {
        if (row is null) return true;
        return row.Status switch
        {
            CanonicalAlbumStatus.Fetched => false,
            CanonicalAlbumStatus.Pending => true,
            _ => row.NextRetryAfterUtc is null || row.NextRetryAfterUtc <= now,
        };
    }

    private static async Task<bool> DelayIdleAsync(int seconds, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private sealed record SongHint(
        string? AlbumArtist,
        string? Artist,
        string? Album,
        string? Title,
        SongAcquisitionIntent Intent,
        string? MusicBrainzReleaseId,
        string? SpotifyId,
        string? Isrc);
}

using System.Text.Json;
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
/// enabled <see cref="IAlbumTracklistProvider"/> concurrently, reconciles the candidates via
/// <see cref="AlbumTracklistReconciler"/>, and persists a <see cref="CanonicalAlbum"/> + its tracks so
/// the album view can show every real track and grey out the ones the user is missing.
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

            if (fetched == 0 && !await DelayIdleAsync(opts.CanonicalAlbumFetchIdleDelaySeconds, stoppingToken))
                break;
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
            .Where(s => s.DeletedAtUtc == null && !s.IsSynthetic
                && s.OwnerUserId != WellKnownUsers.DemoId
                && s.EnrichmentStatus == EnrichmentStatus.Matched
                && s.Album != null && s.Album != "")
            .Select(s => new SongHint(s.AlbumArtist, s.Artist, s.Album, s.MusicBrainzReleaseId, s.SpotifyId, s.Isrc))
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

            var candidates = await GatherCandidatesAsync(enabledProviders, query, ct);
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
        row.SourcesJson = JsonSerializer.Serialize(r.Sources);
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
        string? AlbumArtist, string? Artist, string? Album, string? MusicBrainzReleaseId, string? SpotifyId, string? Isrc);
}

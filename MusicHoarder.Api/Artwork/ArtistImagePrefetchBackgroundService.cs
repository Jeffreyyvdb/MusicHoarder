using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Artwork;

/// <summary>
/// Drains the <see cref="IArtistImagePrefetcher"/> queue: one rate-limited Spotify → Deezer lookup
/// per queued artist, upserted into <c>ArtistImages</c>. HTTP requests only enqueue names here and
/// serve whatever is cached, so a slow or rate-limited provider can never hold up a page render —
/// portraits appear as the rows land.
///
/// <para>
/// Besides the queue it runs a periodic sweep over the <see cref="IKnownArtistIndex"/>: every
/// known artist whose cache row is missing or stale is enqueued, a bounded batch at a time, so a
/// fresh deploy warms the whole library's portraits without anyone visiting each artist.
/// </para>
/// </summary>
public sealed class ArtistImagePrefetchBackgroundService(
    IServiceScopeFactory scopes,
    IArtistImagePrefetcher prefetcher,
    IKnownArtistIndex knownArtists,
    IOptionsMonitor<MusicEnricherOptions> options,
    ILogger<ArtistImagePrefetchBackgroundService> logger) : BackgroundService
{
    /// <summary>Pause between two provider lookups — both providers rate-limit; this keeps the worker inside their budget.</summary>
    private static readonly TimeSpan FetchInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>How many stale/missing artists one sweep enqueues, so a big library trickles in instead of one giant burst.</summary>
    private const int SweepBatchSize = 200;

    /// <summary>How often the sweep looks for stale/missing rows again.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.CurrentValue.EnableArtistImages)
        {
            logger.LogInformation("Artist image prefetch disabled (EnableArtistImages=false)");
            return;
        }

        // Let startup (migrations, the first scan) settle before reading catalogs.
        if (!await DelayAsync(TimeSpan.FromSeconds(30), stoppingToken)) return;

        var lastSweep = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTime.UtcNow - lastSweep >= SweepInterval)
                {
                    lastSweep = DateTime.UtcNow;
                    await SweepAsync(stoppingToken);
                }

                var name = await prefetcher.ReadAsync(TimeSpan.FromSeconds(5), stoppingToken);
                if (name is null)
                    continue;

                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var service = scope.ServiceProvider.GetRequiredService<IArtistImageService>();
                    await service.RefreshAsync(name, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Artist image prefetch failed for {Artist}", name);
                }
                finally
                {
                    prefetcher.Release(name);
                }

                if (!await DelayAsync(FetchInterval, stoppingToken)) return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Artist image prefetch loop failed");
            }
        }
    }

    /// <summary>
    /// Reloads the known-artist index and enqueues every known artist whose row is missing or
    /// stale, a bounded batch per pass.
    /// </summary>
    private async Task SweepAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        await knownArtists.RefreshAsync(db, ct);

        var opts = options.CurrentValue;
        var now = DateTime.UtcNow;
        var rows = await db.ArtistImages.AsNoTracking()
            .Select(a => new { a.NormalizedName, a.DisplayName, a.ImageUrl, a.FetchedAtUtc })
            .ToListAsync(ct);

        var fresh = new HashSet<string>(StringComparer.Ordinal);
        var staleDisplayByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (ArtistImageService.IsStale(row.FetchedAtUtc, row.ImageUrl is not null, opts, now))
                staleDisplayByKey[row.NormalizedName] = row.DisplayName;
            else
                fresh.Add(row.NormalizedName);
        }

        var enqueued = 0;
        foreach (var pair in knownArtists.All)
        {
            if (enqueued >= SweepBatchSize) break;
            if (fresh.Contains(pair.Key)) continue;
            prefetcher.Enqueue(staleDisplayByKey.GetValueOrDefault(pair.Key) ?? pair.Value);
            enqueued++;
        }

        if (enqueued > 0)
            logger.LogInformation("Artist image sweep enqueued {Count} portraits (queue now {Pending})", enqueued, prefetcher.PendingCount);
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

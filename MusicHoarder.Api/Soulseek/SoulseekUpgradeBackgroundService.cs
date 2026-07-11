using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Soulseek;

/// <summary>
/// Drives the quality-upgrade lifecycle: consumes <see cref="SoulseekUpgradeChannel"/> for queued
/// requests (search → download → AwaitingIngest), runs the periodic merge sweep that finishes
/// requests once the pipeline has fingerprinted their downloads, and reclaims crash-stale
/// Searching/Downloading rows on startup. A plain hosted service — never a JobManager step.
/// </summary>
public class SoulseekUpgradeBackgroundService(
    IServiceScopeFactory scopeFactory,
    SoulseekUpgradeChannel channel,
    ILogger<SoulseekUpgradeBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan MergeSweepInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ResetStaleRequestsAsync(stoppingToken);
        await Task.WhenAll(RunWorkerAsync(stoppingToken), RunMergeSweepLoopAsync(stoppingToken));
    }

    /// <summary>
    /// Reclaims unfinished requests on startup. The work queue is an in-memory channel, so a restart
    /// loses whatever was enqueued: a <c>Queued</c> row was enqueued but never picked up, and a
    /// <c>Searching</c>/<c>Downloading</c> row was mid-flight when the process died. Reset the
    /// in-flight ones back to Queued and re-enqueue everything not yet terminal, so nothing is
    /// orphaned across a redeploy.
    /// </summary>
    private async Task ResetStaleRequestsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
            // IgnoreQueryFilters: background scope → the tenant filter resolves to Guid.Empty and
            // would hide every row. Upgrade requests are owner-only by construction.
            var stale = await db.UpgradeRequests
                .IgnoreQueryFilters()
                .Where(r => r.Status == UpgradeRequestStatus.Queued
                    || r.Status == UpgradeRequestStatus.Searching
                    || r.Status == UpgradeRequestStatus.Downloading)
                .ToListAsync(ct);
            if (stale.Count == 0)
                return;

            foreach (var request in stale.Where(r => r.Status != UpgradeRequestStatus.Queued))
            {
                request.Status = UpgradeRequestStatus.Queued;
                request.UpdatedAtUtc = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Re-enqueued {Count} unfinished upgrade request(s) on startup", stale.Count);

            foreach (var request in stale)
                channel.Enqueue(request.Id);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reset stale upgrade requests");
        }
    }

    private async Task RunWorkerAsync(CancellationToken ct)
    {
        await foreach (var requestId in channel.Reader.ReadAllAsync(ct))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<SoulseekUpgradeService>();
                await service.ProcessRequestAsync(requestId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Upgrade worker failed for request {RequestId}", requestId);
            }
        }
    }

    private async Task RunMergeSweepLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var merge = scope.ServiceProvider.GetRequiredService<UpgradeMergeService>();
                await merge.SweepAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Upgrade merge sweep failed");
            }

            try
            {
                await Task.Delay(MergeSweepInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

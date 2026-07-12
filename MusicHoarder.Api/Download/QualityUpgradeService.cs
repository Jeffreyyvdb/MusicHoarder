using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Audio;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Logging;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Download;

/// <summary>
/// Executes one queued quality-upgrade request: walk the configured provider chain (spotiflac, slskd,
/// …) in order, letting each <see cref="IUpgradeProvider"/> that could beat the target's current
/// quality search+download a strictly-better copy into the normal staging dir, stamp it with the
/// song's KNOWN identity (never the source's tags), and hand off to the pipeline as
/// <see cref="UpgradeRequestStatus.AwaitingIngest"/>. The scan→fingerprint stages then produce a
/// provisional row that <see cref="Soulseek.UpgradeMergeService"/> verifies (quality + same-recording
/// fingerprint) and swaps into the target.
/// </summary>
public class QualityUpgradeService(
    MusicHoarderDbContext db,
    IEnumerable<IUpgradeProvider> upgradeProviders,
    JobManager jobManager,
    IOwnerLookupService ownerLookup,
    IOptions<MusicEnricherOptions> enricherOptions,
    ILogger<QualityUpgradeService> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task ProcessRequestAsync(int requestId, CancellationToken ct)
    {
        // IgnoreQueryFilters: this runs in a background scope where the DbContext's tenant filter
        // resolves to Guid.Empty (no HTTP user), which would otherwise hide the owner's rows. Owner
        // scoping is applied explicitly below.
        var request = await db.UpgradeRequests
            .IgnoreQueryFilters()
            .Include(r => r.Song)
            .FirstOrDefaultAsync(r => r.Id == requestId && r.OwnerUserId == ownerLookup.OwnerUserId, ct);
        if (request is null || request.Status != UpgradeRequestStatus.Queued)
            return;

        var song = request.Song;
        if (song is null || song.IsDeleted || song.IsSynthetic
            || string.IsNullOrWhiteSpace(song.Artist) || string.IsNullOrWhiteSpace(song.Title))
        {
            request.MarkTerminal(UpgradeRequestStatus.Failed, "song is not upgradeable (deleted, synthetic, or missing identity)");
            await db.SaveChangesAsync(ct);
            return;
        }

        var stagingDir = enricherOptions.Value.DownloadDirectory;
        if (string.IsNullOrWhiteSpace(stagingDir))
        {
            request.MarkTerminal(UpgradeRequestStatus.Failed, "MusicEnricher:DownloadDirectory is not configured");
            await db.SaveChangesAsync(ct);
            return;
        }

        // The bar a candidate must clear: the target's current tier + score, plus its duration so a
        // provider can sanity-check length.
        var floor = new UpgradeFloor(
            AudioQuality.TierFor(song.Extension), AudioQuality.Score(song), song.DurationMs);

        // Providers in configured order that could actually beat the target (e.g. the lossless sidecar
        // opts out once the target is already lossless; yt-dlp isn't an IUpgradeProvider at all).
        var providers = DownloadProviderChain
            .Resolve(DownloadProviderChain.Names(enricherOptions.Value), upgradeProviders, p => p.Name, logger)
            .Where(p => p.CanUpgrade(floor))
            .ToList();
        if (providers.Count == 0)
        {
            request.MarkTerminal(UpgradeRequestStatus.NotFound, "no upgrade-capable provider can beat the current quality");
            await db.SaveChangesAsync(ct);
            return;
        }

        try
        {
            request.MarkSearching();
            await db.SaveChangesAsync(ct);

            var req = new DownloadRequest(
                song.Artist!, song.Title!, song.Album, song.Isrc, song.DurationMs ?? 0,
                stagingDir, song.SpotifyId);

            string? lastError = null;
            foreach (var provider in providers)
            {
                ct.ThrowIfCancellationRequested();
                var result = await provider.DownloadBetterAsync(req, floor, ct);

                if (result.Success && result.FilePath is not null)
                {
                    // Stamp the KNOWN identity so scan+enrichment read the authoritative tags — the
                    // source file may carry anything.
                    DownloadTagWriter.Stamp(
                        result.FilePath, song.Artist!, song.Title!, song.Album, song.Isrc, logger);
                    request.CandidateInfoJson = JsonSerializer.Serialize(new
                    {
                        provider = provider.Name,
                        file = Path.GetFileName(result.FilePath),
                    }, JsonOpts);
                    request.MarkAwaitingIngest(result.FilePath.Replace('\\', '/'));
                    await db.SaveChangesAsync(ct);

                    if (jobManager.TryStartJob(JobType.Scan, out var scanJobId, out _))
                        logger.LogInformation("Upgrade {RequestId} triggered scan {JobId}", requestId, scanJobId);
                    logger.LogInformation(
                        "Upgrade {RequestId} downloaded a better copy of song {SongId} '{Artist} - {Title}' via {Provider}",
                        requestId, song.Id, LogSanitizer.ForLog(song.Artist), LogSanitizer.ForLog(song.Title), provider.Name);
                    return;
                }

                lastError = result.Error;
                // A transient Error (not "not found") stops the chain: a flaky provider shouldn't burn
                // the next one's quota. The next sweep re-queues nothing automatically — the auto-sweep
                // cooldown governs retries.
                if (!result.NotFound)
                {
                    request.MarkTerminal(UpgradeRequestStatus.Failed, result.Error ?? $"{provider.Name} error");
                    await db.SaveChangesAsync(ct);
                    return;
                }
            }

            request.MarkTerminal(UpgradeRequestStatus.NotFound, lastError ?? "no better copy found in any provider");
            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Upgrade request {RequestId} failed", requestId);
            request.MarkTerminal(UpgradeRequestStatus.Failed, ex.Message);
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }
}

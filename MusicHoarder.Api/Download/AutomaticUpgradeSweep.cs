using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Audio;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Download;

/// <summary>
/// One pass of the automatic quality-upgrade sweep: find lossy tracks already built into the library
/// and queue them for an upgrade to lossless, reusing the exact same request → worker → merge pipeline
/// as the manual "find better quality" button. Opt-out via
/// <see cref="MusicEnricherOptions.EnableAutomaticQualityUpgrades"/> and inert unless a lossless-capable
/// provider (slskd / spotiflac) is configured. Bounded per pass and rate-limited per song so it never
/// hammers external services or re-chases a track that has no better source.
/// </summary>
public class AutomaticUpgradeSweep(
    MusicHoarderDbContext db,
    IEnumerable<IUpgradeProvider> upgradeProviders,
    QualityUpgradeChannel channel,
    IOwnerLookupService ownerLookup,
    IOptions<MusicEnricherOptions> options,
    ILogger<AutomaticUpgradeSweep> logger)
{
    /// <summary>Queues eligible songs and returns how many were queued this pass (0 = disabled, no
    /// provider, or nothing eligible).</summary>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        var opts = options.Value;
        if (!opts.EnableAutomaticQualityUpgrades)
            return 0;

        // Is any configured provider able to upgrade a lossy file? Probe with a lossy floor — the only
        // scope we target — so an all-lossy library on an instance with no slskd/spotiflac is a no-op.
        var lossyProbe = new UpgradeFloor(AudioCodecTier.Lossy, AudioQuality.Score(".mp3", 0), null);
        var providers = DownloadProviderChain.Resolve(
            DownloadProviderChain.Names(opts), upgradeProviders, p => p.Name, logger);
        if (!providers.Any(p => p.CanUpgrade(lossyProbe)))
            return 0;

        var ownerId = ownerLookup.OwnerUserId;
        var cooldown = TimeSpan.FromDays(Math.Max(0, opts.QualityUpgradeCooldownDays));
        var cutoff = DateTime.UtcNow - cooldown;
        var batchSize = Math.Clamp(opts.QualityUpgradeBatchSize, 1, 500);
        var lossyExtensions = AudioQuality.LossyExtensions;

        // Songs to skip: one with an in-flight request, or one whose most recent attempt (of any kind)
        // is still inside the cooldown window — don't re-chase a track that just came up empty.
        var blockedSongIds = await db.UpgradeRequests
            .IgnoreQueryFilters()
            .Where(r => r.OwnerUserId == ownerId
                && (r.Status == UpgradeRequestStatus.Queued
                    || r.Status == UpgradeRequestStatus.Searching
                    || r.Status == UpgradeRequestStatus.Downloading
                    || r.Status == UpgradeRequestStatus.AwaitingIngest
                    || r.UpdatedAtUtc >= cutoff))
            .Select(r => r.SongId)
            .Distinct()
            .ToListAsync(ct);

        var candidateIds = await db.Songs
            .IgnoreQueryFilters()
            .Where(s => s.OwnerUserId == ownerId)
            .ExcludingDemoTenant()
            .Where(s => s.DeletedAtUtc == null && !s.IsSynthetic && !s.IsDuplicate
                && s.LibraryBuildStatus == LibraryBuildStatus.Done
                && s.Artist != null && s.Artist != ""
                && s.Title != null && s.Title != ""
                && s.Extension != null && lossyExtensions.Contains(s.Extension.ToLower())
                && !blockedSongIds.Contains(s.Id))
            .OrderBy(s => s.Id)
            .Select(s => s.Id)
            .Take(batchSize)
            .ToListAsync(ct);

        if (candidateIds.Count == 0)
            return 0;

        var now = DateTime.UtcNow;
        var requests = candidateIds.Select(id => new UpgradeRequest
        {
            SongId = id,
            OwnerUserId = ownerId,
            Trigger = UpgradeTrigger.Auto,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        }).ToList();

        db.UpgradeRequests.AddRange(requests);
        await db.SaveChangesAsync(ct);

        foreach (var request in requests)
            channel.Enqueue(request.Id);

        logger.LogInformation("Auto-upgrade sweep queued {Count} lossy track(s) for quality upgrade", requests.Count);
        return requests.Count;
    }
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Audio;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Logging;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Soulseek;

/// <summary>
/// Executes one queued quality-upgrade request: search Soulseek for a strictly better copy of the
/// target song, download it into the normal staging dir, stamp it with the song's KNOWN identity
/// (exactly like wishlist downloads — never the peer's tags), and hand off to the pipeline as
/// <see cref="UpgradeRequestStatus.AwaitingIngest"/>. The scan→fingerprint stages then produce a
/// provisional row that <see cref="UpgradeMergeService"/> verifies and swaps into the target.
/// </summary>
public class SoulseekUpgradeService(
    MusicHoarderDbContext db,
    SlskdFileFetcher fetcher,
    JobManager jobManager,
    IOwnerLookupService ownerLookup,
    IOptionsMonitor<SlskdOptions> slskdOptions,
    IOptions<MusicEnricherOptions> enricherOptions,
    ILogger<SoulseekUpgradeService> logger)
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

        var opts = slskdOptions.CurrentValue;
        if (!opts.IsConfigured)
        {
            request.MarkTerminal(UpgradeRequestStatus.Failed, "slskd not configured");
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

        try
        {
            request.MarkSearching();
            await db.SaveChangesAsync(ct);

            var candidates = await FindBetterCandidatesAsync(song, opts, ct);
            if (candidates.Count == 0)
            {
                request.MarkTerminal(UpgradeRequestStatus.NotFound, "no strictly better copy found on Soulseek");
                await db.SaveChangesAsync(ct);
                return;
            }

            string? lastError = null;
            foreach (var candidate in candidates.Take(Math.Max(1, opts.MaxCandidateAttempts)))
            {
                ct.ThrowIfCancellationRequested();
                request.MarkDownloading(
                    AudioQuality.Score(candidate.File.NormalizedExtension, candidate.File.BitRate),
                    JsonSerializer.Serialize(new
                    {
                        candidate.Username,
                        filename = candidate.File.RemoteLeafName,
                        bitRate = candidate.File.BitRate,
                        size = candidate.File.Size,
                        extension = candidate.File.NormalizedExtension,
                    }, JsonOpts));
                await db.SaveChangesAsync(ct);

                var outcome = await fetcher.FetchCandidateAsync(candidate, stagingDir, ct);
                if (outcome.Success && outcome.FilePath is not null)
                {
                    // Stamp the KNOWN identity so scan+enrichment read the authoritative tags —
                    // the peer's file may carry anything.
                    Download.DownloadTagWriter.Stamp(
                        outcome.FilePath, song.Artist!, song.Title!, song.Album, song.Isrc, logger);
                    request.MarkAwaitingIngest(outcome.FilePath.Replace('\\', '/'));
                    await db.SaveChangesAsync(ct);

                    if (jobManager.TryStartJob(JobType.Scan, out var scanJobId, out _))
                        logger.LogInformation("Upgrade {RequestId} triggered scan {JobId}", requestId, scanJobId);
                    logger.LogInformation("Upgrade {RequestId} downloaded better copy of song {SongId} '{Artist} - {Title}'",
                        requestId, song.Id, LogSanitizer.ForLog(song.Artist), LogSanitizer.ForLog(song.Title));
                    return;
                }
                lastError = outcome.Error;
            }

            request.MarkTerminal(UpgradeRequestStatus.NotFound, lastError ?? "all Soulseek candidates failed");
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

    /// <summary>
    /// Elects candidates that are STRICTLY better than the target: higher AudioQuality score AND at
    /// least the target's codec tier — a fatter lossy file never replaces lossless, and equal-tier
    /// candidates must beat the target's bitrate.
    /// </summary>
    private async Task<IReadOnlyList<SlskdCandidate>> FindBetterCandidatesAsync(
        SongMetadata song, SlskdOptions opts, CancellationToken ct)
    {
        var responses = await fetcher.SearchAsync($"{song.Artist} {song.Title}".Trim(), ct);
        var candidates = SlskdCandidateSelector.Select(responses, song.Title!, song.DurationMs ?? 0, opts);

        if (candidates.Count == 0 && !string.IsNullOrWhiteSpace(song.Album))
        {
            responses = await fetcher.SearchAsync($"{song.Artist} {song.Album}".Trim(), ct);
            candidates = SlskdCandidateSelector.Select(responses, song.Title!, song.DurationMs ?? 0, opts);
        }

        return FilterStrictlyBetter(candidates, song);
    }

    internal static IReadOnlyList<SlskdCandidate> FilterStrictlyBetter(
        IReadOnlyList<SlskdCandidate> candidates, SongMetadata song)
    {
        var targetScore = AudioQuality.Score(song);
        var targetTier = AudioQuality.TierFor(song.Extension);
        return candidates
            .Where(c => AudioQuality.TierFor(c.File.NormalizedExtension) >= targetTier
                && AudioQuality.Score(c.File.NormalizedExtension, c.File.BitRate) > targetScore)
            .ToList();
    }
}

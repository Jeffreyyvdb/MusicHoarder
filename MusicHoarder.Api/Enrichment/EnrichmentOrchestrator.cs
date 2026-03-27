using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment;

public record EnrichmentBatchResult(
    int TotalTracks,
    int Enriched,
    int Failed,
    int NeedsReview,
    TimeSpan Duration);

public interface IEnrichmentOrchestrator
{
    Task<EnrichmentBatchResult> ProcessNextBatchAsync(Guid runId, CancellationToken ct = default);
}

internal record EnrichmentTrackCandidate(int SongId);

internal enum EnrichmentOutcome
{
    Matched,
    NeedsReview,
    Failed,
}

public class EnrichmentOrchestrator(
    IServiceScopeFactory scopeFactory,
    EnrichmentProgressTracker progressTracker,
    IEnumerable<IEnrichmentProvider> providers,
    ILrcLibService lrcLibService,
    IOptions<MusicEnricherOptions> options,
    ILogger<EnrichmentOrchestrator> logger) : IEnrichmentOrchestrator
{
    private sealed class BatchCounters
    {
        public int Enriched;
        public int NeedsReview;
        public int Failed;
    }

    public async Task<EnrichmentBatchResult> ProcessNextBatchAsync(Guid runId, CancellationToken ct = default)
    {
        var startTime = DateTime.UtcNow;
        var opts = options.Value;

        List<EnrichmentTrackCandidate> candidates;
        using (var loadScope = scopeFactory.CreateScope())
        {
            var dbContext = loadScope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
            candidates = await dbContext.Songs
                .AsNoTracking()
                .WhereReadyForEnrichment()
                .OrderBy(s => s.Id)
                .Take(opts.EnrichmentBatchSize)
                .Select(s => new EnrichmentTrackCandidate(s.Id))
                .ToListAsync(ct);
        }

        if (candidates.Count == 0)
        {
            return new EnrichmentBatchResult(0, 0, 0, 0, DateTime.UtcNow - startTime);
        }

        logger.LogInformation("Starting enrichment batch {RunId} with {Count} tracks", runId, candidates.Count);

        var workChannel = Channel.CreateBounded<EnrichmentTrackCandidate>(new BoundedChannelOptions(opts.EnrichmentBatchSize)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = true
        });

        foreach (var candidate in candidates)
        {
            await workChannel.Writer.WriteAsync(candidate, ct);
        }

        workChannel.Writer.Complete();

        var counters = new BatchCounters();
        var workers = Enumerable.Range(0, opts.EnrichmentWorkerConcurrency)
            .Select(_ => ConsumeWorkAsync(runId, workChannel.Reader, counters, ct))
            .ToArray();

        await Task.WhenAll(workers);

        var duration = DateTime.UtcNow - startTime;
        logger.LogInformation(
            "Enrichment batch {RunId} complete: Total={Total}, Enriched={Enriched}, NeedsReview={NeedsReview}, Failed={Failed}, Duration={Duration:F1}s",
            runId,
            candidates.Count,
            counters.Enriched,
            counters.NeedsReview,
            counters.Failed,
            duration.TotalSeconds);

        return new EnrichmentBatchResult(
            candidates.Count,
            counters.Enriched,
            counters.Failed,
            counters.NeedsReview,
            duration);
    }

    private async Task ConsumeWorkAsync(
        Guid runId,
        ChannelReader<EnrichmentTrackCandidate> reader,
        BatchCounters counters,
        CancellationToken ct)
    {
        await foreach (var candidate in reader.ReadAllAsync(ct))
        {
            ct.ThrowIfCancellationRequested();

            var outcome = await ProcessTrackAsync(candidate.SongId, ct);
            switch (outcome)
            {
                case EnrichmentOutcome.Matched:
                    progressTracker.IncrementEnriched();
                    Interlocked.Increment(ref counters.Enriched);
                    break;
                case EnrichmentOutcome.NeedsReview:
                    progressTracker.IncrementNeedsReview();
                    Interlocked.Increment(ref counters.NeedsReview);
                    break;
                case EnrichmentOutcome.Failed:
                    progressTracker.IncrementFailed();
                    Interlocked.Increment(ref counters.Failed);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null);
            }

            var processed = progressTracker.GetCurrent()?.Processed ?? 0;
            if (processed % 100 == 0)
            {
                var total = progressTracker.GetCurrent()?.TotalTracks ?? 0;
                logger.LogInformation(
                    "Enrichment progress {RunId}: {Processed}/{Total} processed",
                    runId,
                    processed,
                    total);
            }
        }
    }

    private async Task<EnrichmentOutcome> ProcessTrackAsync(int songId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        var song = await dbContext.Songs.FirstOrDefaultAsync(s => s.Id == songId, ct);
        if (song is null || song.IsDeleted)
        {
            logger.LogDebug("Skipping enrichment for missing/deleted song {SongId}", songId);
            return EnrichmentOutcome.Failed;
        }

        try
        {
            song.RecordEnrichmentAttempt();

            var enabledProviders = GetEnabledProviders();
            EnrichmentProviderResult? bestFallback = null;
            var errors = new List<string>();

            foreach (var provider in enabledProviders)
            {
                if (!provider.CanHandle(song))
                    continue;

                try
                {
                    var result = await provider.TryEnrichAsync(song, ct);
                    if (result is null)
                        continue;

                    if (result.RecommendedStatus == EnrichmentStatus.Matched)
                    {
                        ApplyProviderResult(song, result);
                        await dbContext.SaveChangesAsync(ct);

                        logger.LogInformation(
                            "Enrichment matched {Track} (SongId={SongId}) via {Provider} with confidence {Confidence:F3}",
                            song.TrackLabel, songId, result.MatchedBy, result.MatchConfidence);

                        await FetchLyricsForSongAsync(song, dbContext, ct);
                        return EnrichmentOutcome.Matched;
                    }

                    if (bestFallback is null || result.MatchConfidence > bestFallback.MatchConfidence)
                        bestFallback = result;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    errors.Add($"{provider.Name}: {ex.Message}");
                    logger.LogWarning(ex, "Provider {Provider} failed for {Track} (SongId={SongId})",
                        provider.Name, song.TrackLabel, songId);
                }
            }

            if (bestFallback is not null)
            {
                ApplyProviderResult(song, bestFallback);
                await dbContext.SaveChangesAsync(ct);

                logger.LogInformation(
                    "Enrichment needs review for {Track} (SongId={SongId}): best result from {Provider} with confidence {Confidence:F3}, warnings=[{Warnings}]",
                    song.TrackLabel, songId, bestFallback.MatchedBy, bestFallback.MatchConfidence,
                    string.Join(", ", bestFallback.MatchWarnings));

                return EnrichmentOutcome.NeedsReview;
            }

            if (errors.Count > 0)
            {
                song.MarkEnrichmentFailed(string.Join("; ", errors));
                await dbContext.SaveChangesAsync(ct);
                logger.LogWarning("All providers failed for {Track} (SongId={SongId}): {Errors}",
                    song.TrackLabel, songId, string.Join("; ", errors));
                return EnrichmentOutcome.Failed;
            }

            song.MarkEnrichmentNeedsReview("No provider returned a match");
            await dbContext.SaveChangesAsync(ct);
            logger.LogInformation("Enrichment needs review for {Track} (SongId={SongId}): no provider returned a match",
                song.TrackLabel, songId);
            return EnrichmentOutcome.NeedsReview;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            song.MarkEnrichmentFailed(ex.Message);
            await dbContext.SaveChangesAsync(ct);
            logger.LogWarning(ex, "Failed enrichment for {Track} (SongId={SongId})", song.TrackLabel, songId);
            return EnrichmentOutcome.Failed;
        }
    }

    private IReadOnlyList<IEnrichmentProvider> GetEnabledProviders()
    {
        var opts = options.Value;
        return providers
            .Where(p => IsProviderEnabled(p, opts))
            .OrderBy(p => p.Priority)
            .ToList();
    }

    private static bool IsProviderEnabled(IEnrichmentProvider provider, MusicEnricherOptions opts)
    {
        return provider.Name switch
        {
            "AcoustID" => opts.EnableAcoustIdProvider,
            "MusicBrainzWeb" => opts.EnableMusicBrainzWebProvider,
            "SpotifyAPI" => opts.EnableSpotifyApiProvider,
            "Tracker" => opts.EnableTrackerProvider,
            _ => true
        };
    }

    private static void ApplyProviderResult(SongMetadata song, EnrichmentProviderResult result)
    {
        var warningsJson = result.MatchWarnings.Count > 0
            ? JsonSerializer.Serialize(result.MatchWarnings)
            : null;

        song.ApplyEnrichmentMatch(new EnrichmentMatchData(
            result.Artist,
            result.AlbumArtist,
            result.Title,
            result.Year,
            result.TrackNumber,
            result.MusicBrainzId,
            result.MusicBrainzReleaseId,
            result.SpotifyId,
            result.AcoustIdTrackId,
            result.Isrc,
            result.MatchedBy,
            result.MatchConfidence,
            warningsJson,
            result.RecommendedStatus,
            result.Album));
    }

    private async Task FetchLyricsForSongAsync(SongMetadata song, MusicHoarderDbContext dbContext, CancellationToken ct)
    {
        if (!song.IsReadyForLyricsFetch)
        {
            logger.LogDebug("Skipping lyrics fetch for {Track} (SongId={SongId}): not eligible (status={LyricsStatus})",
                song.TrackLabel, song.Id, song.LyricsStatus);
            return;
        }

        try
        {
            var result = await lrcLibService.FetchLyricsAsync(song, ct);

            if (result is null)
            {
                song.MarkLyricsNotFound();
                logger.LogDebug("No lyrics found for {Track} (SongId={SongId})", song.TrackLabel, song.Id);
            }
            else if (result.IsInstrumental)
            {
                song.ApplyLyricsResult(null, null, true, result.LrclibId);
                logger.LogInformation("Lyrics: instrumental confirmed for {Track} (SongId={SongId})", song.TrackLabel, song.Id);
            }
            else
            {
                song.ApplyLyricsResult(result.SyncedLyrics, result.PlainLyrics, false, result.LrclibId);
                var kind = result.SyncedLyrics is not null ? "synced" : "plain";
                logger.LogInformation("Lyrics: fetched ({Kind}) for {Track} (SongId={SongId})", kind, song.TrackLabel, song.Id);
            }

            await dbContext.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            song.MarkLyricsFailed();
            await dbContext.SaveChangesAsync(ct);
            logger.LogWarning(ex, "Lyrics fetch failed for {Track} (SongId={SongId})", song.TrackLabel, song.Id);
        }
    }
}

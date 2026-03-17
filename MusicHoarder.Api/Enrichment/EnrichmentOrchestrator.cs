using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Metadata;
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
    IAcoustIdMatchValidator matchValidator,
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
                .Where(s => s.DeletedAtUtc == null)
                .Where(s => s.Fingerprint != null && s.Fingerprint != string.Empty)
                .Where(s => s.DurationSeconds != null)
                .Where(s => s.EnrichmentStatus == EnrichmentStatus.Pending)
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
        var acoustIdService = scope.ServiceProvider.GetRequiredService<IAcoustIdService>();

        var song = await dbContext.Songs.FirstOrDefaultAsync(s => s.Id == songId, ct);
        if (song is null || song.IsDeleted)
        {
            logger.LogDebug("Skipping enrichment for missing/deleted song {SongId}", songId);
            return EnrichmentOutcome.Failed;
        }

        if (string.IsNullOrWhiteSpace(song.Fingerprint) || song.DurationSeconds is null)
        {
            song.MarkEnrichmentNeedsReview("Missing fingerprint or duration");
            await dbContext.SaveChangesAsync(ct);
            logger.LogInformation("Enrichment needs review for {Track} (SongId={SongId}): missing fingerprint or duration",
                song.TrackLabel, songId);
            return EnrichmentOutcome.NeedsReview;
        }

        try
        {
            song.RecordEnrichmentAttempt();

            var match = await acoustIdService.LookupAsync(song.Fingerprint!, song.DurationSeconds.Value, ct);
            if (match is null)
            {
                song.MarkEnrichmentNeedsReview("No confident AcoustID match");
                await dbContext.SaveChangesAsync(ct);
                logger.LogInformation("Enrichment needs review for {Track} (SongId={SongId}): no confident AcoustID match",
                    song.TrackLabel, songId);
                return EnrichmentOutcome.NeedsReview;
            }

            var validation = matchValidator.Validate(match, song);

            var effectiveArtist = string.IsNullOrWhiteSpace(match.Artist) ? song.Artist : match.Artist;
            var resolvedAlbumArtist = string.IsNullOrWhiteSpace(match.AlbumArtist)
                ? ArtistCreditNormalizer.GetPrimaryArtist(effectiveArtist)
                : match.AlbumArtist;

            song.ApplyEnrichmentMatch(new EnrichmentMatchData(
                match.Artist,
                resolvedAlbumArtist,
                match.Title,
                match.MusicBrainzRecordingId,
                "AcoustID",
                validation.AdjustedScore,
                validation.WarningsJson,
                validation.RecommendedStatus));

            await dbContext.SaveChangesAsync(ct);

            if (validation.RecommendedStatus == EnrichmentStatus.Matched)
            {
                logger.LogInformation(
                    "Enrichment matched {Track} (SongId={SongId}) via AcoustID with adjusted score {Score:F3} (raw={RawScore:F3}) and MusicBrainzId {MusicBrainzId}",
                    song.TrackLabel, songId, validation.AdjustedScore, match.Score, song.MusicBrainzId);

                await FetchLyricsForSongAsync(song, dbContext, ct);

                return EnrichmentOutcome.Matched;
            }

            logger.LogInformation(
                "Enrichment needs review for {Track} (SongId={SongId}): adjusted score {Score:F3} (raw={RawScore:F3}), warnings=[{Warnings}]",
                song.TrackLabel, songId, validation.AdjustedScore, match.Score,
                string.Join(", ", validation.Warnings));
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
                song.ApplyLyricsResult(null, null, true);
                logger.LogInformation("Lyrics: instrumental confirmed for {Track} (SongId={SongId})", song.TrackLabel, song.Id);
            }
            else
            {
                song.ApplyLyricsResult(result.SyncedLyrics, result.PlainLyrics, false);
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

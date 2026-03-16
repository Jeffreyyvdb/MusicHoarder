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
    IOptions<MusicEnricherOptions> options,
    ILogger<EnrichmentOrchestrator> logger) : IEnrichmentOrchestrator
{
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

        progressTracker.Start(runId, candidates.Count);
        if (candidates.Count == 0)
        {
            progressTracker.Complete(runId);
            return new EnrichmentBatchResult(0, 0, 0, 0, DateTime.UtcNow - startTime);
        }

        logger.LogInformation("Starting enrichment run {RunId} with {Count} tracks", runId, candidates.Count);

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

        var workers = Enumerable.Range(0, opts.EnrichmentWorkerConcurrency)
            .Select(_ => ConsumeWorkAsync(runId, workChannel.Reader, ct))
            .ToArray();

        await Task.WhenAll(workers);

        progressTracker.Complete(runId);
        var currentState = progressTracker.GetCurrent();

        var duration = DateTime.UtcNow - startTime;
        logger.LogInformation(
            "Enrichment run {RunId} complete: Total={Total}, Enriched={Enriched}, NeedsReview={NeedsReview}, Failed={Failed}, Duration={Duration:F1}s",
            runId,
            candidates.Count,
            currentState?.Enriched ?? 0,
            currentState?.NeedsReview ?? 0,
            currentState?.Failed ?? 0,
            duration.TotalSeconds);

        return new EnrichmentBatchResult(
            candidates.Count,
            currentState?.Enriched ?? 0,
            currentState?.Failed ?? 0,
            currentState?.NeedsReview ?? 0,
            duration);
    }

    private async Task ConsumeWorkAsync(
        Guid runId,
        ChannelReader<EnrichmentTrackCandidate> reader,
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
                    break;
                case EnrichmentOutcome.NeedsReview:
                    progressTracker.IncrementNeedsReview();
                    break;
                case EnrichmentOutcome.Failed:
                    progressTracker.IncrementFailed();
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
        if (song is null || song.DeletedAtUtc != null)
        {
            logger.LogDebug("Skipping enrichment for missing/deleted song {SongId}", songId);
            return EnrichmentOutcome.Failed;
        }

        var trackLabel = BuildTrackLabel(song);
        var fingerprint = song.Fingerprint;
        var durationSeconds = song.DurationSeconds;
        if (string.IsNullOrWhiteSpace(fingerprint) || durationSeconds is null)
        {
            song.EnrichmentStatus = EnrichmentStatus.NeedsReview;
            song.EnrichmentLastAttemptedAtUtc = DateTime.UtcNow;
            song.EnrichmentError = "Missing fingerprint or duration";
            await dbContext.SaveChangesAsync(ct);
            logger.LogInformation(
                "Enrichment needs review for {Track} (SongId={SongId}): missing fingerprint or duration",
                trackLabel,
                songId);
            return EnrichmentOutcome.NeedsReview;
        }

        try
        {
            var now = DateTime.UtcNow;
            song.EnrichmentLastAttemptedAtUtc = now;

            var match = await acoustIdService.LookupAsync(fingerprint, durationSeconds.Value, ct);
            if (match is null)
            {
                song.EnrichmentStatus = EnrichmentStatus.NeedsReview;
                song.MatchedBy = null;
                song.MatchConfidence = null;
                song.MatchWarnings = null;
                song.EnrichmentError = "No confident AcoustID match";
                await dbContext.SaveChangesAsync(ct);
                logger.LogInformation(
                    "Enrichment needs review for {Track} (SongId={SongId}): no confident AcoustID match",
                    trackLabel,
                    songId);
                return EnrichmentOutcome.NeedsReview;
            }

            var validation = matchValidator.Validate(match, song);
            CaptureOriginalMetadata(song, now);

            song.Artist = string.IsNullOrWhiteSpace(match.Artist) ? song.Artist : match.Artist;
            song.AlbumArtist = string.IsNullOrWhiteSpace(match.AlbumArtist)
                ? ArtistCreditNormalizer.GetPrimaryArtist(song.Artist) ?? song.AlbumArtist
                : match.AlbumArtist;
            song.Title = string.IsNullOrWhiteSpace(match.Title) ? song.Title : match.Title;
            song.MusicBrainzId = match.MusicBrainzRecordingId;
            song.MatchedBy = "AcoustID";
            song.MatchConfidence = validation.AdjustedScore;
            song.MatchWarnings = validation.WarningsJson;
            song.EnrichmentStatus = validation.RecommendedStatus;
            song.EnrichedAtUtc = now;
            song.EnrichmentError = null;

            await dbContext.SaveChangesAsync(ct);

            if (validation.RecommendedStatus == EnrichmentStatus.Matched)
            {
                logger.LogInformation(
                    "Enrichment matched {Track} (SongId={SongId}) via AcoustID with adjusted score {Score:F3} (raw={RawScore:F3}) and MusicBrainzId {MusicBrainzId}",
                    trackLabel, songId, validation.AdjustedScore, match.Score, song.MusicBrainzId);
                return EnrichmentOutcome.Matched;
            }

            logger.LogInformation(
                "Enrichment needs review for {Track} (SongId={SongId}): adjusted score {Score:F3} (raw={RawScore:F3}), warnings=[{Warnings}]",
                trackLabel, songId, validation.AdjustedScore, match.Score,
                string.Join(", ", validation.Warnings));
            return EnrichmentOutcome.NeedsReview;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            song.EnrichmentStatus = EnrichmentStatus.Failed;
            song.EnrichmentError = Truncate(ex.Message, 1024);
            song.EnrichmentLastAttemptedAtUtc = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(ct);
            logger.LogWarning(ex, "Failed enrichment for {Track} (SongId={SongId})", trackLabel, songId);
            return EnrichmentOutcome.Failed;
        }
    }

    private static string BuildTrackLabel(SongMetadata song)
    {
        var title = string.IsNullOrWhiteSpace(song.Title) ? "<unknown-title>" : song.Title;
        var artist = string.IsNullOrWhiteSpace(song.Artist) ? "<unknown-artist>" : song.Artist;
        return $"{artist} - {title} [{song.FileName}]";
    }

    private static void CaptureOriginalMetadata(SongMetadata song, DateTime now)
    {
        if (song.OriginalMetadataCaptured) return;

        song.OriginalMetadataCaptured = true;
        song.OriginalArtist = song.Artist;
        song.OriginalAlbumArtist = song.AlbumArtist;
        song.OriginalAlbum = song.Album;
        song.OriginalTitle = song.Title;
        song.OriginalYear = song.Year;
        song.OriginalTrackNumber = song.TrackNumber;
        song.OriginalIsrc = song.Isrc;
        song.OriginalMusicBrainzId = song.MusicBrainzId;
        song.OriginalSpotifyId = song.SpotifyId;
        song.OriginalMetadataCapturedAtUtc = now;
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}

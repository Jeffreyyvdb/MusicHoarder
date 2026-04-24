using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Pipeline;

public record PurgeResult(
    int SongsAffected,
    int FilesDeleted,
    int FilesFailed,
    int SpotifyMatchesCleared);

public interface IPipelinePurgeService
{
    Task<PurgeResult> ResetPostFingerprintAsync(Guid jobId, CancellationToken ct = default);
    Task<PurgeResult> PurgeAllAsync(Guid jobId, CancellationToken ct = default);
}

public class PipelinePurgeService(
    MusicHoarderDbContext db,
    IOptions<MusicEnricherOptions> options,
    ILibraryDestinationCleaner destinationCleaner,
    JobManager jobManager,
    PurgeStatusTracker tracker,
    ILogger<PipelinePurgeService> logger) : IPipelinePurgeService
{
    private const int FileDeleteConcurrency = 4;

    private static readonly JobType[] PipelineSteps =
        [JobType.Scan, JobType.Fingerprint, JobType.Enrich, JobType.Build];

    public async Task<PurgeResult> ResetPostFingerprintAsync(Guid jobId, CancellationToken ct = default)
    {
        tracker.Start("post-fingerprint", jobId);
        PausePipelineSteps();
        try
        {
            var destinationRoot = options.Value.DestinationDirectory;

            var songs = await db.Songs
                .Include(s => s.ProviderAttempts)
                .Where(s => s.DeletedAtUtc == null)
                .ToListAsync(ct);

            var songsWithFiles = songs.Where(s => !string.IsNullOrWhiteSpace(s.DestinationPath)).ToList();
            tracker.SetTotals(songs.Count, songsWithFiles.Count);

            var (filesDeleted, filesFailed) = await DeleteDestinationFilesAsync(songsWithFiles, destinationRoot, ct);

            foreach (var song in songs) song.ResetPostFingerprint();
            tracker.SetSongsProcessed(songs.Count);

            var matchesCleared = await ClearSpotifyMatchesAsync(ct);

            await db.SaveChangesAsync(ct);

            tracker.Complete(matchesCleared);
            logger.LogInformation(
                "Reset post-fingerprint state for {SongCount} songs, deleted {FileCount} destination files ({FailedCount} failed), cleared {MatchCount} Spotify matches.",
                songs.Count, filesDeleted, filesFailed, matchesCleared);

            return new PurgeResult(songs.Count, filesDeleted, filesFailed, matchesCleared);
        }
        finally
        {
            ResumePipelineSteps();
        }
    }

    public async Task<PurgeResult> PurgeAllAsync(Guid jobId, CancellationToken ct = default)
    {
        tracker.Start("all", jobId);
        PausePipelineSteps();
        try
        {
            var destinationRoot = options.Value.DestinationDirectory;

            var songs = await db.Songs.ToListAsync(ct);
            var songsWithFiles = songs.Where(s => !string.IsNullOrWhiteSpace(s.DestinationPath)).ToList();
            tracker.SetTotals(songs.Count, songsWithFiles.Count);

            var (filesDeleted, filesFailed) = await DeleteDestinationFilesAsync(songsWithFiles, destinationRoot, ct);

            var matchesCleared = await ClearSpotifyMatchesAsync(ct);

            db.Songs.RemoveRange(songs);
            tracker.SetSongsProcessed(songs.Count);

            await db.SaveChangesAsync(ct);

            tracker.Complete(matchesCleared);
            logger.LogInformation(
                "Purged all pipeline data: {SongCount} songs deleted, {FileCount} destination files removed ({FailedCount} failed), {MatchCount} Spotify matches cleared.",
                songs.Count, filesDeleted, filesFailed, matchesCleared);

            return new PurgeResult(songs.Count, filesDeleted, filesFailed, matchesCleared);
        }
        finally
        {
            ResumePipelineSteps();
        }
    }

    private void PausePipelineSteps()
    {
        foreach (var step in PipelineSteps) jobManager.PauseStep(step);
    }

    private void ResumePipelineSteps()
    {
        foreach (var step in PipelineSteps) jobManager.ResumeStep(step);
    }

    private async Task<(int deleted, int failed)> DeleteDestinationFilesAsync(
        IReadOnlyList<SongMetadata> songs,
        string destinationRoot,
        CancellationToken ct)
    {
        var counters = new Counters();

        await Parallel.ForEachAsync(
            songs,
            new ParallelOptions { MaxDegreeOfParallelism = FileDeleteConcurrency, CancellationToken = ct },
            (song, _) =>
            {
                try
                {
                    destinationCleaner.DeleteManagedPathAndPrune(song.DestinationPath!, destinationRoot);
                    var d = Interlocked.Increment(ref counters.Deleted);
                    tracker.UpdateFilesProgress(d, Volatile.Read(ref counters.Failed));
                }
                catch (Exception ex)
                {
                    var f = Interlocked.Increment(ref counters.Failed);
                    tracker.UpdateFilesProgress(Volatile.Read(ref counters.Deleted), f);
                    logger.LogWarning(ex,
                        "Failed to delete destination file for song {SongId} at {Path}",
                        song.Id, song.DestinationPath);
                }
                return ValueTask.CompletedTask;
            });

        return (counters.Deleted, counters.Failed);
    }

    private async Task<int> ClearSpotifyMatchesAsync(CancellationToken ct)
    {
        var matches = await db.SpotifyTrackLibraryMatches.ToListAsync(ct);
        db.SpotifyTrackLibraryMatches.RemoveRange(matches);
        return matches.Count;
    }

    private sealed class Counters
    {
        public int Deleted;
        public int Failed;
    }
}

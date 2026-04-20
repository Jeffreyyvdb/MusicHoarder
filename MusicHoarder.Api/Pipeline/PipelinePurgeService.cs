using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Pipeline;

public record PurgeResult(int SongsAffected, int FilesDeleted, int SpotifyMatchesCleared);

public interface IPipelinePurgeService
{
    Task<PurgeResult> ResetPostFingerprintAsync(CancellationToken ct = default);
    Task<PurgeResult> PurgeAllAsync(CancellationToken ct = default);
}

public class PipelinePurgeService(
    MusicHoarderDbContext db,
    IOptions<MusicEnricherOptions> options,
    ILibraryDestinationCleaner destinationCleaner,
    ILogger<PipelinePurgeService> logger) : IPipelinePurgeService
{
    public async Task<PurgeResult> ResetPostFingerprintAsync(CancellationToken ct = default)
    {
        var destinationRoot = options.Value.DestinationDirectory;

        var songs = await db.Songs
            .Include(s => s.ProviderAttempts)
            .Where(s => s.DeletedAtUtc == null)
            .ToListAsync(ct);

        var filesDeleted = DeleteDestinationFiles(songs, destinationRoot);

        foreach (var song in songs)
        {
            song.ResetPostFingerprint();
        }

        var matchesCleared = await ClearSpotifyMatchesAsync(ct);

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Reset post-fingerprint state for {SongCount} songs, deleted {FileCount} destination files, cleared {MatchCount} Spotify matches.",
            songs.Count, filesDeleted, matchesCleared);

        return new PurgeResult(songs.Count, filesDeleted, matchesCleared);
    }

    public async Task<PurgeResult> PurgeAllAsync(CancellationToken ct = default)
    {
        var destinationRoot = options.Value.DestinationDirectory;

        var songs = await db.Songs.ToListAsync(ct);
        var filesDeleted = DeleteDestinationFiles(songs, destinationRoot);

        var matchesCleared = await ClearSpotifyMatchesAsync(ct);

        db.Songs.RemoveRange(songs);

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Purged all pipeline data: {SongCount} songs deleted, {FileCount} destination files removed, {MatchCount} Spotify matches cleared.",
            songs.Count, filesDeleted, matchesCleared);

        return new PurgeResult(songs.Count, filesDeleted, matchesCleared);
    }

    private int DeleteDestinationFiles(IEnumerable<SongMetadata> songs, string destinationRoot)
    {
        var deleted = 0;
        foreach (var song in songs)
        {
            if (string.IsNullOrWhiteSpace(song.DestinationPath)) continue;
            try
            {
                destinationCleaner.DeleteManagedPathAndPrune(song.DestinationPath, destinationRoot);
                deleted++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to delete destination file for song {SongId} at {Path}",
                    song.Id, song.DestinationPath);
            }
        }
        return deleted;
    }

    private async Task<int> ClearSpotifyMatchesAsync(CancellationToken ct)
    {
        var matches = await db.SpotifyTrackLibraryMatches.ToListAsync(ct);
        db.SpotifyTrackLibraryMatches.RemoveRange(matches);
        return matches.Count;
    }
}

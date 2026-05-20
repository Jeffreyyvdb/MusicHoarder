using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Scanner;

public record DuplicateDetectionResult(
    int GroupsFound,
    int DuplicatesFlagged,
    int DuplicatesCleared,
    TimeSpan Duration);

public interface IDuplicateDetectionService
{
    Task<DuplicateDetectionResult> DetectDuplicatesAsync(CancellationToken ct = default);

    /// <summary>
    /// Computes a quality score for a song based on format and bitrate.
    /// FLAC/WAV/AIFF (lossless) score highest; lossy formats score by bitrate.
    /// Ties are broken by file size (larger = higher quality).
    /// </summary>
    static int QualityScore(SongMetadata song)
    {
        var formatScore = song.Extension?.ToLowerInvariant() switch
        {
            ".flac" => 1000,
            ".wav" => 900,
            ".aiff" => 900,
            ".mp3" => song.Bitrate ?? 0,
            ".m4a" => song.Bitrate ?? 0,
            ".aac" => song.Bitrate ?? 0,
            ".ogg" => song.Bitrate ?? 0,
            ".opus" => song.Bitrate ?? 0,
            ".wma" => song.Bitrate ?? 0,
            _ => 0
        };
        return formatScore;
    }
}

public class DuplicateDetectionService(
    IServiceScopeFactory scopeFactory,
    ILogger<DuplicateDetectionService> logger) : IDuplicateDetectionService
{
    public async Task<DuplicateDetectionResult> DetectDuplicatesAsync(CancellationToken ct = default)
    {
        var startedAt = DateTime.UtcNow;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        var songsWithFingerprint = await db.Songs
            .IgnoreQueryFilters()
            .Where(s => s.DeletedAtUtc == null && !s.IsSynthetic)
            .Where(s => s.Fingerprint != null && s.Fingerprint != "")
            .OrderBy(s => s.Id)
            .ToListAsync(ct);

        var groups = songsWithFingerprint
            .GroupBy(s => s.Fingerprint!)
            .Where(g => g.Count() > 1)
            .ToList();

        var duplicatesFlagged = 0;
        var duplicatesCleared = 0;

        var allCurrentDuplicateIds = new HashSet<int>();
        var allCurrentBestIds = new HashSet<int>();

        foreach (var group in groups)
        {
            var ranked = group
                .OrderByDescending(s => IDuplicateDetectionService.QualityScore(s))
                .ThenByDescending(s => s.FileSizeBytes)
                .ThenBy(s => s.Id)
                .ToList();

            var best = ranked[0];
            allCurrentBestIds.Add(best.Id);

            if (best.IsDuplicate)
            {
                best.ClearDuplicate();
                duplicatesCleared++;
            }

            for (var i = 1; i < ranked.Count; i++)
            {
                var dup = ranked[i];
                allCurrentDuplicateIds.Add(dup.Id);

                if (!dup.IsDuplicate || dup.DuplicateOfId != best.Id)
                {
                    dup.MarkAsDuplicate(best.Id);
                    duplicatesFlagged++;
                }
            }
        }

        var previousDuplicates = songsWithFingerprint
            .Where(s => s.IsDuplicate && !allCurrentDuplicateIds.Contains(s.Id));

        foreach (var song in previousDuplicates)
        {
            song.ClearDuplicate();
            duplicatesCleared++;
        }

        await db.SaveChangesAsync(ct);

        var duration = DateTime.UtcNow - startedAt;

        logger.LogInformation(
            "Duplicate detection complete: {Groups} groups, {Flagged} flagged, {Cleared} cleared, Duration={Duration:F1}s",
            groups.Count, duplicatesFlagged, duplicatesCleared, duration.TotalSeconds);

        return new DuplicateDetectionResult(groups.Count, duplicatesFlagged, duplicatesCleared, duration);
    }
}

using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Audio;
using MusicHoarder.Api.Auth;
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
    /// Computes a quality score for a song. Codec tier dominates (lossless always beats lossy);
    /// bitrate breaks ties within a tier. Ties are broken by file size (larger = higher quality).
    /// </summary>
    static int QualityScore(SongMetadata song) => AudioQuality.Score(song);
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
            // Grouping is by fingerprint across owners; keep the demo tenant out of it entirely.
            .ExcludingDemoTenant()
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
            // Quality first, then metadata trustworthiness: a Matched copy carries verified tags, while
            // an unmatched twin may be a mislabeled file (identical audio under a wrong title/album) —
            // electing that one as "best" knocks the correctly-tagged copy out of the build. An
            // already-built copy also outranks an unbuilt twin at equal standing: flagging the built one
            // would orphan its destination file and rebuild the same audio under a new name.
            var ranked = group
                .OrderByDescending(s => IDuplicateDetectionService.QualityScore(s))
                .ThenByDescending(s => s.EnrichmentStatus == EnrichmentStatus.Matched)
                .ThenByDescending(s => s.LibraryBuildStatus == LibraryBuildStatus.Done && s.DestinationPath != null)
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

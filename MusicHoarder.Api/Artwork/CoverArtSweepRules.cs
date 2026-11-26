using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Artwork;

/// <summary>One cover the sweep or the builder wrote, as the upgrade pass reads it back.</summary>
public readonly record struct CoverWriteRecord(long Id, string Folder, DateTime WrittenAtUtc, string? NewValue);

/// <summary>
/// What the external cover art sweep selects and when it retries, in one place so the Background work page
/// counts the same folders the sweep would work on.
/// </summary>
public static class CoverArtSweepRules
{
    public const string CoverArtArchiveSource = "fetched:coverartarchive";

    /// <summary>
    /// Built, real, non-demo tracks. Unreleased folders mix unrelated singles, so one album cover would be
    /// wrong there, and demo rows' destination is a read-only mount.
    /// </summary>
    public static IQueryable<SongMetadata> WhereBuiltForCoverSweep(this IQueryable<SongMetadata> songs)
        => songs
            .ExcludingDemoTenant()
            .Where(s => s.DeletedAtUtc == null && !s.IsSynthetic
                && !s.IsUnreleased
                && s.LibraryBuildStatus == LibraryBuildStatus.Done
                && s.DestinationPath != null
                && s.SourcePath != null);

    /// <summary>A folder with no cover on disk is worked on when it was never tried or its cooldown has passed.</summary>
    public static bool IsMissingFolderDue(AlbumCoverFetchAttempt? attempt, DateTime now)
        => attempt is null
            || attempt.Status is AlbumCoverFetchStatus.UpgradeNotFound or AlbumCoverFetchStatus.UpgradeFailed
            || (attempt.NextRetryAfterUtc is not null && attempt.NextRetryAfterUtc <= now);

    /// <summary>The same for a folder whose Cover Art Archive cover is a candidate for a Spotify one.</summary>
    public static bool IsUpgradeDue(AlbumCoverFetchAttempt? attempt, DateTime now)
        => attempt is null
            || attempt.Status is AlbumCoverFetchStatus.NotFound or AlbumCoverFetchStatus.Failed
            || (attempt.NextRetryAfterUtc is not null && attempt.NextRetryAfterUtc <= now);

    /// <summary>Folders whose newest cover write came from the Cover Art Archive, the ones Spotify may replace.</summary>
    public static HashSet<string> CoverArtArchiveFolders(IEnumerable<CoverWriteRecord> writes)
        => writes
            .GroupBy(w => w.Folder, StringComparer.Ordinal)
            .Where(g =>
            {
                var latest = g.OrderByDescending(w => w.WrittenAtUtc).ThenByDescending(w => w.Id).First();
                return string.Equals(latest.NewValue, CoverArtArchiveSource, StringComparison.OrdinalIgnoreCase);
            })
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);
}

using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Download;

/// <summary>What deciding whether a song is due and its priority needs; the pass loads these few columns for the whole library.</summary>
public sealed record PrefetchSong(
    int Id,
    string? Artist,
    string? Title,
    int? DurationMs,
    int? DurationSeconds,
    int Priority,
    DateTime AddedAt);

public sealed record PrefetchSet(
    int SongId,
    string QueryKey,
    DateTime? NextSearchAtUtc,
    DateTime SearchedAtUtc,
    bool HasError,
    int EmptySearches);

/// <summary>
/// Which songs <see cref="MusicVideoCandidatePrefetch"/> searches for, in the order it takes them, and when one
/// is due. The prefetch and the Background work page both read it from here, so the page cannot count a song
/// the pass would not pick. The queries start from the context and lift its query filters themselves: a
/// background scope has no caller, and the admin restriction below is the scope wanted.
/// </summary>
public static class MusicVideoPrefetchScope
{
    /// <summary>
    /// Priority tiers for video search and automatic downloads (lower runs first): the owner's own
    /// "My tracks" first, then other built library tracks (albums, artists, playlists), then unbuilt
    /// rows waiting on review or build.
    /// </summary>
    public const int PriorityMyTracks = 0;
    public const int PriorityOtherBuilt = 1;
    public const int PriorityUnbuilt = 2;

    /// <summary>
    /// Live, non-duplicate songs of an admin that enrichment has finished with and that have a title:
    /// the search is by artist and title, and enrichment is what corrects them.
    /// </summary>
    public static IQueryable<SongMetadata> Eligible(MusicHoarderDbContext db)
    {
        var adminIds = db.Users.IgnoreQueryFilters()
            .Where(u => u.Role == UserRole.Admin)
            .Select(u => u.Id);
        return db.Songs.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.DeletedAtUtc == null
                && !s.IsDuplicate
                && !s.IsSynthetic
                && adminIds.Contains(s.OwnerUserId)
                && s.EnrichmentStatus != EnrichmentStatus.Pending
                && s.Title != null && s.Title != "");
    }

    /// <summary>
    /// The video rows of eligible songs: every video count the Background work page shows. Duplicates
    /// and soft-deleted songs are outside <see cref="Eligible"/> and therefore excluded here too, so
    /// the search and downloads sheets always agree on every count (files left on disk by those rows
    /// may still appear in the storage snapshot until cleaned up).
    /// </summary>
    public static IQueryable<SongMusicVideo> VideosOfEligible(MusicHoarderDbContext db)
    {
        var eligible = Eligible(db).Select(s => s.Id);
        return db.SongMusicVideos.IgnoreQueryFilters().AsNoTracking().Where(v => eligible.Contains(v.SongId));
    }

    /// <summary>
    /// The eligible songs without a working video. A video that failed is exactly when a list of alternatives
    /// is wanted, so only a working or queued one excludes.
    /// </summary>
    public static IQueryable<SongMetadata> InPrefetchScope(MusicHoarderDbContext db)
        => Eligible(db).Where(s => !db.SongMusicVideos.IgnoreQueryFilters()
            .Any(v => v.SongId == s.Id && v.Status != MusicVideoStatus.Failed));

    /// <summary>
    /// Whether the search runs at all: only where music videos are used, so an instance that has never fetched
    /// one and has not asked for them to be downloaded automatically gets no YouTube traffic from it.
    /// </summary>
    public static async Task<bool> HasStartedAsync(MusicHoarderDbContext db, bool autoDownload, CancellationToken ct)
        => autoDownload || await db.SongMusicVideos.IgnoreQueryFilters().AnyAsync(ct);

    /// <summary>
    /// Newest first within each priority tier: a song just added is searched before the back catalogue.
    /// A song with no acquisition stamp is ordered by when it was indexed.
    /// </summary>
    public static IOrderedQueryable<SongMetadata> InPrefetchOrder(this IQueryable<SongMetadata> songs)
        => songs.OrderByDescending(s => s.AcquiredAtUtc ?? s.IndexedAtUtc).ThenByDescending(s => s.Id);

    /// <summary>
    /// The single priority ordering shared by <see cref="MusicVideoCandidatePrefetch"/> and the
    /// Background work sheet: highest priority tier first (My tracks, then other built, then unbuilt),
    /// newest addition first within each tier, then song id descending.
    /// </summary>
    public static IOrderedEnumerable<T> OrderByPriority<T>(
        IEnumerable<T> items,
        Func<T, int> priority,
        Func<T, DateTime> addedAt,
        Func<T, int> id) =>
        items.OrderBy(priority).ThenByDescending(addedAt).ThenByDescending(id);

    /// <summary>
    /// Priority tier for a song: <see cref="PriorityMyTracks"/> for tracks in "My tracks",
    /// <see cref="PriorityOtherBuilt"/> for other built library tracks (collections, album fills,
    /// synced), and <see cref="PriorityUnbuilt"/> for unbuilt rows.
    /// </summary>
    public static int PriorityOf(
        Guid ownerUserId,
        int songId,
        string sourcePath,
        string? spotifyId,
        DateTime? likedAtUtc,
        SongAcquisitionIntent acquisitionIntent,
        LibraryBuildStatus buildStatus,
        string? destinationPath,
        EnrichmentStatus enrichmentStatus,
        SpotifySaveDates saveDates,
        string? downloadDirectory,
        string? syncedSourceDirectory)
    {
        var origin = SongOriginResolver.Resolve(
            sourcePath, saveDates.LinkFor(songId), downloadDirectory, syncedSourceDirectory);
        var isBuilt = buildStatus == LibraryBuildStatus.Done && destinationPath is not null;
        var isSpotifyLiked = saveDates.SaveDateFor(
            ownerUserId, songId, spotifyId,
            origin.Source == SongOriginSource.SpotifyLiked ? origin.SpotifyAddedAtUtc : null) is not null;

        if (SongOriginResolver.IsInMyTracks(
            origin, acquisitionIntent, isBuilt, enrichmentStatus,
            isLiked: likedAtUtc is not null, isSpotifyLiked))
            return PriorityMyTracks;

        return isBuilt ? PriorityOtherBuilt : PriorityUnbuilt;
    }

    /// <summary>No list yet, the schedule has come round, or the song's search changed since the list was made.</summary>
    public static bool IsDue(PrefetchSong song, PrefetchSet? set, DateTime now) =>
        set is null
        || set.NextSearchAtUtc is not { } next
        || next <= now
        || set.QueryKey != MusicVideoCandidateCache.QueryKeyFor(song.Artist, song.Title, song.DurationMs, song.DurationSeconds);

    /// <summary>
    /// Whether an in-scope song's stored candidate set is eligible to drain directly into automatic
    /// downloads without re-searching: no video row of any status, not due for a fresh search (so
    /// not missing, overdue, or out of date), and the last search succeeded with candidates. Shared
    /// by the prefetch drain and the "Confident picks waiting" figure on the downloads sheet.
    /// </summary>
    public static bool IsWaitingStoredPickCandidate(
        bool inScope, bool hasVideoRow, bool isDue, bool hasError, int emptySearches) =>
        inScope && !hasVideoRow && !isDue && !hasError && emptySearches == 0;
}

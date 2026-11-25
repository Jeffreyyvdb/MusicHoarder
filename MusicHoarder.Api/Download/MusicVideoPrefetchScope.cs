using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Download;

/// <summary>What deciding whether a song is due needs; the pass loads these few columns for the whole library.</summary>
public sealed record PrefetchSong(int Id, string? Artist, string? Title, int? DurationMs, int? DurationSeconds);

public sealed record PrefetchSet(int SongId, string QueryKey, DateTime? NextSearchAtUtc);

/// <summary>
/// Which songs <see cref="MusicVideoCandidatePrefetch"/> searches for, in the order it takes them, and when one
/// is due. The prefetch and the Background work page both read it from here, so the page cannot count a song
/// the pass would not pick. The queries start from the context and lift its query filters themselves: a
/// background scope has no caller, and the admin restriction below is the scope wanted.
/// </summary>
public static class MusicVideoPrefetchScope
{
    /// <summary>
    /// Live songs of an admin that enrichment has finished with and that have a title: the search is by artist
    /// and title, and enrichment is what corrects them.
    /// </summary>
    public static IQueryable<SongMetadata> Eligible(MusicHoarderDbContext db)
    {
        var adminIds = db.Users.IgnoreQueryFilters()
            .Where(u => u.Role == UserRole.Admin)
            .Select(u => u.Id);
        return db.Songs.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.DeletedAtUtc == null
                && adminIds.Contains(s.OwnerUserId)
                && s.EnrichmentStatus != EnrichmentStatus.Pending
                && s.Title != null && s.Title != "");
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
    /// Newest first: a song just added is searched before the back catalogue. A song with no acquisition stamp is
    /// ordered by when it was indexed.
    /// </summary>
    public static IOrderedQueryable<SongMetadata> InPrefetchOrder(this IQueryable<SongMetadata> songs)
        => songs.OrderByDescending(s => s.AcquiredAtUtc ?? s.IndexedAtUtc).ThenByDescending(s => s.Id);

    /// <summary>No list yet, the schedule has come round, or the song's search changed since the list was made.</summary>
    public static bool IsDue(PrefetchSong song, PrefetchSet? set, DateTime now) =>
        set is null
        || set.NextSearchAtUtc is not { } next
        || next <= now
        || set.QueryKey != MusicVideoCandidateCache.QueryKeyFor(song.Artist, song.Title, song.DurationMs, song.DurationSeconds);
}

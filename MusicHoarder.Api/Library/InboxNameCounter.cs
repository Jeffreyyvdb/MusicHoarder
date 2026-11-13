using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Library;

/// <summary>
/// How many suggestions the Inbox's two name queues hold. <see cref="Artists"/> is the Artist names
/// page's rows (spelling clusters plus combined credits), <see cref="Albums"/> the Album names
/// page's (split albums plus duplicate-name pairs).
/// </summary>
public sealed record InboxNameCounts(int Artists, int Albums);

public interface IInboxNameCounter
{
    /// <summary>
    /// The counts the three name detectors' reports would have for this owner, without building or
    /// sending the reports.
    /// </summary>
    Task<InboxNameCounts> CountAsync(Guid ownerUserId, CancellationToken ct = default);
}

/// <summary>
/// The app shell shows two numbers beside the Inbox's name queues, on every page load. Getting them
/// from the detectors' own endpoints meant three reads of the whole library, three full detections
/// and three reports sent to be counted. This reads the library once, for all three, and asks each
/// detector for its count — which the two duplicate detectors remember until a name changes.
/// </summary>
public sealed class InboxNameCounter(
    MusicHoarderDbContext db,
    IArtistDuplicateService artistDuplicates,
    IAlbumDuplicateDetector albumDuplicates,
    IAlbumSplitHealer splitHealer) : IInboxNameCounter
{
    /// <summary>Everything any of the three reads, or filters on, in one projection.</summary>
    internal static readonly SongColumnSet Columns = SongColumnSet.Union(
        ArtistDuplicateService.CountColumns,
        AlbumDuplicateDetector.CountColumns,
        AlbumSplitHealer.CountColumns);

    public async Task<InboxNameCounts> CountAsync(Guid ownerUserId, CancellationToken ct = default)
    {
        // The part of the three detectors' own queries they all share; each applies the rest of its
        // filter to these rows. Every account's, because the split-album report is instance-wide.
        // In id order so that an unchanged library reads the same way twice: the duplicate counts
        // are remembered under a stamp of these rows in the order they come.
        var library = await db.Songs
            .IgnoreQueryFilters()
            .Where(s => s.DeletedAtUtc == null && !s.IsSynthetic)
            .ExcludingDemoTenant()
            .OrderBy(s => s.Id)
            .WithOnly(db, Columns)
            .ToListAsync(ct);

        var artists = await artistDuplicates.CountAsync(ownerUserId, library, ct);
        var albumPairs = await albumDuplicates.CountAsync(ownerUserId, library, ct);
        // Last: the split dry run rewrites the rows it is given, and the other two must stamp and
        // read them as the database has them.
        var splitAlbums = await splitHealer.CountAsync(library, ct);

        return new InboxNameCounts(artists, splitAlbums + albumPairs);
    }
}

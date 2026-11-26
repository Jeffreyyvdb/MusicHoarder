using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using Npgsql;

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
/// <para>
/// On PostgreSQL, an L1 row-version stamp over every table the three detectors read lets repeated
/// calls return immediately without transferring 10,000+ rows or re-running the unmemoized
/// split-album dry run whenever nothing in those tables has moved. A song's version there is its
/// <see cref="RowVersions.Names"/>, which a play, a like, a lyrics fetch or a build leaves alone:
/// the detectors load only <see cref="Columns"/>, and none of those writes touches one of them.
/// </para>
/// </summary>
public sealed class InboxNameCounter : IInboxNameCounter
{
    private readonly MusicHoarderDbContext _db;
    private readonly IArtistDuplicateService _artistDuplicates;
    private readonly IAlbumDuplicateDetector _albumDuplicates;
    private readonly IAlbumSplitHealer _splitHealer;
    private readonly DetectionCountMemo? _memo;
    private readonly IOptions<MusicEnricherOptions>? _options;

    private static string? _versionSql;

    public InboxNameCounter(
        MusicHoarderDbContext db,
        IArtistDuplicateService artistDuplicates,
        IAlbumDuplicateDetector albumDuplicates,
        IAlbumSplitHealer splitHealer,
        DetectionCountMemo? memo = null,
        IOptions<MusicEnricherOptions>? options = null)
    {
        _db = db;
        _artistDuplicates = artistDuplicates;
        _albumDuplicates = albumDuplicates;
        _splitHealer = splitHealer;
        _memo = memo;
        _options = options;
    }

    /// <summary>Everything any of the three reads, or filters on, in one projection.</summary>
    internal static readonly SongColumnSet Columns = SongColumnSet.Union(
        ArtistDuplicateService.CountColumns,
        AlbumDuplicateDetector.CountColumns,
        AlbumSplitHealer.CountColumns);

    public async Task<InboxNameCounts> CountAsync(Guid ownerUserId, CancellationToken ct = default)
    {
        byte[]? versionStamp = null;
        if (_memo is not null && _options is not null && _db.Database.IsNpgsql())
        {
            versionStamp = await TryComputePostgresStampAsync(ownerUserId, ct);
            if (versionStamp is not null && _memo.TryGetPair("InboxNameCounter:pg", ownerUserId, versionStamp, out var cached))
                return new InboxNameCounts(cached.First, cached.Second);
        }

        // The part of the three detectors' own queries they all share; each applies the rest of its
        // filter to these rows. Every account's, because the split-album report is instance-wide.
        // In id order so that an unchanged library reads the same way twice: the duplicate counts
        // are remembered under a stamp of these rows in the order they come.
        var library = await _db.Songs
            .IgnoreQueryFilters()
            .Where(s => s.DeletedAtUtc == null && !s.IsSynthetic)
            .ExcludingDemoTenant()
            .OrderBy(s => s.Id)
            .WithOnly(_db, Columns)
            .ToListAsync(ct);

        var artists = await _artistDuplicates.CountAsync(ownerUserId, library, ct);
        var albumPairs = await _albumDuplicates.CountAsync(ownerUserId, library, ct);
        // Last: the split dry run rewrites the rows it is given, and the other two must stamp and
        // read them as the database has them.
        var splitAlbums = await _splitHealer.CountAsync(library, ct);

        var counts = new InboxNameCounts(artists, splitAlbums + albumPairs);
        if (_memo is not null && versionStamp is not null)
            _memo.SetPair("InboxNameCounter:pg", ownerUserId, versionStamp, (counts.Artists, counts.Albums));

        return counts;
    }

    private async Task<byte[]?> TryComputePostgresStampAsync(Guid ownerUserId, CancellationToken ct)
    {
        var opts = _options!.Value;
        var rows = await _db.Database
            .SqlQueryRaw<string>(
                _versionSql ??= BuildVersionSql(_db),
                new NpgsqlParameter<Guid>("owner", ownerUserId),
                new NpgsqlParameter<Guid>("demo", Auth.WellKnownUsers.DemoId))
            .ToListAsync(ct);

        using var stamp = new ContentStamp();
        stamp.Add(rows.Single());
        stamp.Add(opts.ArtistMergeFuzzyThreshold);
        stamp.Add(opts.ArtistMergeIdentityFuzzyThreshold);
        stamp.Add(opts.DuplicateMaxBlockSize);
        stamp.Add(opts.AlbumMergeFuzzyThreshold);
        stamp.Add(opts.EnableCanonicalDrivenBuild);
        stamp.Add(opts.IdentityTitleThreshold);
        stamp.Add(opts.IdentityArtistThreshold);
        return stamp.Finish();
    }

    private static string BuildVersionSql(MusicHoarderDbContext db)
    {
        var model = db.Model;
        var sql = db.GetService<ISqlGenerationHelper>();

        string Table<T>()
        {
            var entity = model.FindEntityType(typeof(T))!;
            return sql.DelimitIdentifier(entity.GetTableName()!, entity.GetSchema());
        }

        string Column<T>(string property)
        {
            var entity = model.FindEntityType(typeof(T))!;
            var table = StoreObjectIdentifier.Create(entity, StoreObjectType.Table)!.Value;
            return sql.DelimitIdentifier(entity.FindProperty(property)!.GetColumnName(table)!);
        }

        return $"""
            SELECT concat_ws('|',
              pg_postmaster_start_time()::text,
              (SELECT {RowVersions.FoldSql("s", Column<SongMetadata>(nameof(SongMetadata.Id)), RowVersions.Kept("s", RowVersions.Names))}
                 FROM {Table<SongMetadata>()} s
                WHERE s.{Column<SongMetadata>(nameof(SongMetadata.DeletedAtUtc))} IS NULL
                  AND NOT s.{Column<SongMetadata>(nameof(SongMetadata.IsSynthetic))}
                  AND s.{Column<SongMetadata>(nameof(SongMetadata.OwnerUserId))} <> @demo),
              (SELECT {RowVersions.FoldSql("d", Column<DedupDismissal>(nameof(DedupDismissal.Id)), RowVersions.Physical("d"))}
                 FROM {Table<DedupDismissal>()} d
                WHERE d.{Column<DedupDismissal>(nameof(DedupDismissal.OwnerUserId))} = @owner),
              (SELECT {RowVersions.FoldSql("c", Column<CanonicalAlbum>(nameof(CanonicalAlbum.Id)), RowVersions.Physical("c"))}
                 FROM {Table<CanonicalAlbum>()} c),
              (SELECT {RowVersions.FoldSql("t", Column<CanonicalAlbumTrack>(nameof(CanonicalAlbumTrack.Id)), RowVersions.Physical("t"))}
                 FROM {Table<CanonicalAlbumTrack>()} t),
              (SELECT {RowVersions.FoldSql("a", Column<ArtistAlias>(nameof(ArtistAlias.Id)), RowVersions.Physical("a"))}
                 FROM {Table<ArtistAlias>()} a),
              (SELECT {RowVersions.FoldSql("m", Column<SongMetadataChange>(nameof(SongMetadataChange.Id)), RowVersions.Physical("m"))}
                 FROM {Table<SongMetadataChange>()} m
                WHERE m.{Column<SongMetadataChange>(nameof(SongMetadataChange.Source))} = '{AlbumSplitHealer.ChangeSource}'
                  AND m.{Column<SongMetadataChange>(nameof(SongMetadataChange.FieldName))} = '{nameof(SongMetadata.AlbumArtist)}')
            ) AS "Value"
            """;
    }
}

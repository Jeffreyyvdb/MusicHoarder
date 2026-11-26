using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Sharing;
using MusicHoarder.Api.Spotify;
using Npgsql;

namespace MusicHoarder.Api.Library;

/// <summary>
/// A short value that changes whenever anything <c>GET /songs</c> or <c>GET /api/albums</c> is
/// computed from changes for the calling account. It is what lets those two lists answer "nothing
/// changed" (see <see cref="LibraryConditionalGetFilter"/>) without reading and serialising the
/// library to find out.
/// </summary>
public interface ILibraryChangeStamp
{
    /// <summary>
    /// The caller's stamp right now, or null when none can be given — the lists then go out without
    /// a validator, exactly as they did before there was one.
    /// </summary>
    Task<string?> ComputeAsync(CancellationToken ct);
}

/// <summary>
/// <para>
/// <b>What it covers is a contract with the two handlers.</b> A stamp that misses an input lets a
/// client keep an out-of-date library, which is the one failure this must never have; a stamp that
/// covers too much only costs a download that was not needed. So it reads row <i>versions</i>, not
/// values, and errs wide. Everything <c>ListSongs</c>, <c>ListAlbums</c>,
/// <see cref="SpotifySaveDates"/>, <see cref="LibraryScopeResolver"/> and
/// <see cref="SharedSongProjection"/> read:
/// </para>
/// <list type="bullet">
/// <item><c>Songs</c> of the caller and of every account sharing with them — every row, deleted,
/// duplicate and unbuilt ones included, since <c>includeDeleted</c> and the grant scopes pick among
/// them.</item>
/// <item><c>SongMusicVideos</c> of those songs.</item>
/// <item>The caller's <c>WishlistSources</c>, their <c>WishlistItems</c> that link a song, and their
/// liked-songs rows in <c>SpotifyTrackLibraryMatches</c> that carry a save date.</item>
/// <item>Every <c>LibraryShareGrants</c> row naming the caller as grantee, the granting accounts'
/// <c>Users</c> rows (the "Shared by" name), and the caller's <c>UserSongStates</c>.</item>
/// <item>The two directory options a song's origin is derived from, and who is asking.</item>
/// </list>
/// <para>
/// Nothing in those code paths reads the clock or the disk. <b>A new read there has to be added
/// here in the same change</b> — or, if it cannot be expressed as a row version (the time of day, a
/// file's presence), that response must go out without a validator.
/// </para>
/// <para>
/// A row's version is its primary key with a value that is replaced whenever the row is written
/// and never comes back. For <c>Songs</c>, <c>SongMusicVideos</c> and <c>WishlistSources</c>, the
/// three tables background jobs write around the clock, that is the <see cref="RowVersions.List"/>
/// column a trigger keeps: it moves on every insert and on every update except one that changes
/// only the bookkeeping columns <see cref="RowVersions"/> lists, none of which the handlers read.
/// Without that the wishlist sync's two-minute "last synced" stamp alone meant the lists never
/// answered "unchanged". For the other tables it is Postgres's own <c>xmin</c> and <c>ctid</c>,
/// which move on every write. Every insert, update and delete that counts moves the per-table
/// count or sum — which is what makes it safe for the stamp to be read a moment before the
/// handler's own queries: a write landing in between leaves the client holding a stamp older than
/// its data, so its next request simply downloads again. Comparing values instead would let a
/// change that is later undone look like no change at all.
/// </para>
/// <para>
/// The statement goes around the EF tenancy filter, so it is scoped by hand: the caller's id for
/// their own rows, and for anyone else's only the grantor ids <see cref="ILibraryScopeResolver"/>
/// resolved for this request. It returns counts and sums, never a row, and reads all of a grantor's
/// song versions rather than the granted subset — so a grantee's lists are re-sent whenever that
/// library changes at all, which reveals that something changed and nothing more.
/// </para>
/// </summary>
public sealed class LibraryChangeStamp(
    MusicHoarderDbContext db,
    ICurrentUserAccessor currentUser,
    ILibraryScopeResolver scopeResolver,
    IOptions<MusicEnricherOptions> enricherOptions,
    IOptions<SyncOptions> syncOptions,
    ILogger<LibraryChangeStamp> logger) : ILibraryChangeStamp
{
    /// <summary>
    /// Makes a stamp meaningful only to the process that issued it. Whatever is fixed for the life of
    /// a process — the build and so the shape of the two responses, the JSON settings, the bound
    /// options, the runtime's collation data that orders album cards — therefore never has to be
    /// tracked: after a restart or a deploy every client downloads once more.
    /// </summary>
    private static readonly string ProcessId = Guid.NewGuid().ToString("N");

    private static string? _sql;

    public async Task<string?> ComputeAsync(CancellationToken ct)
    {
        var callerId = currentUser.UserId;
        // Row versions exist only in Postgres. The test suite's in-memory provider has none, and
        // without a caller the tenancy filter is not scoping to anyone the stamp could name.
        if (callerId == Guid.Empty || !db.Database.IsNpgsql())
            return null;

        try
        {
            var scope = await scopeResolver.ResolveAsync(db, ct);
            var grantorIds = scope.GrantedSlices.Select(s => s.GrantorUserId).ToArray();
            var rowVersions = await ReadRowVersionsAsync(callerId, grantorIds, ct);

            return Compose(
                ProcessId,
                callerId,
                grantorIds,
                enricherOptions.Value.DownloadDirectory,
                syncOptions.Value.SyncedSourceDirectory,
                rowVersions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The list itself may still be answerable; it just goes out without a validator.
            logger.LogWarning(ex, "Could not compute the library change stamp; answering without a validator.");
            return null;
        }
    }

    /// <summary>
    /// Folds everything the lists depend on into one value. The grantor ids go in as resolved, in
    /// order: the lists append shared songs grantor by grantor, and naming them here also means row
    /// versions read for one set of grantors can never be taken for another's.
    /// </summary>
    internal static string Compose(
        string processId,
        Guid callerId,
        IReadOnlyList<Guid> grantorIds,
        string? downloadDirectory,
        string? syncedSourceDirectory,
        string rowVersions)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, processId);
        Append(hash, callerId.ToString("N"));
        Append(hash, grantorIds.Count.ToString());
        foreach (var grantorId in grantorIds)
            Append(hash, grantorId.ToString("N"));
        Append(hash, downloadDirectory);
        Append(hash, syncedSourceDirectory);
        Append(hash, rowVersions);
        return WebEncoders.Base64UrlEncode(hash.GetHashAndReset());
    }

    /// <summary>Length-prefixed, so two neighbouring parts can never run together into a third.</summary>
    private static void Append(IncrementalHash hash, string? part)
    {
        var bytes = Encoding.UTF8.GetBytes(part ?? string.Empty);
        hash.AppendData(BitConverter.GetBytes(part is null ? -1 : bytes.Length));
        hash.AppendData(bytes);
    }

    private async Task<string> ReadRowVersionsAsync(Guid callerId, Guid[] grantorIds, CancellationToken ct)
    {
        // One statement, so one snapshot: every table is read as of the same instant.
        var rows = await db.Database
            .SqlQueryRaw<string>(
                _sql ??= BuildSql(db),
                new NpgsqlParameter<Guid>("caller", callerId),
                new NpgsqlParameter<Guid[]>("owners", [callerId, .. grantorIds]),
                new NpgsqlParameter<Guid[]>("grantors", grantorIds),
                new NpgsqlParameter<string>("likedSync", SpotifyLibraryComparisonService.SourceLikedSync))
            .ToListAsync(ct);
        return rows.Single();
    }

    /// <summary>
    /// The statement, with table and column names taken from the EF model so a rename migration
    /// cannot leave it reading a name that no longer exists. Built once: the names are the same in
    /// every account's compiled model.
    /// </summary>
    private static string BuildSql(MusicHoarderDbContext db)
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

        var songs = Table<SongMetadata>();
        var songId = Column<SongMetadata>(nameof(SongMetadata.Id));
        var songOwner = Column<SongMetadata>(nameof(SongMetadata.OwnerUserId));

        // The server's start time comes first: row versions are only comparable within one run of
        // one database server. A restore to an earlier point hands the same transaction ids out
        // again for different changes, and cannot happen without a restart.
        return $"""
            SELECT concat_ws('|',
              pg_postmaster_start_time()::text,
              (SELECT {RowVersions.FoldSql("s", songId, RowVersions.Kept("s", RowVersions.List))}
                 FROM {songs} s
                WHERE s.{songOwner} = ANY (@owners)),
              (SELECT {RowVersions.FoldSql("v", Column<SongMusicVideo>(nameof(SongMusicVideo.Id)), RowVersions.Kept("v", RowVersions.List))}
                 FROM {Table<SongMusicVideo>()} v
                 JOIN {songs} s ON s.{songId} = v.{Column<SongMusicVideo>(nameof(SongMusicVideo.SongId))}
                WHERE s.{songOwner} = ANY (@owners)),
              (SELECT {RowVersions.FoldSql("w", Column<WishlistSource>(nameof(WishlistSource.Id)), RowVersions.Kept("w", RowVersions.List))}
                 FROM {Table<WishlistSource>()} w
                WHERE w.{Column<WishlistSource>(nameof(WishlistSource.OwnerUserId))} = @caller),
              (SELECT {RowVersions.FoldSql("i", Column<WishlistItem>(nameof(WishlistItem.Id)), RowVersions.Physical("i"))}
                 FROM {Table<WishlistItem>()} i
                WHERE i.{Column<WishlistItem>(nameof(WishlistItem.OwnerUserId))} = @caller
                  AND i.{Column<WishlistItem>(nameof(WishlistItem.DownloadedSongId))} IS NOT NULL),
              (SELECT {RowVersions.FoldSql("m", Column<SpotifyTrackLibraryMatch>(nameof(SpotifyTrackLibraryMatch.SpotifyTrackId)), RowVersions.Physical("m"))}
                 FROM {Table<SpotifyTrackLibraryMatch>()} m
                WHERE m.{Column<SpotifyTrackLibraryMatch>(nameof(SpotifyTrackLibraryMatch.OwnerUserId))} = @caller
                  AND m.{Column<SpotifyTrackLibraryMatch>(nameof(SpotifyTrackLibraryMatch.Source))} = @likedSync
                  AND m.{Column<SpotifyTrackLibraryMatch>(nameof(SpotifyTrackLibraryMatch.SpotifyAddedAtUtc))} IS NOT NULL),
              (SELECT {RowVersions.FoldSql("g", Column<LibraryShareGrant>(nameof(LibraryShareGrant.Id)), RowVersions.Physical("g"))}
                 FROM {Table<LibraryShareGrant>()} g
                WHERE g.{Column<LibraryShareGrant>(nameof(LibraryShareGrant.GranteeUserId))} = @caller),
              (SELECT {RowVersions.FoldSql("u", Column<User>(nameof(User.Id)), RowVersions.Physical("u"))}
                 FROM {Table<User>()} u
                WHERE u.{Column<User>(nameof(User.Id))} = ANY (@grantors)),
              (SELECT {RowVersions.FoldSql("t", Column<UserSongState>(nameof(UserSongState.Id)), RowVersions.Physical("t"))}
                 FROM {Table<UserSongState>()} t
                WHERE t.{Column<UserSongState>(nameof(UserSongState.UserId))} = @caller)
            ) AS "Value"
            """;
    }
}

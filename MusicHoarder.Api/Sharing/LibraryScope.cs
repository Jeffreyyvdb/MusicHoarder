using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Sharing;

/// <summary>
/// One source of songs the caller may read: either their own rows, or one grantor's rows that a
/// <see cref="LibraryShareGrant"/> exposes to them.
/// </summary>
/// <param name="GrantorUserId">Whose rows these are.</param>
/// <param name="IsSelf">
/// True for the caller's own library. This is the flag every write path must branch on — likes and
/// plays go to the song's own columns when it is true, and to a <see cref="UserSongState"/> row
/// when it is false. Branching on the role instead is wrong: an admin can be a grantee too.
/// </param>
/// <param name="GrantorDisplayName">
/// What the UI shows as "Shared by …". Never the grantor's email — that would leak an address to
/// everyone they shared with.
/// </param>
public sealed record LibrarySlice(Guid GrantorUserId, bool IsSelf, string? GrantorDisplayName);

/// <summary>Everything the calling account may read, split by whose library it comes from.</summary>
public interface ILibraryScope
{
    /// <summary>The caller's own library first, then one slice per granting account.</summary>
    IReadOnlyList<LibrarySlice> Slices { get; }

    /// <summary>Slices that are not the caller's own, in <see cref="Slices"/> order.</summary>
    IReadOnlyList<LibrarySlice> GrantedSlices { get; }

    /// <summary>
    /// The songs one slice exposes.
    ///
    /// <para>
    /// For the self slice this is plain <c>db.Songs</c> with the ambient tenancy filter left ON,
    /// which is what makes the unified endpoints safe: an account that owns no songs — every
    /// member — gets an empty set by construction, with no role check anywhere. For a granted
    /// slice it delegates to <see cref="ISharedLibraryGrantResolver.ScopeSongs"/>, which bypasses
    /// the filter only after a grant resolved and re-scopes explicitly to that grantor's rows.
    /// </para>
    /// </summary>
    IQueryable<SongMetadata> SongsFor(MusicHoarderDbContext db, LibrarySlice slice);
}

public interface ILibraryScopeResolver
{
    /// <summary>
    /// Resolve what the caller may read. Grants are re-read on every call and never cached — a
    /// revoked grant has to stop working immediately, and a cache (or a predicate baked into the
    /// compiled EF model) would keep serving it.
    /// </summary>
    Task<ILibraryScope> ResolveAsync(MusicHoarderDbContext db, CancellationToken ct);

    /// <summary>
    /// Point lookup for stream, cover, lyrics, and video: the song plus the slice it came from,
    /// or null if the caller may not read it. Callers must not distinguish "does not exist" from
    /// "not shared with me" in their response, or song ids become enumerable.
    /// </summary>
    Task<(SongMetadata Song, LibrarySlice Slice)?> ResolveSongAsync(
        MusicHoarderDbContext db, int songId, CancellationToken ct);
}

public sealed class LibraryScopeResolver(
    ICurrentUserAccessor currentUser,
    ISharedLibraryGrantResolver grants) : ILibraryScopeResolver
{
    public async Task<ILibraryScope> ResolveAsync(MusicHoarderDbContext db, CancellationToken ct)
    {
        var callerId = currentUser.UserId;
        var self = new LibrarySlice(callerId, IsSelf: true, GrantorDisplayName: null);

        if (callerId == Guid.Empty)
            return new LibraryScope([self], grants);

        var sets = await grants.ResolveAsync(db, callerId, ct);
        if (sets.Count == 0)
            return new LibraryScope([self], grants);

        var names = await LoadGrantorNamesAsync(db, sets.Select(s => s.OwnerUserId).ToList(), ct);

        var slices = new List<LibrarySlice>(sets.Count + 1) { self };
        foreach (var set in sets)
        {
            slices.Add(new LibrarySlice(
                set.OwnerUserId,
                IsSelf: false,
                GrantorDisplayName: names.GetValueOrDefault(set.OwnerUserId)));
        }

        return new LibraryScope(slices, grants, sets);
    }

    public async Task<(SongMetadata Song, LibrarySlice Slice)?> ResolveSongAsync(
        MusicHoarderDbContext db, int songId, CancellationToken ct)
    {
        var callerId = currentUser.UserId;
        if (callerId == Guid.Empty)
            return null;

        // Own rows first: the ambient filter does the authorization, so this cannot reach another
        // account's library even if grant resolution below is wrong.
        var own = await db.Songs.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == songId && s.DeletedAtUtc == null, ct);
        if (own is not null)
            return (own, new LibrarySlice(callerId, IsSelf: true, GrantorDisplayName: null));

        var shared = await grants.ResolveSongAsync(db, callerId, songId, ct);
        if (shared is null)
            return null;

        var names = await LoadGrantorNamesAsync(db, [shared.OwnerUserId], ct);
        return (shared, new LibrarySlice(
            shared.OwnerUserId,
            IsSelf: false,
            GrantorDisplayName: names.GetValueOrDefault(shared.OwnerUserId)));
    }

    /// <summary>
    /// Display names for attribution. <c>Users</c> carries no query filter, so this is a plain
    /// read. Email is deliberately never a fallback — an unnamed grantor shows as null and the UI
    /// picks the wording.
    /// </summary>
    private static async Task<Dictionary<Guid, string?>> LoadGrantorNamesAsync(
        MusicHoarderDbContext db, IReadOnlyList<Guid> userIds, CancellationToken ct)
    {
        if (userIds.Count == 0) return [];

        return await db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }

    private sealed class LibraryScope(
        IReadOnlyList<LibrarySlice> slices,
        ISharedLibraryGrantResolver grants,
        IReadOnlyList<GrantSet>? sets = null) : ILibraryScope
    {
        public IReadOnlyList<LibrarySlice> Slices { get; } = slices;

        public IReadOnlyList<LibrarySlice> GrantedSlices { get; } =
            slices.Where(s => !s.IsSelf).ToList();

        public IQueryable<SongMetadata> SongsFor(MusicHoarderDbContext db, LibrarySlice slice)
        {
            if (slice.IsSelf)
            {
                // Ambient tenancy filter stays ON. Do not "optimize" this into
                // IgnoreQueryFilters() with an explicit OwnerUserId predicate — the filter is the
                // only thing standing between two admins' libraries.
                return db.Songs.AsNoTracking().Where(s => s.DeletedAtUtc == null);
            }

            var set = sets?.FirstOrDefault(s => s.OwnerUserId == slice.GrantorUserId);
            if (set is null)
                return db.Songs.AsNoTracking().Where(_ => false);

            return grants.ScopeSongs(db, set);
        }
    }
}

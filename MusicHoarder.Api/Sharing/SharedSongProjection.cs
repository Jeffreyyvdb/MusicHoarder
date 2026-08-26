using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Contracts;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Sharing;

/// <summary>
/// Builds the grantee-facing song rows for a resolved <see cref="ILibraryScope"/>.
///
/// <para>
/// One implementation, two callers: the unified <c>GET /songs</c> and the deprecated
/// <c>GET /api/shared/songs</c> alias. Keeping them on the same code path is what makes the alias
/// a genuine compatibility shim rather than a second surface that can drift — and it means the
/// existing <c>SharedLibraryEndpointsTests</c> keep working as a regression net for the unified
/// endpoint.
/// </para>
/// </summary>
public static class SharedSongProjection
{
    public static async Task<(List<SharedSongRowDto> Songs, List<GrantorDto> Grantors)> BuildAsync(
        MusicHoarderDbContext db,
        ILibraryScope scope,
        Guid callerId,
        CancellationToken ct)
    {
        if (scope.GrantedSlices.Count == 0)
            return ([], []);

        // Materialized rather than projected in SQL: the lyric flags come from the computed
        // Display* properties, which have no column to translate to. Grantors are disjoint by
        // construction (ScopeSongs pins every branch to one OwnerUserId), so per-slice queries
        // cannot produce cross-slice duplicates.
        var bySlice = new List<(LibrarySlice Slice, List<SongMetadata> Rows)>();
        foreach (var slice in scope.GrantedSlices)
        {
            var rows = await scope.SongsFor(db, slice)
                .OrderBy(s => s.Artist ?? "")
                .ThenBy(s => s.Album ?? "")
                .ThenBy(s => s.DiscNumber ?? 1)
                .ThenBy(s => s.TrackNumber ?? 0)
                .ThenBy(s => s.Title ?? "")
                .ThenBy(s => s.FileName)
                .ToListAsync(ct);
            bySlice.Add((slice, rows));
        }

        var songIds = bySlice.SelectMany(x => x.Rows).Select(s => s.Id).ToList();
        if (songIds.Count == 0)
        {
            return ([], scope.GrantedSlices
                .Select(s => new GrantorDto(s.GrantorUserId, s.GrantorDisplayName, 0))
                .ToList());
        }

        // Filter bypassed deliberately: SongMusicVideo is scoped by its parent song's owner, so an
        // ambient-filtered read returns nothing for a grantee and every shared track would report
        // "no video". Safe here because songIds is already the authorized set.
        var videoSongIds = await db.SongMusicVideos.IgnoreQueryFilters().AsNoTracking()
            .Where(v => songIds.Contains(v.SongId))
            .Select(v => v.SongId)
            .ToHashSetAsync(ct);

        // The caller's OWN listening state. The explicit UserId predicate sits on top of the
        // ambient filter rather than relying on it, so this stays correct if the filter changes.
        var stateBySongId = await db.UserSongStates.AsNoTracking()
            .Where(f => f.UserId == callerId && songIds.Contains(f.SongId))
            .ToDictionaryAsync(f => f.SongId, ct);

        var songs = new List<SharedSongRowDto>(songIds.Count);
        var grantors = new List<GrantorDto>(bySlice.Count);

        foreach (var (slice, rows) in bySlice)
        {
            foreach (var row in rows)
            {
                songs.Add(SharedSongRowDto.From(
                    row,
                    slice.GrantorUserId,
                    videoSongIds.Contains(row.Id),
                    stateBySongId.GetValueOrDefault(row.Id)));
            }

            grantors.Add(new GrantorDto(slice.GrantorUserId, slice.GrantorDisplayName, rows.Count));
        }

        return (songs, grantors);
    }
}

using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Sharing;

/// <summary>
/// Read-modify-write for a listener's own <see cref="UserSongState"/> row.
///
/// <para>
/// Used only on the not-my-row branch of like and play. If you find yourself calling this for a
/// song the caller owns, the branch is inverted — owned rows write their own columns, which is
/// what Navidrome and instance-sync mirror.
/// </para>
/// </summary>
public static class UserSongStateWriter
{
    public static async Task<UserSongState> UpsertAsync(
        MusicHoarderDbContext db,
        Guid userId,
        int songId,
        Action<UserSongState> mutate,
        CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var state = await db.UserSongStates
                .FirstOrDefaultAsync(s => s.UserId == userId && s.SongId == songId, ct);
            var isNew = state is null;
            state ??= new UserSongState { UserId = userId, SongId = songId };
            mutate(state);
            if (isNew) db.UserSongStates.Add(state);

            try
            {
                await db.SaveChangesAsync(ct);
                return state;
            }
            catch (DbUpdateException) when (isNew && attempt == 0)
            {
                // Someone else inserted the row between our read and write; detach and re-read.
                db.Entry(state).State = EntityState.Detached;
            }
        }
    }
}

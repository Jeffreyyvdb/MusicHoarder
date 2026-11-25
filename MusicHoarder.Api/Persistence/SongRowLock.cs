using Microsoft.EntityFrameworkCore;

namespace MusicHoarder.Api.Persistence;

public static class SongRowLock
{
    /// <summary>
    /// Runs <paramref name="work"/> in a transaction that holds one song's row lock, so nothing else can save
    /// the song between what the work reads and what it writes. For a result computed over a minute or more
    /// (a transcription) that must be checked against the song as it is now before it is stored. The work is
    /// run again if the connection drops, so it must read the song afresh rather than assume earlier state.
    /// Without a relational database (the in-memory test provider) it simply runs.
    /// </summary>
    public static async Task<T> WithSongLockedAsync<T>(
        this MusicHoarderDbContext db, int songId, Func<Task<T>> work, CancellationToken ct)
    {
        if (!db.Database.IsRelational())
            return await work();

        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlAsync(
                $"SELECT 1 FROM \"Songs\" WHERE \"Id\" = {songId} FOR UPDATE", ct);
            var result = await work();
            await tx.CommitAsync(ct);
            return result;
        });
    }

    /// <summary>
    /// Reads <paramref name="song"/> again under its row lock, lets <paramref name="apply"/> change it, and saves
    /// what it changed; <paramref name="apply"/> returns false to leave the song alone. Every writer of a song's
    /// lyrics or transcription whose result took seconds to compute (an LRCLIB lookup, a transcription) saves
    /// this way, so none of them can write back a copy older than another's save.
    /// </summary>
    public static Task<bool> UpdateLatestAsync(
        this MusicHoarderDbContext db, SongMetadata song, Func<bool> apply, CancellationToken ct)
        => db.WithSongLockedAsync(song.Id, async () =>
        {
            await db.Entry(song).ReloadAsync(ct);
            if (!apply())
                return false;
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);
}

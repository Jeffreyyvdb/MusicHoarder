using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Import;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Download;

/// <summary>
/// The steps that queue a music-video fetch, shared by the fetch endpoint (a person's request) and the
/// candidate prefetch (an automatic pick), so the two cannot drift: the song's
/// <see cref="SongMusicVideo"/> row goes to <see cref="MusicVideoStatus.Fetching"/> and the work item
/// reaches <see cref="MusicVideoBackgroundService"/>. The caller has already authorized the song.
/// </summary>
public static class MusicVideoFetchQueue
{
    /// <summary>
    /// A person's request: a fetch of <paramref name="pinnedVideoId"/> verbatim, or a search by artist
    /// and title when null. A row already <see cref="MusicVideoStatus.Fetching"/> queues nothing more.
    /// If an automatic pick wins the unique-index insert between the initial read and save, the
    /// winning row is loaded and returned (already <see cref="MusicVideoStatus.Fetching"/>) rather
    /// than failing with a duplicate-key error.
    /// </summary>
    public static async Task<SongMusicVideo> RequestAsync(
        MusicHoarderDbContext db, MusicVideoChannel channel, int songId, string? pinnedVideoId, CancellationToken ct)
    {
        var video = await db.SongMusicVideos.IgnoreQueryFilters()
            .FirstOrDefaultAsync(v => v.SongId == songId, ct);
        if (video is { Status: MusicVideoStatus.Fetching })
            return video; // already queued/in-flight — don't stack a second fetch

        try
        {
            return await QueueAsync(db, channel, video, songId, pinnedVideoId, MusicVideoPick.Requested, ct);
        }
        catch (DbUpdateException) when (video is null)
        {
            db.ChangeTracker.Clear();
            var existing = await db.SongMusicVideos.IgnoreQueryFilters()
                .FirstOrDefaultAsync(v => v.SongId == songId, ct);
            if (existing is null)
                throw;
            if (existing.Status == MusicVideoStatus.Fetching)
                return existing;
            return await QueueAsync(db, channel, existing, songId, pinnedVideoId, MusicVideoPick.Requested, ct);
        }
    }

    /// <summary>
    /// An automatic pick of <paramref name="videoId"/>. Never touches a song that has any row, whatever
    /// its status: a video a person chose stays theirs, and a failed one waits for a person, or the
    /// next pass would retry the same failing video forever. Null when it was refused for that.
    /// <para>
    /// On PostgreSQL the insert is atomic (<c>ON CONFLICT ("SongId") DO NOTHING</c>) so a manual pick
    /// racing between check and insert cannot throw a unique violation and abort the prefetch pass.
    /// When <paramref name="estimatedBytes"/> is positive, the reservation is recorded before the item
    /// reaches <paramref name="channel"/> so a fast worker cannot release before reserve runs.
    /// </para>
    /// </summary>
    public static async Task<SongMusicVideo?> TryPickAsync(
        MusicHoarderDbContext db,
        MusicVideoChannel channel,
        int songId,
        string videoId,
        CancellationToken ct,
        long estimatedBytes = 0)
    {
        if (db.Database.IsNpgsql())
        {
            var inserted = await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "SongMusicVideos"
                    ("SongId", "Status", "Pick", "YouTubeVideoId", "SyncOffsetMs", "SyncSource", "FetchedAtUtc")
                VALUES ({songId}, {(int)MusicVideoStatus.Fetching}, {(int)MusicVideoPick.Automatic}, {videoId},
                    {0}, {(int)MusicVideoSyncSource.Unaligned}, {DateTime.SpecifyKind(default, DateTimeKind.Utc)})
                ON CONFLICT ("SongId") DO NOTHING
                """, ct);
            if (inserted == 0)
                return null;

            channel.ReserveBytes(songId, estimatedBytes);
            channel.Enqueue(new MusicVideoWorkItem(
                songId, MusicVideoWorkKind.Fetch, ImportUrlParser.YouTubeWatchUrl(videoId)));
            return await db.SongMusicVideos.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(v => v.SongId == songId, ct);
        }

        var exists = await db.SongMusicVideos.IgnoreQueryFilters().AnyAsync(v => v.SongId == songId, ct);
        if (exists)
            return null;

        try
        {
            return await QueueAsync(db, channel, null, songId, videoId, MusicVideoPick.Automatic, ct, estimatedBytes);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return null;
        }
    }

    private static async Task<SongMusicVideo> QueueAsync(
        MusicHoarderDbContext db,
        MusicVideoChannel channel,
        SongMusicVideo? video,
        int songId,
        string? pinnedVideoId,
        MusicVideoPick pick,
        CancellationToken ct,
        long estimatedBytes = 0)
    {
        if (video is null)
        {
            video = new SongMusicVideo { SongId = songId };
            db.SongMusicVideos.Add(video);
        }

        video.Status = MusicVideoStatus.Fetching;
        video.LastError = null;
        video.Pick = pick;
        // Stamp the pinned id so a restart's re-enqueue (which only has the row) retries the same video.
        if (pinnedVideoId is not null)
            video.YouTubeVideoId = pinnedVideoId;
        await db.SaveChangesAsync(ct);

        if (estimatedBytes > 0)
            channel.ReserveBytes(songId, estimatedBytes);
        channel.Enqueue(new MusicVideoWorkItem(
            songId, MusicVideoWorkKind.Fetch,
            pinnedVideoId is null ? null : ImportUrlParser.YouTubeWatchUrl(pinnedVideoId)));
        return video;
    }
}

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
    /// </summary>
    public static async Task<SongMusicVideo> RequestAsync(
        MusicHoarderDbContext db, MusicVideoChannel channel, int songId, string? pinnedVideoId, CancellationToken ct)
    {
        var video = await db.SongMusicVideos.IgnoreQueryFilters()
            .FirstOrDefaultAsync(v => v.SongId == songId, ct);
        if (video is { Status: MusicVideoStatus.Fetching })
            return video; // already queued/in-flight — don't stack a second fetch

        return await QueueAsync(db, channel, video, songId, pinnedVideoId, MusicVideoPick.Requested, ct);
    }

    /// <summary>
    /// An automatic pick of <paramref name="videoId"/>. Never touches a song that has any row, whatever
    /// its status: a video a person chose stays theirs, and a failed one waits for a person, or the
    /// next pass would retry the same failing video forever. Null when it was refused for that.
    /// </summary>
    public static async Task<SongMusicVideo?> TryPickAsync(
        MusicHoarderDbContext db, MusicVideoChannel channel, int songId, string videoId, CancellationToken ct)
    {
        var exists = await db.SongMusicVideos.IgnoreQueryFilters().AnyAsync(v => v.SongId == songId, ct);
        return exists
            ? null
            : await QueueAsync(db, channel, null, songId, videoId, MusicVideoPick.Automatic, ct);
    }

    private static async Task<SongMusicVideo> QueueAsync(
        MusicHoarderDbContext db,
        MusicVideoChannel channel,
        SongMusicVideo? video,
        int songId,
        string? pinnedVideoId,
        MusicVideoPick pick,
        CancellationToken ct)
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

        channel.Enqueue(new MusicVideoWorkItem(
            songId, MusicVideoWorkKind.Fetch,
            pinnedVideoId is null ? null : ImportUrlParser.YouTubeWatchUrl(pinnedVideoId)));
        return video;
    }
}

using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.History.Sources;

/// <summary>
/// Music videos: which clip MusicHoarder went and got for a track, how it decided to line it up with
/// the audio, and the fetches that came back empty.
/// <para>
/// "Which candidate got rejected as an album-cover upload" is deliberately not here: that decision is
/// logged, not stored, and the outcome the owner cares about — whether the clip is the track's own
/// source upload or a real video found by searching — is exactly what
/// <see cref="MusicVideoSyncSource"/> already records.
/// </para>
/// </summary>
public sealed class MusicVideoActivitySource(MusicHoarderDbContext db) : IActivitySource
{
    public async Task<IReadOnlyList<HistorySummary>> CollectAsync(ActivityWindow window, CancellationToken ct)
    {
        var videos = await db.SongMusicVideos
            .AsNoTracking()
            .Where(v => v.FetchedAtUtc >= window.FromUtc && v.FetchedAtUtc <= window.ToUtc
                && v.Status != MusicVideoStatus.Fetching)
            .OrderByDescending(v => v.FetchedAtUtc)
            .Take(window.MaxRowsPerSource)
            .Select(v => new
            {
                v.Id, v.SongId, v.Status, v.SyncSource, v.SyncOffsetMs, v.YouTubeVideoId, v.LastError,
                v.FetchedAtUtc,
                Title = v.Song.Title,
                Album = v.Song.Album,
                AlbumArtist = v.Song.AlbumArtist,
                Artist = v.Song.Artist,
            })
            .ToListAsync(ct);

        var summaries = new List<HistorySummary>();

        foreach (var v in videos.Where(v => v.Status == MusicVideoStatus.Ready))
        {
            summaries.Add(ActivityText.Summary(
                ActivityCategory.Video, "video-added", ActivityTint.Ok,
                idKey: v.Id.ToString(),
                headline: $"Got a music video for {ActivityText.Quote(v.Title)}",
                detail: DescribeChoice(v.SyncSource, v.SyncOffsetMs, v.YouTubeVideoId),
                items: [new ActivityItem(v.SongId, v.Title, v.Album, v.AlbumArtist ?? v.Artist, v.FetchedAtUtc)]));
        }

        summaries.AddRange(ActivityText.GroupByAlbumDay(
            ActivityCategory.Video, "video-failed", ActivityTint.Warn,
            videos
                .Where(v => v.Status == MusicVideoStatus.Failed)
                .Select(v => new ActivityItem(
                    v.SongId, v.Title, v.Album, v.AlbumArtist ?? v.Artist, v.FetchedAtUtc,
                    ActivityText.Clip(v.LastError))),
            rows => $"No music video for {ActivityText.Plural(rows.Count, "track")} of {ActivityText.Quote(rows[0].Album)}",
            rows => ActivityText.Clip(rows[0].Detail)));

        return summaries;
    }

    private static string DescribeChoice(MusicVideoSyncSource sync, int offsetMs, string? youTubeId)
    {
        var source = youTubeId is null ? "" : $" · youtube.com/watch?v={youTubeId}";
        return sync switch
        {
            MusicVideoSyncSource.SameSource =>
                $"The track's own source upload — already in perfect sync{source}",
            MusicVideoSyncSource.AutoAligned =>
                $"Found by searching, then aligned automatically ({ActivityText.Seconds(offsetMs)} offset){source}",
            MusicVideoSyncSource.Manual =>
                $"Found by searching, lined up by you ({ActivityText.Seconds(offsetMs)} offset){source}",
            _ => $"Found by searching — not aligned to the audio yet{source}",
        };
    }
}

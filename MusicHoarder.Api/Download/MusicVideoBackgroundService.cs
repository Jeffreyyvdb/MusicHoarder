using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Import;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Download;

/// <summary>
/// Consumes <see cref="MusicVideoChannel"/>: downloads music videos (manual per-song fetches) and
/// (re-)estimates audio↔video sync offsets. A plain hosted service, never a JobManager step — video
/// fetches are slow network side-work that must not hold the pipeline's one-job lock. The queue is
/// in-memory, so startup re-enqueues rows left <see cref="MusicVideoStatus.Fetching"/> by a restart.
/// </summary>
public class MusicVideoBackgroundService(
    IServiceScopeFactory scopeFactory,
    MusicVideoChannel channel,
    IMusicVideoDownloader downloader,
    IHttpClientFactory httpClientFactory,
    ILogger<MusicVideoBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ResetStaleFetchingAsync(stoppingToken);

        await foreach (var work in channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
                var alignment = scope.ServiceProvider.GetRequiredService<MusicVideoAlignmentService>();
                await ProcessAsync(db, alignment, work, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Music video worker failed for song {SongId} ({Kind})", work.SongId, work.Kind);
            }
        }
    }

    /// <summary>
    /// Re-enqueues rows a restart left mid-fetch. The pinned video id survives on the row (stamped at
    /// enqueue time when the fetch had an explicit URL), so the retry hits the same video.
    /// </summary>
    private async Task ResetStaleFetchingAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
            // IgnoreQueryFilters: background scope → the tenant filter resolves to Guid.Empty and
            // would hide every row.
            var stale = await db.SongMusicVideos
                .IgnoreQueryFilters()
                .Where(v => v.Status == MusicVideoStatus.Fetching)
                .Select(v => new { v.SongId, v.YouTubeVideoId })
                .ToListAsync(ct);
            if (stale.Count == 0)
                return;

            logger.LogInformation("Re-enqueued {Count} unfinished music video fetch(es) on startup", stale.Count);
            foreach (var row in stale)
            {
                var url = row.YouTubeVideoId is null ? null : ImportUrlParser.YouTubeWatchUrl(row.YouTubeVideoId);
                channel.Enqueue(new MusicVideoWorkItem(row.SongId, MusicVideoWorkKind.Fetch, url));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reset stale music video fetches");
        }
    }

    private async Task ProcessAsync(
        MusicHoarderDbContext db, MusicVideoAlignmentService alignment, MusicVideoWorkItem work, CancellationToken ct)
    {
        var song = await db.Songs
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == work.SongId && s.DeletedAtUtc == null, ct);
        if (song is null)
        {
            logger.LogInformation("Skipping music video work for missing/deleted song {SongId}", work.SongId);
            return;
        }

        var video = await db.SongMusicVideos
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(v => v.SongId == work.SongId, ct);

        switch (work.Kind)
        {
            case MusicVideoWorkKind.Fetch:
                await FetchAsync(db, alignment, song, video, work.ExplicitUrl, ct);
                break;

            case MusicVideoWorkKind.Align:
                if (video is { Status: MusicVideoStatus.Ready })
                {
                    await EnsureThumbnailAsync(song, video, ct);
                    // Manual offsets stand until something invalidates them (the upgrade-merge hook
                    // resets to Unaligned first); SameSource is 0 by construction — for those this
                    // work item is effectively just the thumbnail ensure above.
                    if (video.SyncSource is MusicVideoSyncSource.Unaligned or MusicVideoSyncSource.AutoAligned)
                        await alignment.AlignAsync(song, video, ct);
                    await db.SaveChangesAsync(ct);
                }
                break;
        }
    }

    private async Task FetchAsync(
        MusicHoarderDbContext db,
        MusicVideoAlignmentService alignment,
        SongMetadata song,
        SongMusicVideo? video,
        string? explicitUrl,
        CancellationToken ct)
    {
        if (video is null)
        {
            video = new SongMusicVideo { SongId = song.Id, Status = MusicVideoStatus.Fetching };
            db.SongMusicVideos.Add(video);
        }
        video.Status = MusicVideoStatus.Fetching;
        video.LastError = null;
        await db.SaveChangesAsync(ct);

        var previousPath = video.FilePath;
        // A URL typed into the video field is an explicit choice — honored verbatim. Search-based
        // fetches carry the song duration so candidate scoring can sanity-check lengths.
        var result = await downloader.DownloadAsync(
            new MusicVideoFetchRequest(
                explicitUrl, PinIsExplicit: explicitUrl is not null,
                song.Artist ?? string.Empty, song.Title ?? string.Empty,
                song.DurationMs ?? song.DurationSeconds * 1000),
            ct);

        if (result is { Success: true, FilePath: not null })
        {
            // Refetch replaces the file — drop the old one and its thumbnail (managed artifacts).
            if (previousPath is not null && previousPath != result.FilePath)
            {
                TryDeleteFile(previousPath);
                TryDeleteFile(Path.ChangeExtension(previousPath, ".jpg"));
            }

            video.FilePath = result.FilePath.Replace('\\', '/');
            video.YouTubeVideoId = result.YouTubeVideoId;
            video.DurationSeconds = result.DurationSeconds;
            video.Status = MusicVideoStatus.Ready;
            video.FetchedAtUtc = DateTime.UtcNow;
            // The song's audio was acquired independently of this video, so estimate the offset.
            video.SyncSource = MusicVideoSyncSource.Unaligned;
            video.SyncOffsetMs = 0;
            video.SyncConfidence = null;
            await alignment.AlignAsync(song, video, ct);
            await EnsureThumbnailAsync(song, video, ct);
        }
        else if (previousPath is not null && File.Exists(previousPath))
        {
            // A refetch failed (e.g. a transient YouTube 403) but the previous video is still on
            // disk — keep it usable instead of clobbering Ready, and surface the error alongside.
            video.Status = MusicVideoStatus.Ready;
            video.LastError = result.Error ?? (result.NotFound ? "no video found" : "download failed");
        }
        else
        {
            video.Status = MusicVideoStatus.Failed;
            video.LastError = result.Error ?? (result.NotFound ? "no video found" : "download failed");
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Keeps the video's YouTube thumbnail next to the mp4 (<c>&lt;stem&gt;.jpg</c>). For an artless
    /// song it doubles as the cover: the cover endpoint falls back to it, so flag
    /// <see cref="SongMetadata.HasCoverArt"/> (real art always wins in the endpoint's priority
    /// order, and enrichment's cover pipeline replaces it later). Best-effort — a missing
    /// thumbnail never fails the video.
    /// </summary>
    private async Task EnsureThumbnailAsync(SongMetadata song, SongMusicVideo video, CancellationToken ct)
    {
        if (video.FilePath is null || string.IsNullOrWhiteSpace(video.YouTubeVideoId))
            return;

        var thumbnailPath = Path.ChangeExtension(video.FilePath, ".jpg");
        if (!File.Exists(thumbnailPath))
        {
            var client = httpClientFactory.CreateClient();
            // maxres only exists for HD uploads; hqdefault exists for every video.
            foreach (var variant in new[] { "maxresdefault", "hqdefault" })
            {
                try
                {
                    using var response = await client.GetAsync(
                        $"https://i.ytimg.com/vi/{video.YouTubeVideoId}/{variant}.jpg", ct);
                    if (!response.IsSuccessStatusCode)
                        continue;
                    var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                    if (bytes.Length < 1024)
                        continue; // placeholder/soft-404
                    await File.WriteAllBytesAsync(thumbnailPath, bytes, ct);
                    logger.LogInformation("Saved music video thumbnail for song {SongId} ({Variant})", song.Id, variant);
                    break;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    logger.LogDebug(ex, "Music video thumbnail fetch failed for song {SongId}", song.Id);
                }
            }
        }

        if (File.Exists(thumbnailPath) && !song.HasCoverArt)
            song.HasCoverArt = true;
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete replaced music video file");
        }
    }
}

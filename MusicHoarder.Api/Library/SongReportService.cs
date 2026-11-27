using System.IO.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Logging;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Library;

public enum SongReportOutcome
{
    Reported,
    NotFound,

    /// <summary>The song's file sits in the owner's own source library, which MusicHoarder never deletes from.</summary>
    NotRemovable,

    /// <summary>The downloaded file could not be deleted; nothing was changed.</summary>
    DeleteFailed,
}

public sealed record SongReportResult(SongReportOutcome Outcome, SongReport? Report = null, string? Message = null);

/// <summary>
/// "This download is wrong": records a <see cref="SongReport"/> and, when asked, takes the song out of
/// the library.
/// <para>
/// Removal has to stick, which a bare <see cref="SongMetadata.SoftDelete"/> does not: the scanner
/// treats a file whose row is soft-deleted as new and brings the row back. So the downloaded file is
/// deleted too, and that is only allowed for files MusicHoarder put there itself — the download
/// staging and slskd directories, as <c>UpgradeMergeService</c> decides. A file in the owner's source
/// library is never touched (the non-destructive rule), so such a song can be reported but not removed
/// here. The file goes first and the row after: a scan that lands in between reads the missing file as
/// a deletion, which is the same outcome.
/// </para>
/// <para>
/// The wishlist items that pointed at the song become parked failures carrying the reason, rather than
/// staying "Downloaded": the track is no longer in the library, a parked failure is not retried on its
/// own (album completion reads it as a tombstone, and a playlist sync keeps the existing row), and the
/// wishlist's Retry is there for a person who wants another attempt.
/// </para>
/// </summary>
public class SongReportService(
    MusicHoarderDbContext db,
    IFileSystem fileSystem,
    ILibraryDestinationCleaner destinationCleaner,
    IOptions<MusicEnricherOptions> options,
    IOptionsMonitor<SlskdOptions> slskdOptions,
    Assets.AudioAssetRegistry assetRegistry,
    ILogger<SongReportService> logger)
{
    public const string WishlistReportedError = "Reported as a wrong download and removed from the library.";

    public async Task<SongReportResult> ReportAsync(
        Guid ownerId, int songId, SongReportReason reason, string? note, bool remove, CancellationToken ct)
    {
        var song = await db.Songs
            .FirstOrDefaultAsync(s => s.Id == songId && s.OwnerUserId == ownerId && s.DeletedAtUtc == null, ct);
        if (song is null)
            return new SongReportResult(SongReportOutcome.NotFound, Message: $"Song {songId} not found.");

        var items = await db.WishlistItems
            .Where(w => w.OwnerUserId == ownerId && w.DownloadedSongId == song.Id)
            .ToListAsync(ct);
        // The item that fetched this file, when several point at the song (one Downloaded, others
        // SkippedOwned because the track was already here when they synced).
        var fetcher = items.FirstOrDefault(w => w.DownloadedFilePath == song.SourcePath)
            ?? items.FirstOrDefault(w => w.Status == WishlistItemStatus.Downloaded)
            ?? items.OrderBy(w => w.Id).FirstOrDefault();

        var now = DateTime.UtcNow;
        var report = Snapshot(ownerId, song, fetcher, reason, note, now);

        if (remove)
        {
            var sourceIsManaged = IsManaged(song.SourcePath);
            if (song.SourceReleasedAtUtc is null && !sourceIsManaged)
            {
                return new SongReportResult(SongReportOutcome.NotRemovable, Message:
                    "This file is in your own music folder, which MusicHoarder never deletes from. "
                    + "Delete it there and the next scan drops it from the library.");
            }

            // A stored file another song plays stays, and so does this song: deleting the row alone
            // would only have the next scan bring it back.
            if (song.SourceReleasedAtUtc is null && !await assetRegistry.TryPurgeForSongAsync(db, song.SourcePath, song.Id, ct))
            {
                return new SongReportResult(SongReportOutcome.NotRemovable, Message:
                    "Another song plays this same stored file, so it was left in the library.");
            }

            // The staged download first: if it stays, the next scan brings the song back, so a failure
            // here changes nothing at all.
            if (song.SourceReleasedAtUtc is null && !TryDelete(song.SourcePath))
            {
                return new SongReportResult(SongReportOutcome.DeleteFailed, Message:
                    "The downloaded file could not be deleted, so the song was left in the library.");
            }

            // The library copy. Only ever under the destination root (the cleaner refuses anything
            // else), and a failure leaves an orphan file rather than a song that comes back.
            if (!string.IsNullOrWhiteSpace(song.DestinationPath))
            {
                try
                {
                    destinationCleaner.DeleteManagedPathAndPrune(song.DestinationPath, options.Value.DestinationDirectory);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Report of song {SongId}: could not delete the library copy {Path}",
                        song.Id, LogSanitizer.ForLog(song.DestinationPath));
                }
            }

            // Its music video was fetched for the same wrong download.
            var video = await db.SongMusicVideos.FirstOrDefaultAsync(v => v.SongId == song.Id, ct);
            if (video is not null)
            {
                if (video.FilePath is not null)
                {
                    TryDelete(video.FilePath);
                    TryDelete(fileSystem.Path.ChangeExtension(video.FilePath, ".jpg"));
                }
                db.SongMusicVideos.Remove(video);
            }

            song.SoftDelete();
            report.RemovedAtUtc = now;

            foreach (var item in items)
            {
                item.Status = WishlistItemStatus.Failed;
                item.NextAttemptAtUtc = null;
                item.LastError = WishlistReportedError;
                item.DownloadedSongId = null;
                item.DownloadedFilePath = null;
                item.DownloadedVideoFilePath = null;
                item.UpdatedAtUtc = now;
            }
        }

        db.SongReports.Add(report);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Song {SongId} reported as {Reason} (provider {Provider}, removed {Removed})",
            song.Id, reason, report.DownloadProvider ?? "none", report.RemovedAtUtc is not null);

        return new SongReportResult(SongReportOutcome.Reported, report);
    }

    private static SongReport Snapshot(
        Guid ownerId, SongMetadata song, WishlistItem? item, SongReportReason reason, string? note, DateTime now) => new()
    {
        OwnerUserId = ownerId,
        SongId = song.Id,
        Reason = reason,
        Note = Clip(string.IsNullOrWhiteSpace(note) ? null : note.Trim(), 1000),
        Title = Clip(song.Title, 512),
        Artist = Clip(song.Artist, 512),
        Album = Clip(song.Album, 512),
        DurationMs = song.DurationMs ?? (song.DurationSeconds is { } seconds ? seconds * 1000 : null),
        FileName = Clip(song.FileName, 512),
        MatchedBy = Clip(song.MatchedBy, 64),
        MatchConfidence = song.MatchConfidence,
        WishlistItemId = item?.Id,
        Origin = item?.Origin,
        RequestedTitle = Clip(item?.Title, 512),
        RequestedArtist = Clip(item?.Artist, 512),
        RequestedAlbum = Clip(item?.Album, 512),
        RequestedDurationMs = item is { DurationMs: > 0 } ? item.DurationMs : null,
        SpotifyTrackId = item?.SpotifyTrackId,
        DeezerTrackId = item?.DeezerTrackId,
        Isrc = item?.Isrc ?? Clip(song.Isrc, 32),
        DownloadProvider = item?.DownloadProvider,
        DownloadedSourceId = item?.DownloadedSourceId,
        SourceUrl = item?.SourceUrl,
        CreatedAtUtc = now,
    };

    /// <summary>A file MusicHoarder wrote itself, and so may delete.</summary>
    private bool IsManaged(string path)
    {
        var roots = new[] { options.Value.DownloadDirectory, slskdOptions.CurrentValue.DownloadsDirectory };
        return roots.Any(root => !string.IsNullOrWhiteSpace(root) && SongOriginResolver.IsUnder(path, root));
    }

    private bool TryDelete(string path)
    {
        try
        {
            if (fileSystem.File.Exists(path))
                fileSystem.File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete {Path}", LogSanitizer.ForLog(path));
            return false;
        }
    }

    private static string? Clip(string? value, int maxLength)
        => value is null || value.Length <= maxLength ? value : value[..maxLength];
}

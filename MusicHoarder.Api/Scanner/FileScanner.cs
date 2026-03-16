using System.IO.Abstractions;
using MusicHoarder.Api.Metadata;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Scanner;

public interface IFileScanner
{
    /// <summary>Reads tags and runs fpcalc for a single file. Returns null on unrecoverable error.</summary>
    Task<SongMetadata?> ScanFileAsync(string filePath, CancellationToken ct = default);
}

public class FileScanner(
    IFileSystem fileSystem,
    IFpcalcService fpcalcService,
    ILogger<FileScanner> logger) : IFileScanner
{
    public async Task<SongMetadata?> ScanFileAsync(string filePath, CancellationToken ct = default)
    {
        var fileName = fileSystem.Path.GetFileName(filePath);
        var extension = fileSystem.Path.GetExtension(filePath).ToLowerInvariant();

        try
        {
            var fileInfo = fileSystem.FileInfo.New(filePath);
            var fileSize = fileInfo.Length;
            var lastModified = fileInfo.LastWriteTimeUtc;

            string? artist = null;
            string? albumArtist = null;
            string? album = null;
            string? title = null;
            int? year = null;
            int? trackNumber = null;
            int? durationMs = null;

            try
            {
                using var tagFile = TagLib.File.Create(filePath);
                var tag = tagFile.Tag;

                album = NullIfEmpty(tag.Album);
                albumArtist = tag.AlbumArtists?.Length > 0
                    ? NullIfEmpty(tag.AlbumArtists[0])
                    : null;
                artist = tag.Performers?.Length > 0
                    ? NullIfEmpty(tag.Performers[0])
                    : NullIfEmpty(tag.FirstPerformer);
                artist ??= albumArtist;
                albumArtist ??= ArtistCreditNormalizer.GetPrimaryArtist(artist);
                title = NullIfEmpty(tag.Title);
                year = tag.Year != 0 ? (int)tag.Year : null;
                trackNumber = tag.Track != 0 ? (int)tag.Track : null;

                if (tagFile.Properties?.Duration.TotalMilliseconds > 0)
                    durationMs = (int)tagFile.Properties.Duration.TotalMilliseconds;
            }
            catch (Exception ex)
            {
                logger.LogDebug("Could not read tags from {File}: {Message}", fileName, ex.Message);
            }

            var fpcalcResult = await fpcalcService.GetFingerprintAsync(filePath, ct);

            return new SongMetadata
            {
                SourcePath = filePath,
                FileName = fileName,
                Extension = extension,
                FileSizeBytes = fileSize,
                LastModifiedUtc = lastModified,
                Artist = artist,
                AlbumArtist = albumArtist,
                Album = album,
                Title = title,
                Year = year,
                TrackNumber = trackNumber,
                DurationSeconds = fpcalcResult?.DurationSeconds,
                DurationMs = durationMs ?? (fpcalcResult?.DurationSeconds is { } sec ? sec * 1000 : null),
                Fingerprint = fpcalcResult?.Fingerprint,
                IndexedAtUtc = DateTime.UtcNow,
                DeletedAtUtc = null
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Failed to process {File}: {Message}", fileName, ex.Message);
            return null;
        }
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

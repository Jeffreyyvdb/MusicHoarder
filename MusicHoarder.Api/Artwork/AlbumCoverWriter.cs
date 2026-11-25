using System.IO.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Artwork;

/// <summary>
/// Outcome of a cover write attempt. <see cref="Source"/> is <c>"source"</c> when the art came from
/// the source files, or the external provider tag (<c>spotify</c>/<c>coverartarchive</c>/<c>deezer</c>/<c>itunes</c>).
/// <see cref="TransientFailure"/> reports an external-provider error/rate-limit so the sweep can pick
/// a shorter retry cooldown than a clean "no provider has it".
/// <see cref="Skipped"/> indicates a targeted upgrade could not run (provider disabled or credentials
/// missing) and should not be recorded as a miss.
/// </summary>
public sealed record AlbumCoverWriteResult(
    bool Written,
    string? Source,
    bool TransientFailure = false,
    bool Skipped = false);

/// <summary>
/// Writes a destination album folder's <c>cover.&lt;ext&gt;</c> from a representative source track,
/// so Navidrome (pointed at the destination) shows folder-level art. Shared by the library builder
/// (post-batch pass) and the one-time backfill so the skip/extension/best-effort rules live in one place.
/// </summary>
public interface IAlbumCoverWriter
{
    /// <summary>
    /// Writes <c>cover.&lt;ext&gt;</c> into <paramref name="destinationDirectory"/> — resolving the art
    /// from <paramref name="sourceAudioPath"/> (folder image first, then embedded) — unless the folder
    /// already has a <c>cover/folder/front.*</c> image. Best-effort: failures are logged, not thrown.
    /// Returns <c>true</c> if a cover was written.
    /// </summary>
    bool WriteIfMissing(string destinationDirectory, string sourceAudioPath);

    /// <summary>
    /// Like <see cref="WriteIfMissing"/>, but when the source files carry no art it falls back to
    /// fetching a cover from external providers using <paramref name="externalQuery"/> (skipped when
    /// null or when external fetching is disabled).
    /// </summary>
    Task<AlbumCoverWriteResult> WriteIfMissingAsync(
        string destinationDirectory,
        string sourceAudioPath,
        ExternalCoverArtQuery? externalQuery,
        CancellationToken ct = default);

    /// <summary>
    /// Replaces an existing destination folder cover with Spotify's official album cover when Spotify
    /// has a verified match. Leaves existing cover files untouched when Spotify is disabled,
    /// unconfigured, errors, or has no match; removes stale <c>cover.&lt;ext&gt;</c> siblings when the
    /// format changes so an old file cannot shadow the new cover.
    /// </summary>
    Task<AlbumCoverWriteResult> ReplaceWithSpotifyAsync(
        string destinationDirectory,
        ExternalCoverArtQuery externalQuery,
        CancellationToken ct = default);
}

public class AlbumCoverWriter(
    IFileSystem fileSystem,
    ICoverArtResolver coverArtResolver,
    IExternalCoverArtFetcher externalFetcher,
    ICoverResolutionCache coverCache,
    ICoverThumbnailService thumbnails,
    IOptions<MusicEnricherOptions> options,
    ILogger<AlbumCoverWriter> logger) : IAlbumCoverWriter
{
    public bool WriteIfMissing(string destinationDirectory, string sourceAudioPath)
    {
        try
        {
            return DestinationNeedsCover(destinationDirectory)
                && TryWriteFromSource(destinationDirectory, sourceAudioPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write album cover in {Directory}", destinationDirectory);
            return false;
        }
    }

    public async Task<AlbumCoverWriteResult> WriteIfMissingAsync(
        string destinationDirectory,
        string sourceAudioPath,
        ExternalCoverArtQuery? externalQuery,
        CancellationToken ct = default)
    {
        try
        {
            if (!DestinationNeedsCover(destinationDirectory))
            {
                return new AlbumCoverWriteResult(false, null);
            }

            if (TryWriteFromSource(destinationDirectory, sourceAudioPath))
            {
                return new AlbumCoverWriteResult(true, "source");
            }

            if (externalQuery is null || !options.Value.EnableExternalCoverArtFetch)
            {
                return new AlbumCoverWriteResult(false, null);
            }

            var fetched = await externalFetcher.FetchAsync(externalQuery, ct);
            if (fetched.Cover is null)
            {
                return new AlbumCoverWriteResult(false, null, fetched.HadTransientFailure);
            }

            WriteCoverFile(destinationDirectory, fetched.Cover.Bytes, fetched.Cover.ContentType);
            return new AlbumCoverWriteResult(true, fetched.Cover.Source, fetched.HadTransientFailure);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write album cover in {Directory}", destinationDirectory);
            return new AlbumCoverWriteResult(false, null, TransientFailure: true);
        }
    }

    public async Task<AlbumCoverWriteResult> ReplaceWithSpotifyAsync(
        string destinationDirectory,
        ExternalCoverArtQuery externalQuery,
        CancellationToken ct = default)
    {
        try
        {
            if (!options.Value.EnableExternalCoverArtFetch
                || !options.Value.EnableSpotifyCovers
                || !fileSystem.Directory.Exists(destinationDirectory))
            {
                return new AlbumCoverWriteResult(false, null, Skipped: true);
            }

            var fetched = await externalFetcher.FetchSpotifyAsync(externalQuery, ct);
            if (fetched.Skipped)
            {
                return new AlbumCoverWriteResult(false, null, Skipped: true);
            }

            if (fetched.Cover is null)
            {
                return new AlbumCoverWriteResult(false, null, fetched.HadTransientFailure);
            }

            var newPath = WriteCoverFile(destinationDirectory, fetched.Cover.Bytes, fetched.Cover.ContentType);
            RemoveStaleCoverFiles(destinationDirectory, newPath);
            return new AlbumCoverWriteResult(true, fetched.Cover.Source, fetched.HadTransientFailure);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to replace album cover with Spotify art in {Directory}", destinationDirectory);
            return new AlbumCoverWriteResult(false, null, TransientFailure: true);
        }
    }

    private bool DestinationNeedsCover(string destinationDirectory)
        => fileSystem.Directory.Exists(destinationDirectory)
            && !coverArtResolver.DirectoryHasCoverImage(destinationDirectory);

    private bool TryWriteFromSource(string destinationDirectory, string sourceAudioPath)
    {
        var cover = coverArtResolver.Resolve(sourceAudioPath);
        if (cover is null)
        {
            return false;
        }

        var bytes = cover.Bytes
            ?? (cover.FilePath is not null ? fileSystem.File.ReadAllBytes(cover.FilePath) : null);
        if (bytes is null || bytes.Length == 0)
        {
            return false;
        }

        // The resolver already skips an embedded picture whose bytes are not a recognised image, but a
        // folder image (cover.jpg and friends) is taken by file name, so its bytes may still be anything.
        // Don't propagate undecodable bytes to the destination — treating them as "no source art" lets
        // the external fetch supply a real cover instead.
        if (CoverArtResolver.SniffImageMime(bytes) is null)
        {
            logger.LogDebug("Skipping unrecognizable source art for {SourcePath}", sourceAudioPath);
            return false;
        }

        WriteCoverFile(destinationDirectory, bytes, cover.ContentType);
        return true;
    }

    private string WriteCoverFile(string destinationDirectory, byte[] bytes, string contentType)
    {
        var coverPath = fileSystem.Path.Combine(
            destinationDirectory,
            $"cover{CoverArtResolver.ExtensionForContentType(contentType)}");
        fileSystem.File.WriteAllBytes(coverPath, bytes);
        logger.LogInformation("Wrote album cover {CoverPath}", coverPath);

        // A new cover in this folder changes what every song in it resolves to: drop their cached
        // resolutions and prewarm the grid thumbnails of the fresh file.
        coverCache.InvalidatePath(destinationDirectory);
        thumbnails.Prewarm(
            new ResolvedCover { FilePath = coverPath, ContentType = contentType },
            coverPath);
        return coverPath;
    }

    // When a replacement cover changes format (e.g. cover.png → cover.jpg), remove the older
    // cover.<ext> so CoverArtResolver (which checks extensions in priority order) never serves the
    // stale file ahead of the newly written one.
    private void RemoveStaleCoverFiles(string destinationDirectory, string keepPath)
    {
        var keepName = fileSystem.Path.GetFileName(keepPath);
        try
        {
            foreach (var path in fileSystem.Directory.EnumerateFiles(destinationDirectory))
            {
                var fileName = fileSystem.Path.GetFileName(path);
                if (string.Equals(fileName, keepName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var stem = fileSystem.Path.GetFileNameWithoutExtension(fileName);
                var ext = fileSystem.Path.GetExtension(fileName);
                if (!string.Equals(stem, "cover", StringComparison.OrdinalIgnoreCase)
                    || !CoverArtResolver.ImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    fileSystem.File.Delete(path);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Could not remove superseded cover file {Path}", path);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not inspect {Directory} for superseded cover files", destinationDirectory);
        }
    }
}

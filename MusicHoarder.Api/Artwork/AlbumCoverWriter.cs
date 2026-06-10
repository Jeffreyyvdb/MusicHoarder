using System.IO.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Artwork;

/// <summary>
/// Outcome of a cover write attempt. <see cref="Source"/> is <c>"source"</c> when the art came from
/// the source files, or the external provider tag (<c>coverartarchive</c>/<c>deezer</c>/<c>itunes</c>).
/// <see cref="TransientFailure"/> reports an external-provider error/rate-limit so the sweep can pick
/// a shorter retry cooldown than a clean "no provider has it".
/// </summary>
public sealed record AlbumCoverWriteResult(bool Written, string? Source, bool TransientFailure = false);

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
}

public class AlbumCoverWriter(
    IFileSystem fileSystem,
    ICoverArtResolver coverArtResolver,
    IExternalCoverArtFetcher externalFetcher,
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

        // Some taggers embed garbage (the resolver's mime fallback masks it as image/jpeg). Don't
        // propagate undecodable bytes to the destination — treating them as "no source art" lets the
        // external fetch supply a real cover instead.
        if (CoverArtResolver.SniffImageMime(bytes) is null)
        {
            logger.LogDebug("Skipping unrecognizable source art for {SourcePath}", sourceAudioPath);
            return false;
        }

        WriteCoverFile(destinationDirectory, bytes, cover.ContentType);
        return true;
    }

    private void WriteCoverFile(string destinationDirectory, byte[] bytes, string contentType)
    {
        var coverPath = fileSystem.Path.Combine(
            destinationDirectory,
            $"cover{CoverArtResolver.ExtensionForContentType(contentType)}");
        fileSystem.File.WriteAllBytes(coverPath, bytes);
        logger.LogInformation("Wrote album cover {CoverPath}", coverPath);
    }
}

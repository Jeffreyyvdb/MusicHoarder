using Microsoft.Extensions.Options;
using MusicHoarder.Api.Artwork;
using MusicHoarder.Api.Logging;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Download;

public interface IDownloadArtworkEmbedder
{
    /// <summary>
    /// Downloads <paramref name="imageUrl"/> and embeds it as the file's front cover. No-op when the
    /// URL is blank, the image is unusable, or the file already carries artwork. Best-effort: every
    /// failure is logged and swallowed — artwork never fails a download. Returns true when art was
    /// written.
    /// </summary>
    Task<bool> EmbedAsync(string filePath, string? imageUrl, CancellationToken ct = default);
}

/// <summary>
/// Gives a freshly-downloaded file its cover art from the identity the download was requested for —
/// the Spotify album image for a wishlist track, the video thumbnail for a pasted YouTube link.
/// <para>
/// yt-dlp is deliberately run without <c>--embed-thumbnail</c> (see
/// <see cref="YtDlpDownloadProvider"/>), so without this the file reaches the library with no art at
/// all: the source row shows no cover, and the build's cover pass — which searches the external
/// providers <em>by album</em> — has nothing to look up for a one-off single. Embedding here makes the
/// art travel with the file: the scanner flags <c>HasCoverArt</c>, and the build's
/// <see cref="Artwork.AlbumCoverWriter"/> writes it out as the destination folder's <c>cover.jpg</c>.
/// </para>
/// </summary>
public sealed class DownloadArtworkEmbedder(
    HttpClient httpClient,
    IOptions<MusicEnricherOptions> options,
    ILogger<DownloadArtworkEmbedder> logger) : IDownloadArtworkEmbedder
{
    public async Task<bool> EmbedAsync(string filePath, string? imageUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
            return false;

        try
        {
            if (HasPicture(filePath))
                return false;

            var bytes = await DownloadAsync(imageUrl.Trim(), ct);
            if (bytes is null)
                return false;

            // Magic bytes only — the CDN's content-type is not trusted, and a mime that doesn't match
            // the payload makes players drop the art silently.
            var mime = CoverArtResolver.SniffImageMime(bytes);
            if (mime is null)
            {
                logger.LogDebug("Download artwork rejected for {Path}: not a recognizable image", filePath);
                return false;
            }

            using var tagFile = TagLib.File.Create(filePath);
            tagFile.Tag.Pictures =
            [
                new TagLib.Picture(new TagLib.ByteVector(bytes))
                {
                    Type = TagLib.PictureType.FrontCover,
                    MimeType = mime,
                    Description = "Cover",
                },
            ];
            tagFile.Save();
            logger.LogInformation("Embedded cover art ({Bytes} bytes) into {Path}", bytes.Length, filePath);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                "Could not embed cover art from {Url} into {Path}: {Message}",
                LogSanitizer.ForLog(imageUrl), filePath, ex.Message);
            return false;
        }
    }

    /// <summary>True when the file already carries artwork — a download's own art always wins.</summary>
    private static bool HasPicture(string filePath)
    {
        using var tagFile = TagLib.File.Create(filePath);
        return tagFile.Tag.Pictures?.Length > 0;
    }

    private async Task<byte[]?> DownloadAsync(string url, CancellationToken ct)
    {
        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogDebug(
                "Download artwork fetch failed ({Status}) for {Url}",
                (int)response.StatusCode, LogSanitizer.ForLog(url));
            return null;
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        // Below the floor it's a placeholder or an error page, not a cover (ytimg soft-404s a missing
        // thumbnail variant with a tiny image rather than a 404).
        if (bytes.Length < options.Value.ExternalCoverArtMinImageBytes)
        {
            logger.LogDebug("Download artwork rejected: {Bytes} bytes is below the minimum", bytes.Length);
            return null;
        }

        return bytes;
    }
}

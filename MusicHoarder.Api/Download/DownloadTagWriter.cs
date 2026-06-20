using MusicHoarder.Api.Metadata;

namespace MusicHoarder.Api.Download;

/// <summary>
/// Stamps a freshly-downloaded file with the <b>known</b> wishlist identity (the Spotify artist/title/
/// album/ISRC the download was requested for) so the scanner reads that as the source identity instead
/// of the downloader's native tags. yt-dlp's <c>--embed-metadata</c> wrote the YouTube uploader/channel
/// into ARTIST and the full video title into TITLE — junk that poisons enrichment matching (blocking
/// artist/title mismatch warnings). Writing the authoritative identity here lets enrichment match.
/// <para>
/// Deliberately lightweight: it writes only the source-identity fields a pre-enrichment file needs.
/// The build stage's <c>TagLibLibraryTagWriter</c> is the full writer (MB IDs, lyrics, reconciled
/// album identity) and is not appropriate before enrichment has run.
/// </para>
/// </summary>
public static class DownloadTagWriter
{
    /// <summary>
    /// Overwrites the file's ARTIST/TITLE/ALBUM/ALBUMARTIST/ISRC with the supplied identity and saves.
    /// Tolerant by design — a tag-write failure (unsupported container, missing/locked file) is logged
    /// and swallowed so it never fails the download or the backfill. Returns true when the file was
    /// successfully stamped.
    /// </summary>
    public static bool Stamp(string path, string artist, string title, string? album, string? isrc, ILogger? logger = null)
    {
        try
        {
            using var tagFile = TagLib.File.Create(path);
            var tag = tagFile.Tag;

            tag.Performers = string.IsNullOrWhiteSpace(artist) ? [] : [artist];
            tag.Title = NullIfEmpty(title);
            tag.Album = NullIfEmpty(album);
            // Mirror the scanner's AlbumArtist derivation (FileScanner falls back to GetPrimaryArtist)
            // so a downloaded track groups under the same album-artist the rest of the pipeline expects.
            var albumArtist = ArtistCreditNormalizer.GetPrimaryArtist(artist);
            tag.AlbumArtists = string.IsNullOrWhiteSpace(albumArtist) ? [] : [albumArtist];
            tag.ISRC = NullIfEmpty(isrc) ?? string.Empty;

            tagFile.Save();
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogDebug("Could not stamp download tags on {Path}: {Message}", path, ex.Message);
            return false;
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

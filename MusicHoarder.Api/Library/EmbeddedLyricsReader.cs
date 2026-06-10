namespace MusicHoarder.Api.Library;

/// <summary>
/// Reads the lyrics actually embedded in a file on disk. The lyrics-embed backfill uses this as ground
/// truth — the DB row and the <see cref="WrittenTagSet"/> snapshot both only record what MusicHoarder
/// *intended* to write, which can disagree with the file when an earlier build raced the lyrics fetch.
/// </summary>
public interface IEmbeddedLyricsReader
{
    /// <summary>
    /// The lyrics embedded in the file at <paramref name="path"/>, or null when the file is absent,
    /// unreadable, or carries no lyrics. Whitespace-only is treated as none.
    /// </summary>
    string? ReadEmbeddedLyrics(string path);
}

public sealed class TagLibEmbeddedLyricsReader(ILogger<TagLibEmbeddedLyricsReader> logger) : IEmbeddedLyricsReader
{
    public string? ReadEmbeddedLyrics(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var tagFile = TagLib.File.Create(path);
            var lyrics = tagFile.Tag.Lyrics;
            return string.IsNullOrWhiteSpace(lyrics) ? null : lyrics;
        }
        catch (Exception ex)
        {
            // A corrupt/unsupported file shouldn't fail the backfill — treat it as "can't tell" by
            // returning null. The worst case is one extra re-tag, which is harmless and idempotent.
            logger.LogDebug(ex, "Could not read embedded lyrics from {Path}", path);
            return null;
        }
    }
}

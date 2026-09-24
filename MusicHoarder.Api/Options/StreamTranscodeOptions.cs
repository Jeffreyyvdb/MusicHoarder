using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Options;

/// <summary>
/// On-demand AAC renditions for clients that cannot play a file as it is (Ogg Opus in Safari, say).
/// A stream request only converts when it asks for <c>?format=aac</c>, and the web player only asks
/// when the browser it runs in cannot play the original. Everyone else keeps getting the original
/// bytes, untouched. A rendition is converted once, cached on disk and served with range support,
/// so seeking works exactly as it does on an original file.
/// </summary>
public class StreamTranscodeOptions
{
    public const string SectionName = "StreamTranscode";

    /// <summary>
    /// Where renditions are cached. A disposable directory of derived files: wiping it only makes
    /// them convert again. Falls back to a folder next to the app when this path is not writable.
    /// </summary>
    public string CacheDirectory { get; set; } = "/data/transcode-cache";

    /// <summary>
    /// AAC bitrate. 256 kbps is the rate Apple sells music at, so a rendition of a lossy file adds no
    /// audible loss of its own.
    /// </summary>
    [Range(96, 512)]
    public int AacBitrateKbps { get; set; } = 256;

    /// <summary>How many conversions may run at once; the rest wait for a slot.</summary>
    [Range(1, 8)]
    public int Concurrency { get; set; } = 2;

    /// <summary>
    /// Size the cache is trimmed back to, least recently played first. A four-minute song at 256 kbps
    /// is about 8 MB, so the default holds a few hundred.
    /// </summary>
    [Range(100, 1_000_000)]
    public int CacheMaxMegabytes { get; set; } = 2048;

    /// <summary>
    /// Longest one conversion may take. Kept under the frontend proxy's 240 s wait for a converted
    /// stream, so a stuck ffmpeg surfaces as an error rather than a proxy timeout.
    /// </summary>
    [Range(10, 230)]
    public int TimeoutSeconds { get; set; } = 180;
}

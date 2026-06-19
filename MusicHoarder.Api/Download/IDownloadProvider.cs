namespace MusicHoarder.Api.Download;

/// <summary>
/// A single track to acquire. <paramref name="DestinationDirectory"/> is the absolute directory the
/// file must be written into (the writable download staging dir, which the scanner indexes so the file
/// is ingested by the normal pipeline).
/// </summary>
public record DownloadRequest(
    string Artist,
    string Title,
    string? Album,
    string? Isrc,
    int DurationMs,
    string DestinationDirectory);

/// <summary>
/// Outcome of a download attempt. <paramref name="NotFound"/> distinguishes "no result for this
/// track" (don't retry forever) from a transient <paramref name="Error"/>.
/// </summary>
public record DownloadResult(bool Success, string? FilePath, string? Error, bool NotFound)
{
    public static DownloadResult Ok(string filePath) => new(true, filePath, null, false);
    public static DownloadResult Failed(string error) => new(false, null, error, false);
    public static DownloadResult Missing(string? error = null) => new(false, null, error, true);
}

/// <summary>
/// Pluggable acquisition backend. yt-dlp is the first implementation; slskd / torrents / spotiflac
/// can drop in later behind the same interface, resolved by <see cref="Name"/> from
/// <c>MusicEnricher:DownloadProvider</c>.
/// </summary>
public interface IDownloadProvider
{
    /// <summary>Stable identifier matched against <c>MusicEnricher:DownloadProvider</c>, e.g. "yt-dlp".</summary>
    string Name { get; }

    Task<DownloadResult> DownloadAsync(DownloadRequest req, CancellationToken ct);
}

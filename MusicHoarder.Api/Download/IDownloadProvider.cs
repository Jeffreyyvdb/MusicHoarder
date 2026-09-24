namespace MusicHoarder.Api.Download;

/// <summary>
/// A single track to acquire. <paramref name="DestinationDirectory"/> is the absolute directory the
/// file must be written into (the writable download staging dir, which the scanner indexes so the file
/// is ingested by the normal pipeline).
/// <para>
/// <paramref name="SpotifyTrackId"/> is optional and only used by providers that acquire from a
/// streaming-service URL (e.g. the "spotiflac" provider builds
/// <c>https://open.spotify.com/track/{id}</c> from it). yt-dlp / slskd ignore it and match on
/// artist/title/album instead.
/// </para>
/// <para>
/// <paramref name="SourceUrl"/> is an optional direct URL for a single-track URL import (e.g. a pasted
/// YouTube video). When set, yt-dlp downloads that exact URL instead of searching by artist/title —
/// required for content (remixes/edits) that has no streaming-service equivalent. Providers that can't
/// use a raw URL ignore it.
/// </para>
/// </summary>
public record DownloadRequest(
    string Artist,
    string Title,
    string? Album,
    string? Isrc,
    int DurationMs,
    string DestinationDirectory,
    string? SpotifyTrackId = null,
    string? SourceUrl = null);

/// <summary>
/// Outcome of a download attempt. Three failure kinds, and a provider chain treats them differently:
/// <list type="bullet">
/// <item><paramref name="NotFound"/> — the provider looked and has nothing for this track. The chain
/// moves on to the next provider; if every provider says so the item is terminal NotFound.</item>
/// <item><paramref name="Unavailable"/> — the provider could not be reached at all (DNS failure,
/// connection refused). Nothing was tried against the track, so the chain moves on to the next
/// provider, the item remembers which provider was skipped, and the row is retried later rather than
/// tombstoned.</item>
/// <item>Neither flag (<see cref="Failed"/>) — the provider was reached and the attempt failed
/// (rate limit, 5xx, corrupt download). The chain stops so a flaky backend can't burn the fallback's
/// quota; the item is retried with backoff.</item>
/// </list>
/// <para>
/// <paramref name="SourceId"/> is the provider-specific id of the media the audio actually came from
/// (yt-dlp: the YouTube video id, including for <c>ytsearch1:</c> results). The companion
/// music-video download uses it to fetch the clip from the <em>same</em> video, making the
/// audio/video sync offset 0 by construction. Null for providers without such an id.
/// </para>
/// <para>
/// <paramref name="Isrc"/> is set when the provider deliberately fetched a different edition of the
/// requested track (spotiflac swapping a clean edit for its explicit edition): the ISRC of what was
/// actually downloaded, for the caller to stamp instead of the requested one. Null means the request's.
/// </para>
/// </summary>
public record DownloadResult(
    bool Success,
    string? FilePath,
    string? Error,
    bool NotFound,
    string? SourceId = null,
    bool Unavailable = false,
    string? Isrc = null)
{
    public static DownloadResult Ok(string filePath, string? sourceId = null) => new(true, filePath, null, false, sourceId);
    public static DownloadResult Failed(string error) => new(false, null, error, false);
    public static DownloadResult Missing(string? error = null) => new(false, null, error, true);
    /// <summary>The provider itself was unreachable — see the type docs for how a chain treats this.</summary>
    public static DownloadResult ProviderUnavailable(string error) => new(false, null, error, false, null, true);

    /// <summary>True when the chain should try the next provider rather than stop here.</summary>
    public bool FallsThrough => !Success && (NotFound || Unavailable);
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

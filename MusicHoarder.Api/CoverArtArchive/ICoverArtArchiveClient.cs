namespace MusicHoarder.Api.CoverArtArchive;

/// <summary>A downloaded front cover. <see cref="ContentType"/> is the server-reported type, if any.</summary>
public sealed record CoverArtArchiveImage(byte[] Bytes, string? ContentType);

public interface ICoverArtArchiveClient
{
    /// <summary>
    /// Downloads the front cover for a release MBID (<c>front-1200</c> first, original <c>front</c>
    /// as fallback). Null when the release has no registered art (404) or the response isn't usable.
    /// </summary>
    Task<CoverArtArchiveImage?> GetReleaseFrontAsync(string releaseMbid, CancellationToken ct = default);

    /// <summary>Same as <see cref="GetReleaseFrontAsync"/> for a release-group MBID.</summary>
    Task<CoverArtArchiveImage?> GetReleaseGroupFrontAsync(string releaseGroupMbid, CancellationToken ct = default);
}

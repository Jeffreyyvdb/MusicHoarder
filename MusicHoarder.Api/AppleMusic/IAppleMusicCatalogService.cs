namespace MusicHoarder.Api.AppleMusic;

public interface IAppleMusicCatalogService
{
    /// <summary>Fuzzy artist+title search against the iTunes Search API (<c>entity=song</c>).</summary>
    Task<IReadOnlyList<AppleMusicCatalogTrack>> SearchTracksAsync(string query, CancellationToken ct = default);

    /// <summary>Finds an album (collection) id by artist + album name (<c>entity=album</c>).</summary>
    Task<string?> SearchAlbumIdAsync(string artist, string album, CancellationToken ct = default);

    /// <summary>Fetches an album with its full tracklist (<c>GET /lookup?id=..&amp;entity=song</c>); null if not found.</summary>
    Task<AppleAlbumDetail?> GetAlbumAsync(string collectionId, CancellationToken ct = default);
}

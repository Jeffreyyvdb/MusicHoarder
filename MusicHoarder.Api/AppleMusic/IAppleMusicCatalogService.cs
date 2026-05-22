namespace MusicHoarder.Api.AppleMusic;

public interface IAppleMusicCatalogService
{
    /// <summary>Fuzzy artist+title search against the iTunes Search API (<c>entity=song</c>).</summary>
    Task<IReadOnlyList<AppleMusicCatalogTrack>> SearchTracksAsync(string query, CancellationToken ct = default);
}

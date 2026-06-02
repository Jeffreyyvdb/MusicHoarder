using MusicHoarder.Api.AppleMusic;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment.AlbumTracklist.Providers;

/// <summary>Album tracklists from Apple/iTunes (free, no auth). Resolves the collection id by artist+album search.</summary>
public sealed class AppleMusicAlbumTracklistProvider(
    IAppleMusicCatalogService apple) : IAlbumTracklistProvider
{
    public EnrichmentProvider Source => EnrichmentProvider.AppleMusic;

    public bool IsEnabled(MusicEnricherOptions options) => options.EnableAppleMusicProvider;

    public async Task<AlbumTracklistCandidate?> FetchAsync(AlbumQuery query, CancellationToken ct = default)
    {
        var collectionId = await apple.SearchAlbumIdAsync(query.AlbumArtist, query.Album, ct);
        if (collectionId is null)
            return null;

        var album = await apple.GetAlbumAsync(collectionId, ct);
        if (album is null || album.Tracks.Count == 0)
            return null;

        return new AlbumTracklistCandidate(
            Source,
            album.Id,
            album.Name,
            album.Artist,
            album.Year,
            album.ArtworkUrl,
            album.Tracks
                .Select(t => new CandidateTrack(t.DiscNumber, t.TrackNumber, t.Title, t.DurationMs, t.Id))
                .ToList());
    }
}

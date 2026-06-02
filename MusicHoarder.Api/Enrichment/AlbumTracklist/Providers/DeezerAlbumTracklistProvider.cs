using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment.AlbumTracklist.Providers;

/// <summary>Album tracklists from Deezer (free, no auth). Resolves the album id by artist+album search.</summary>
public sealed class DeezerAlbumTracklistProvider(
    IDeezerCatalogService deezer) : IAlbumTracklistProvider
{
    public EnrichmentProvider Source => EnrichmentProvider.Deezer;

    public bool IsEnabled(MusicEnricherOptions options) => options.EnableDeezerProvider;

    public async Task<AlbumTracklistCandidate?> FetchAsync(AlbumQuery query, CancellationToken ct = default)
    {
        var albumId = await deezer.SearchAlbumIdAsync(query.AlbumArtist, query.Album, ct);
        if (albumId is null)
            return null;

        var album = await deezer.GetAlbumAsync(albumId, ct);
        if (album is null || album.Tracks.Count == 0)
            return null;

        return new AlbumTracklistCandidate(
            Source,
            album.Id,
            album.Title,
            album.Artist,
            album.Year,
            album.CoverUrl,
            album.Tracks
                .Select(t => new CandidateTrack(t.DiscNumber, t.TrackNumber, t.Title, t.DurationMs, t.Id))
                .ToList());
    }
}

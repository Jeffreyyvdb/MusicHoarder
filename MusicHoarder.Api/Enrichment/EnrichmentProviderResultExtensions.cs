using System.Text.Json;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment;

public static class EnrichmentProviderResultExtensions
{
    /// <summary>
    /// Projects a provider's match candidate onto the <see cref="EnrichmentMatchData"/> carrier
    /// consumed by <see cref="SongMetadata.ApplyEnrichmentMatch"/>. The two records are near-twins;
    /// the only transforms are serializing <see cref="EnrichmentProviderResult.MatchWarnings"/> to a
    /// JSON string, mapping <c>MatchConfidence</c> onto <c>AdjustedScore</c>, and substituting the
    /// caller's terminal <paramref name="recommendedStatus"/> for the candidate's own recommendation.
    /// Keeping this mapping in one named place means a field added to both records has a single,
    /// test-covered home instead of being silently dropped on the bulk-approve path.
    /// </summary>
    public static EnrichmentMatchData ToMatchData(this EnrichmentProviderResult candidate, EnrichmentStatus recommendedStatus)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var warningsJson = candidate.MatchWarnings.Count > 0
            ? JsonSerializer.Serialize(candidate.MatchWarnings)
            : null;

        return new EnrichmentMatchData(
            candidate.Artist,
            candidate.AlbumArtist,
            candidate.Title,
            candidate.Year,
            candidate.TrackNumber,
            candidate.MusicBrainzId,
            candidate.MusicBrainzReleaseId,
            candidate.SpotifyId,
            candidate.AcoustIdTrackId,
            candidate.Isrc,
            candidate.MatchedBy,
            candidate.MatchConfidence,
            warningsJson,
            recommendedStatus,
            candidate.Album,
            candidate.Artists,
            candidate.ArtistMusicBrainzIds,
            candidate.AlbumArtistMusicBrainzId,
            candidate.MusicBrainzReleaseGroupId,
            candidate.DiscNumber,
            candidate.TotalDiscs,
            candidate.TotalTracks,
            candidate.IsCompilation,
            candidate.ReleaseTypePrimary,
            candidate.ReleaseTypes,
            candidate.Genre,
            candidate.ReleaseDate,
            candidate.OriginalReleaseDate,
            candidate.Label,
            candidate.CatalogNumber,
            candidate.Upc,
            candidate.Composer,
            candidate.Copyright,
            candidate.ArtistSort,
            candidate.AlbumArtistSort);
    }
}

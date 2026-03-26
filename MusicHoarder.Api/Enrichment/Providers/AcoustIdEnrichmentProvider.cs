using MusicHoarder.Api.Metadata;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment.Providers;

public class AcoustIdEnrichmentProvider(
    IAcoustIdService acoustIdService,
    IAcoustIdMatchValidator matchValidator,
    ILogger<AcoustIdEnrichmentProvider> logger) : IEnrichmentProvider
{
    public string Name => "AcoustID";
    public int Priority => 100;

    public bool CanHandle(SongMetadata song) =>
        !string.IsNullOrWhiteSpace(song.Fingerprint) && song.DurationSeconds is not null;

    public async Task<EnrichmentProviderResult?> TryEnrichAsync(SongMetadata song, CancellationToken ct = default)
    {
        var match = await acoustIdService.LookupAsync(song.Fingerprint!, song.DurationSeconds!.Value, ct);
        if (match is null)
        {
            logger.LogDebug("AcoustID returned no match for song {SongId}", song.Id);
            return null;
        }

        var validation = matchValidator.Validate(match, song);

        var effectiveArtist = string.IsNullOrWhiteSpace(match.Artist) ? song.Artist : match.Artist;
        var resolvedAlbumArtist = string.IsNullOrWhiteSpace(match.AlbumArtist)
            ? ArtistCreditNormalizer.GetPrimaryArtist(effectiveArtist)
            : match.AlbumArtist;

        return new EnrichmentProviderResult(
            Artist: match.Artist,
            AlbumArtist: resolvedAlbumArtist,
            Title: match.Title,
            Year: null,
            TrackNumber: null,
            MusicBrainzId: match.MusicBrainzRecordingId,
            MusicBrainzReleaseId: null,
            SpotifyId: null,
            AcoustIdTrackId: match.AcoustIdTrackId,
            Isrc: null,
            MatchedBy: Name,
            MatchConfidence: validation.AdjustedScore,
            MatchWarnings: validation.Warnings,
            RecommendedStatus: validation.RecommendedStatus);
    }
}

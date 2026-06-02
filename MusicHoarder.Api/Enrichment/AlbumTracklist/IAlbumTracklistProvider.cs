using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment.AlbumTracklist;

/// <summary>
/// Hints — gathered from an album's owned songs — that a provider uses to resolve and fetch the
/// album's full tracklist. Providers prefer a precise identifier (release/track id) and fall back to
/// an artist+album search.
/// </summary>
public sealed record AlbumQuery(
    string AlbumArtist,
    string Album,
    string? MusicBrainzReleaseId,
    string? SpotifyTrackId,
    IReadOnlyList<string> Isrcs,
    int? TotalTracksHint);

/// <summary>One provider's full-tracklist answer for an album. Reconciled against the others.</summary>
public sealed record AlbumTracklistCandidate(
    EnrichmentProvider Source,
    string? ProviderAlbumId,
    string? Title,
    string? AlbumArtist,
    int? Year,
    string? CoverArtUrl,
    IReadOnlyList<CandidateTrack> Tracks);

public sealed record CandidateTrack(
    int DiscNumber,
    int TrackNumber,
    string? Title,
    int? DurationMs,
    string? ProviderRecordingId);

/// <summary>
/// A single metadata source able to return an album's full canonical tracklist. Mirrors
/// <see cref="IEnrichmentProvider"/>: implementations are registered as singletons and gated by the
/// same per-provider enable flags. The fetch service runs every enabled provider for an album and
/// hands the candidates to <see cref="AlbumTracklistReconciler"/>.
/// </summary>
public interface IAlbumTracklistProvider
{
    /// <summary>The provider this source maps to (for per-track corroboration attribution).</summary>
    EnrichmentProvider Source { get; }

    /// <summary>Whether this provider is turned on (mirrors the enrichment Enable* flags).</summary>
    bool IsEnabled(MusicEnricherOptions options);

    /// <summary>Resolves and fetches the album's full tracklist, or null when it can't be found.</summary>
    Task<AlbumTracklistCandidate?> FetchAsync(AlbumQuery query, CancellationToken ct = default);
}

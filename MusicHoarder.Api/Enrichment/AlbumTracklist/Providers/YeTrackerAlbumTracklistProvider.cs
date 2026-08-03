using Microsoft.Extensions.Options;
using MusicHoarder.Api.Enrichment.Providers;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment.AlbumTracklist.Providers;

/// <summary>
/// Album tracklists from the yetracker's committed catalog. Gated to
/// <see cref="MusicEnricherOptions.YeTrackerArtistAllowlist"/> like the enrichment provider — the
/// catalog is single-artist, so an album by anyone else must not be answered for.
/// <para>
/// Unlike every other tracklist provider this one has no durations or recording ids: the tracker
/// documents a running order, not a release. That's exactly what the unreleased eras need, since no
/// mainstream service lists them at all.
/// </para>
/// </summary>
public sealed class YeTrackerAlbumTracklistProvider(
    YeTrackerTracklistCatalogService catalog,
    IOptions<MusicEnricherOptions> options) : IAlbumTracklistProvider
{
    public EnrichmentProvider Source => EnrichmentProvider.YeTracker;

    public bool IsEnabled(MusicEnricherOptions opts) => opts.EnableYeTrackerProvider;

    public Task<AlbumTracklistCandidate?> FetchAsync(AlbumQuery query, CancellationToken ct = default)
    {
        if (!MatchesArtistAllowlist(query.AlbumArtist))
            return Task.FromResult<AlbumTracklistCandidate?>(null);

        var tracklist = catalog.Find(query.Album);
        if (tracklist is null)
            return Task.FromResult<AlbumTracklistCandidate?>(null);

        var candidate = new AlbumTracklistCandidate(
            Source,
            ProviderAlbumId: null,
            Title: tracklist.Album,
            AlbumArtist: query.AlbumArtist,
            Year: tracklist.Year,
            CoverArtUrl: null,
            Tracks: tracklist.Tracks
                .Select(t => new CandidateTrack(1, t.Number, t.Title, null, null))
                .ToList());

        return Task.FromResult<AlbumTracklistCandidate?>(candidate);
    }

    /// <summary>
    /// Same gate as the enrichment provider, via the shared <see cref="TrackerArtistAllowlist"/> —
    /// a plain fuzzy ratio here would let the two-letter "Ye" alias answer for Yeat, Yebba and
    /// Yeule, attaching Ye's running order to their albums.
    /// </summary>
    private bool MatchesArtistAllowlist(string? artist)
        => TrackerArtistAllowlist.Matches(
            artist, options.Value.YeTrackerArtistAllowlist, options.Value.IdentityArtistThreshold);
}

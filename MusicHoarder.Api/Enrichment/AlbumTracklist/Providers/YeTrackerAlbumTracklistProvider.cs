using Microsoft.Extensions.Options;
using MusicHoarder.Api.Matching;
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

    private bool MatchesArtistAllowlist(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return false;

        foreach (var allowed in options.Value.YeTrackerArtistAllowlist)
        {
            if (FuzzyTextMatch.Ratio(artist, allowed) is double ratio
                && ratio >= options.Value.IdentityArtistThreshold)
            {
                return true;
            }
        }
        return false;
    }
}

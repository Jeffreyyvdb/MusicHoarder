using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment.AlbumTracklist.Providers;

/// <summary>
/// Album tracklists from MusicBrainz. Uses a song's stored release MBID when present, else resolves
/// one by searching releases for the album artist + title.
/// </summary>
public sealed class MusicBrainzAlbumTracklistProvider(
    IMusicBrainzWebService musicBrainz,
    ILogger<MusicBrainzAlbumTracklistProvider> logger) : IAlbumTracklistProvider
{
    public EnrichmentProvider Source => EnrichmentProvider.MusicBrainzWeb;

    public bool IsEnabled(MusicEnricherOptions options) => options.EnableMusicBrainzWebProvider;

    public async Task<AlbumTracklistCandidate?> FetchAsync(AlbumQuery query, CancellationToken ct = default)
    {
        var releaseId = string.IsNullOrWhiteSpace(query.MusicBrainzReleaseId) ? null : query.MusicBrainzReleaseId;

        if (releaseId is null)
        {
            var results = await musicBrainz.SearchReleasesAsync(query.AlbumArtist, query.Album, 5, ct);
            releaseId = results
                .OrderByDescending(r => r.Score)
                .ThenBy(r => query.TotalTracksHint is int hint ? Math.Abs((r.TrackCount ?? 0) - hint) : 0)
                .FirstOrDefault()?.Id;
        }

        if (releaseId is null)
            return null;

        var release = await musicBrainz.LookupReleaseAsync(releaseId, ct);
        if (release is null || release.Tracks.Count == 0)
            return null;

        logger.LogDebug("MusicBrainz album tracklist: {Count} tracks for release {ReleaseId}", release.Tracks.Count, release.Id);

        return new AlbumTracklistCandidate(
            Source,
            release.Id,
            release.Title,
            release.AlbumArtist,
            release.Year,
            $"https://coverartarchive.org/release/{release.Id}/front",
            release.Tracks
                .Select(t => new CandidateTrack(t.DiscNumber, t.TrackNumber, t.Title, t.LengthMs, t.RecordingId))
                .ToList());
    }
}

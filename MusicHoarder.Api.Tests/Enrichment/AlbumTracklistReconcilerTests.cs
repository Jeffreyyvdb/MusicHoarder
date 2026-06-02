using MusicHoarder.Api.Enrichment.AlbumTracklist;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Enrichment;

public class AlbumTracklistReconcilerTests
{
    [Fact]
    public void Reconcile_TwoProvidersAgree_CorroboratesEveryTrack()
    {
        var candidates = new[]
        {
            Candidate(EnrichmentProvider.MusicBrainzWeb, "Discovery", 2001,
                (1, 1, "One More Time", 320000, "rec-1"),
                (1, 2, "Aerodynamic", 210000, "rec-2")),
            Candidate(EnrichmentProvider.Deezer, "Discovery", 2001,
                (1, 1, "One More Time", 321000, null),
                (1, 2, "Aerodynamic", 211000, null)),
        };

        var result = AlbumTracklistReconciler.Reconcile(candidates);

        Assert.NotNull(result);
        Assert.Equal("Discovery", result!.Title);
        Assert.Equal(2, result.ResolvedTrackCount);
        Assert.False(result.TrackCountContested);
        Assert.Equal(2, result.Tracks.Count);
        Assert.All(result.Tracks, t => Assert.False(t.IsContested));
        Assert.All(result.Tracks, t => Assert.Equal(2, t.CorroboratingProviders.Count));
        // MusicBrainz recording id is carried onto the canonical track.
        Assert.Equal("rec-1", result.Tracks[0].MusicBrainzRecordingId);
        // Both sources are in the winning cluster.
        Assert.All(result.Sources, s => Assert.True(s.InWinningCluster));
    }

    [Fact]
    public void Reconcile_ProvidersDisagreeOnLength_FlagsContestedAndKeepsBonusTracks()
    {
        // MusicBrainz + Deezer say 12 tracks; Spotify lists 14 (a deluxe edition under the same title).
        var candidates = new[]
        {
            Album(EnrichmentProvider.MusicBrainzWeb, "Album", 12),
            Album(EnrichmentProvider.Deezer, "Album", 12),
            Album(EnrichmentProvider.SpotifyAPI, "Album", 14),
        };

        var result = AlbumTracklistReconciler.Reconcile(candidates);

        Assert.NotNull(result);
        // Most-voted count is 12 (two providers vs one), but the union keeps all 14 so nothing is hidden.
        Assert.Equal(12, result!.ResolvedTrackCount);
        Assert.True(result.TrackCountContested);
        Assert.Equal(14, result.Tracks.Count);

        // Tracks 1–12 are backed by all three providers; 13–14 only by Spotify (contested bonus tracks).
        Assert.All(result.Tracks.Take(12), t => Assert.False(t.IsContested));
        Assert.All(result.Tracks.Skip(12), t => Assert.True(t.IsContested));
        Assert.Equal(3, result.Tracks[0].CorroboratingProviders.Count);
        Assert.Single(result.Tracks[13].CorroboratingProviders);
    }

    [Fact]
    public void Reconcile_DifferentAlbums_PicksMostCorroboratedAndRecordsLoser()
    {
        // Two providers each resolved a different album (a search mismatch). They don't cluster; the
        // cluster with MusicBrainz wins on the tie, and the other is kept as a non-winning source.
        var candidates = new[]
        {
            Candidate(EnrichmentProvider.MusicBrainzWeb, "Discovery", 2001, (1, 1, "One More Time", 320000, "rec-1")),
            Candidate(EnrichmentProvider.Deezer, "Random Access Memories", 2013, (1, 1, "Give Life Back to Music", 274000, null)),
        };

        var result = AlbumTracklistReconciler.Reconcile(candidates);

        Assert.NotNull(result);
        Assert.Equal("Discovery", result!.Title);
        Assert.Single(result.Tracks);
        var mbSource = result.Sources.Single(s => s.Provider == EnrichmentProvider.MusicBrainzWeb);
        var dzSource = result.Sources.Single(s => s.Provider == EnrichmentProvider.Deezer);
        Assert.True(mbSource.InWinningCluster);
        Assert.False(dzSource.InWinningCluster);
    }

    [Fact]
    public void Reconcile_NoCandidates_ReturnsNull()
    {
        Assert.Null(AlbumTracklistReconciler.Reconcile([]));
        Assert.Null(AlbumTracklistReconciler.Reconcile(
            [new AlbumTracklistCandidate(EnrichmentProvider.Deezer, "id", "x", "y", 2000, null, [])]));
    }

    private static AlbumTracklistCandidate Candidate(
        EnrichmentProvider source, string title, int year, params (int Disc, int Track, string Title, int Dur, string? Rec)[] tracks)
        => new(source, $"id-{source}", title, "Artist", year, null,
            tracks.Select(t => new CandidateTrack(t.Disc, t.Track, t.Title, t.Dur, t.Rec)).ToList());

    private static AlbumTracklistCandidate Album(EnrichmentProvider source, string title, int trackCount)
        => new(source, $"id-{source}", title, "Artist", 2010, null,
            Enumerable.Range(1, trackCount)
                .Select(n => new CandidateTrack(1, n, $"Track {n}", 200000, null))
                .ToList());
}

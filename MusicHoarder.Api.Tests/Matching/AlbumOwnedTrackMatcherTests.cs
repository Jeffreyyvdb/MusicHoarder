using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Matching;

public class AlbumOwnedTrackMatcherTests
{
    private static CanonicalAlbumTrack Track(int id, int trackNumber, string title, string? recMbid = null) => new()
    {
        Id = id,
        DiscNumber = 1,
        TrackNumber = trackNumber,
        Title = title,
        MusicBrainzRecordingId = recMbid,
    };

    // When the owned track numbers are corrupt (each song enriched against a different release), the
    // default position phase links the wrong files; turning it off forces a title-based match.
    [Fact]
    public void Match_WithoutPositionPhase_IgnoresCorruptTrackNumbers()
    {
        var tracks = new[]
        {
            Track(id: 100, trackNumber: 9, title: "Leray"),
            Track(id: 200, trackNumber: 17, title: "U Deserve It"),
        };

        // The "U Deserve It" file is wrongly numbered 9; the "Leray" file is wrongly numbered 1.
        var owned = new[]
        {
            new OwnedTrackInfo(Id: 31, MusicBrainzId: null, DiscNumber: 1, TrackNumber: 9, Title: "U Deserve It"),
            new OwnedTrackInfo(Id: 9, MusicBrainzId: null, DiscNumber: 1, TrackNumber: 1, Title: "Leray"),
        };

        var positionMatched = AlbumOwnedTrackMatcher.Match(tracks, owned, titleThreshold: 85);
        // Position phase mislinks canonical "Leray" (track 9) to the "U Deserve It" file (also track 9).
        Assert.Equal(31, positionMatched[100]);

        var titleMatched = AlbumOwnedTrackMatcher.Match(tracks, owned, titleThreshold: 85, usePositionPhase: false);
        Assert.Equal(9, titleMatched[100]);   // Leray -> the Leray file
        Assert.Equal(31, titleMatched[200]);  // U Deserve It -> the U Deserve It file
    }

    [Fact]
    public void Match_PrefersRecordingMbid_OverTitle()
    {
        var tracks = new[] { Track(id: 1, trackNumber: 1, title: "Real Title", recMbid: "rec-x") };
        var owned = new[]
        {
            new OwnedTrackInfo(Id: 50, MusicBrainzId: "rec-x", DiscNumber: 1, TrackNumber: 99, Title: "Totally Different"),
            new OwnedTrackInfo(Id: 51, MusicBrainzId: null, DiscNumber: 1, TrackNumber: 1, Title: "Real Title"),
        };

        var matched = AlbumOwnedTrackMatcher.Match(tracks, owned, titleThreshold: 85, usePositionPhase: false);
        Assert.Equal(50, matched[1]);
    }

    [Fact]
    public void Match_ConsumesEachSongAtMostOnce()
    {
        var tracks = new[]
        {
            Track(id: 1, trackNumber: 1, title: "Even Steven"),
            Track(id: 2, trackNumber: 2, title: "Even Steven"),
        };
        var owned = new[]
        {
            new OwnedTrackInfo(Id: 70, MusicBrainzId: null, DiscNumber: 1, TrackNumber: 1, Title: "Even Steven"),
        };

        var matched = AlbumOwnedTrackMatcher.Match(tracks, owned, titleThreshold: 85, usePositionPhase: false);
        Assert.Single(matched);
        Assert.Equal(70, matched[1]);
        Assert.False(matched.ContainsKey(2));
    }

    [Fact]
    public void Match_DefaultPositionPhase_StillMatchesByPosition()
    {
        var tracks = new[] { Track(id: 1, trackNumber: 5, title: "No Title Here") };
        var owned = new[]
        {
            new OwnedTrackInfo(Id: 80, MusicBrainzId: null, DiscNumber: 1, TrackNumber: 5, Title: "Something Else"),
        };

        var matched = AlbumOwnedTrackMatcher.Match(tracks, owned, titleThreshold: 85);
        Assert.Equal(80, matched[1]);
    }
}

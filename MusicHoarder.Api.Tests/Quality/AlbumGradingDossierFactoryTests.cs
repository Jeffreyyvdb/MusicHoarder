using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Quality;

namespace MusicHoarder.Api.Tests.Quality;

public class AlbumGradingDossierFactoryTests
{
    [Fact]
    public void Build_MatchesOwnedSongs_AndCountsUnmatchedAndRollup()
    {
        var album = new CanonicalAlbum
        {
            Id = 1,
            ArtistKey = "daft punk",
            AlbumKey = "discovery",
            DisplayArtist = "Daft Punk",
            DisplayTitle = "Discovery",
            Year = 2001,
            ResolvedTrackCount = 4,
            TrackCountContested = false,
            Status = CanonicalAlbumStatus.Fetched,
            SourcesJson = """[{"Provider":2,"AlbumId":"rel-1","TrackCount":4,"InWinningCluster":true}]""",
            Tracks =
            [
                new CanonicalAlbumTrack { Id = 1, DiscNumber = 1, TrackNumber = 1, Title = "One More Time", MusicBrainzRecordingId = "rec-1", CorroborationCount = 2 },
                new CanonicalAlbumTrack { Id = 2, DiscNumber = 1, TrackNumber = 2, Title = "Aerodynamic", CorroborationCount = 2 },
                new CanonicalAlbumTrack { Id = 3, DiscNumber = 1, TrackNumber = 3, Title = "Nightvision", CorroborationCount = 2 },
                new CanonicalAlbumTrack { Id = 4, DiscNumber = 1, TrackNumber = 4, Title = "Hidden Bonus", CorroborationCount = 1, IsContested = true },
            ],
        };

        var owned = new List<OwnedSongForGrading>
        {
            new(101, "One More Time", "Daft Punk", 1, 1, 320, "rec-1"), // t1 by recording id
            new(102, "Aerodynamic", "Daft Punk", 1, 2, 210, null),      // t2 by position
            new(103, "Night Vision", "Daft Punk", null, null, 280, null), // t3 by fuzzy title
            new(104, "Stray Track", "Daft Punk", null, 99, 200, null),  // matches nothing
        };
        var verdicts = new Dictionary<int, SongQualityVerdict> { [101] = SongQualityVerdict.Wrong };

        var dossier = new AlbumGradingDossierFactory().Build(album, owned, verdicts, titleThreshold: 85);

        Assert.Equal(4, dossier.MatchSummary.OwnedCount);
        Assert.Equal(4, dossier.MatchSummary.CanonicalCount);
        Assert.Equal(3, dossier.MatchSummary.OwnedMatchedCount);
        Assert.Equal(1, dossier.MatchSummary.OwnedUnmatchedCount);
        Assert.Equal(0.75, dossier.MatchSummary.TitleMatchRate, 3);

        Assert.True(dossier.Canonical.Tracks[0].Owned);
        Assert.True(dossier.Canonical.Tracks[1].Owned);
        Assert.True(dossier.Canonical.Tracks[2].Owned);
        Assert.False(dossier.Canonical.Tracks[3].Owned); // the bonus track nobody owns

        // The one owned song that matched no canonical track is flagged.
        Assert.Contains(dossier.LocalAlbum.OwnedSongs, s => s.Title == "Stray Track" && !s.MatchedToCanonical);

        Assert.Equal(1, dossier.SongGradeRollup.Graded);
        Assert.Equal(1, dossier.SongGradeRollup.Wrong);
        Assert.Single(dossier.Sources);
    }
}

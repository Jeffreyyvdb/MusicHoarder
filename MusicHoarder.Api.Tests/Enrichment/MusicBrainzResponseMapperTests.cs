using MusicHoarder.Api.Enrichment;

namespace MusicHoarder.Api.Tests.Enrichment;

/// <summary>
/// Exercises the extracted mapper directly — DTO in, domain record out — without an
/// HttpClient stub or JSON fixtures. Covers rules that were previously too expensive
/// to reach through the wire format.
/// </summary>
public class MusicBrainzResponseMapperTests
{
    [Fact]
    public void MapRecording_SortCredit_FallsBackToDisplayName_WhenSortNameAbsent()
    {
        var dto = new MusicBrainzRecordingDto
        {
            Id = "rec-1",
            Title = "Song",
            ArtistCredit =
            [
                new MusicBrainzArtistCreditDto
                {
                    Name = "MF DOOM",
                    JoinPhrase = " & ",
                    Artist = new MusicBrainzArtistDto { Id = "a1", Name = "MF DOOM" },
                },
                new MusicBrainzArtistCreditDto
                {
                    Name = "Madlib",
                    Artist = new MusicBrainzArtistDto { Id = "a2", Name = "Madlib", SortName = "Madlib" },
                },
            ],
        };

        var rec = MusicBrainzResponseMapper.MapRecording(dto);

        // First credit has no sort-name, so its credited-as name fills the slot.
        Assert.Equal("MF DOOM & Madlib", rec.ArtistSort);
        Assert.Null(rec.AlbumArtistSort);
    }

    [Fact]
    public void MapRecording_WithoutReleases_LeavesReleaseFieldsNull()
    {
        var dto = new MusicBrainzRecordingDto { Id = "rec-1", Title = "Song" };

        var rec = MusicBrainzResponseMapper.MapRecording(dto);

        Assert.Equal(string.Empty, rec.Artist);
        Assert.Null(rec.ReleaseId);
        Assert.Null(rec.ReleaseTypes);
        Assert.Null(rec.TotalDiscs);
        Assert.Null(rec.Genre);
        Assert.Equal(100, rec.Score);
    }

    [Fact]
    public void MapRelease_TrackWithoutPosition_GetsOrdinalZero_AndTracklessMediumIsSkipped()
    {
        var dto = new MusicBrainzReleaseDetailDto
        {
            Id = "rel-1",
            Title = "Album",
            Media =
            [
                new MusicBrainzMediaDto
                {
                    Position = 1,
                    Tracks = [new MusicBrainzTrackDto { Number = "A1", Title = "Vinyl Side Opener" }],
                },
                new MusicBrainzMediaDto { Position = 2 },
            ],
        };

        var release = MusicBrainzResponseMapper.MapRelease(dto);

        var track = Assert.Single(release.Tracks);
        // The printed "A1" designation is not a positional ordinal; without `position` the
        // ordinal defaults to 0 rather than being parsed from `number`.
        Assert.Equal(0, track.TrackNumber);
        Assert.Equal(1, track.DiscNumber);
        // Both media still count toward the disc total even when one has no track list.
        Assert.Equal(2, release.TotalDiscs);
        Assert.Equal(1, release.TotalTracks);
    }

    [Fact]
    public void MapReleaseSearchResults_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Empty(MusicBrainzResponseMapper.MapReleaseSearchResults(null));
        Assert.Empty(MusicBrainzResponseMapper.MapReleaseSearchResults([]));
    }
}

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

    [Theory]
    [InlineData(" met ", "Hef", "Jayh")]
    [InlineData(" + ", "2Pac", "Outlawz")]
    [InlineData(" and ", "50 Cent", "Olivia")]
    public void MapRecording_JoinPhraseTheSplitterDoesNotKnow_AlbumArtistIsTheLeadCredit(
        string joinPhrase, string lead, string guest)
    {
        var dto = Recording(Credit(lead, "mbid-lead", joinPhrase), Credit(guest, "mbid-guest"));

        var rec = MusicBrainzResponseMapper.MapRecording(dto);

        // The album-artist name comes from the same entry as its id. Re-parsing the joined credit
        // kept "Hef met Jayh" whole while pairing it with Hef's MBID, so the artist-dedup Inbox
        // offered the collab as a spelling of Hef.
        Assert.Equal(lead, rec.AlbumArtist);
        Assert.Equal("mbid-lead", rec.AlbumArtistMusicBrainzId);
        // The display credit and the discrete list are untouched.
        Assert.Equal($"{lead}{joinPhrase}{guest}", rec.Artist);
        Assert.Equal($"{lead}; {guest}", rec.Artists);
        Assert.Equal("mbid-lead; mbid-guest", rec.ArtistMusicBrainzIds);
    }

    [Theory]
    [InlineData("Simon & Garfunkel")]
    [InlineData("Tyler, The Creator")]
    [InlineData("Earth, Wind & Fire")]
    public void MapRecording_SingleArtistWhoseNameHoldsADelimiter_IsNotTruncated(string name)
    {
        var dto = Recording(Credit(name, "mbid-1"));

        var rec = MusicBrainzResponseMapper.MapRecording(dto);

        Assert.Equal(name, rec.AlbumArtist);
        Assert.Equal(name, rec.Artist);
    }

    [Fact]
    public void MapRecording_FeaturingCredit_AlbumArtistIsStillTheLead()
    {
        var dto = Recording(Credit("Kanye West", "mbid-kanye", " feat. "), Credit("Kid Cudi", "mbid-cudi"));

        var rec = MusicBrainzResponseMapper.MapRecording(dto);

        Assert.Equal("Kanye West", rec.AlbumArtist);
        Assert.Equal("Kanye West feat. Kid Cudi", rec.Artist);
    }

    [Fact]
    public void MapRecording_CreditedAsName_WinsOverTheCanonicalName()
    {
        // The credited-as spelling is what the old parse of the display credit returned; the discrete
        // Artists list keeps the canonical name, as before.
        var dto = Recording(
            Credit("Jay Z", "mbid-jay", " & ", canonical: "JAY-Z"),
            Credit("Kanye West", "mbid-kanye"));

        var rec = MusicBrainzResponseMapper.MapRecording(dto);

        Assert.Equal("Jay Z", rec.AlbumArtist);
        Assert.Equal("JAY-Z; Kanye West", rec.Artists);
    }

    [Fact]
    public void MapRecording_BlankCreditedName_FallsBackToTheCanonicalName_AndCollapsesWhitespace()
    {
        var blank = Recording(Credit(" ", "mbid-1", canonical: "  Daft   Punk "));
        var padded = Recording(Credit("  Daft   Punk ", "mbid-1"));

        Assert.Equal("Daft Punk", MusicBrainzResponseMapper.MapRecording(blank).AlbumArtist);
        Assert.Equal("Daft Punk", MusicBrainzResponseMapper.MapRecording(padded).AlbumArtist);
    }

    [Fact]
    public void MapRecording_NoCredit_LeavesAlbumArtistNull()
    {
        Assert.Null(MusicBrainzResponseMapper.MapRecording(new MusicBrainzRecordingDto { Id = "rec-1" }).AlbumArtist);
        Assert.Null(MusicBrainzResponseMapper.MapRecording(Recording()).AlbumArtist);
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
    public void MapRelease_AlbumArtistIsTheLeadCredit_NotAParseOfTheJoinedCredit()
    {
        var collab = new MusicBrainzReleaseDetailDto
        {
            Id = "rel-1",
            ArtistCredit = [Credit("Hef", "mbid-hef", " met "), Credit("Jayh", "mbid-jayh")],
        };
        var single = new MusicBrainzReleaseDetailDto { Id = "rel-2", ArtistCredit = [Credit("Simon & Garfunkel", "mbid-sg")] };

        Assert.Equal("Hef", MusicBrainzResponseMapper.MapRelease(collab).AlbumArtist);
        Assert.Equal("Simon & Garfunkel", MusicBrainzResponseMapper.MapRelease(single).AlbumArtist);
    }

    [Fact]
    public void MapRelease_NoCredit_LeavesAlbumArtistNull()
    {
        Assert.Null(MusicBrainzResponseMapper.MapRelease(new MusicBrainzReleaseDetailDto { Id = "rel-1" }).AlbumArtist);
        Assert.Null(MusicBrainzResponseMapper.MapRelease(new MusicBrainzReleaseDetailDto { Id = "rel-1", ArtistCredit = [] }).AlbumArtist);
    }

    [Fact]
    public void MapReleaseSearchResults_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Empty(MusicBrainzResponseMapper.MapReleaseSearchResults(null));
        Assert.Empty(MusicBrainzResponseMapper.MapReleaseSearchResults([]));
    }

    private static MusicBrainzRecordingDto Recording(params MusicBrainzArtistCreditDto[] credits) =>
        new() { Id = "rec-1", Title = "Song", ArtistCredit = [.. credits] };

    private static MusicBrainzArtistCreditDto Credit(
        string name, string id, string joinPhrase = "", string? canonical = null) => new()
    {
        Name = name,
        JoinPhrase = joinPhrase,
        Artist = new MusicBrainzArtistDto { Id = id, Name = canonical ?? name },
    };
}

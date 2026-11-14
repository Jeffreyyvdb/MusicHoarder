using MusicHoarder.Api.Artists;
using MusicHoarder.Api.Persistence;
using static MusicHoarder.Api.Tests.Artists.ArtistTestKit;

namespace MusicHoarder.Api.Tests.Artists;

public class ArtistDiscographyTests
{
    [Theory]
    [InlineData("album", ArtistReleaseKinds.Album)]
    [InlineData("ep", ArtistReleaseKinds.Ep)]
    [InlineData("single", ArtistReleaseKinds.Single)]
    [InlineData("compile", ArtistReleaseKinds.Compilation)]
    [InlineData(null, ArtistReleaseKinds.Album)]
    [InlineData("something-new", ArtistReleaseKinds.Album)]
    public void KindOf_MapsDeezerRecordTypes(string? recordType, ArtistReleaseKinds expected) =>
        Assert.Equal(expected, ArtistDiscography.KindOf(recordType));

    [Fact]
    public void Group_FoldsEditionsIntoOneRelease()
    {
        var grouped = ArtistDiscography.Group(
        [
            Release("clean", "Discovery", date: "2001-03-12"),
            Release("deluxe", "Discovery (Deluxe Edition)", date: "2021-02-22", isExplicit: true),
            Release("other", "Homework", date: "1997-01-20"),
        ]);

        Assert.Equal(2, grouped.Count);
        var discovery = grouped.Single(r => r.Key == "album:discovery");
        // The explicit deluxe is fetched first, but the card shows the plain title and the first date.
        Assert.Equal("deluxe", discovery.PrimaryId);
        Assert.Equal(["deluxe", "clean"], discovery.EditionIds);
        Assert.Equal("Discovery", discovery.Title);
        Assert.Equal(new DateOnly(2001, 3, 12), discovery.ReleaseDate);
        Assert.True(discovery.Explicit);
    }

    [Fact]
    public void Group_KeepsASingleApartFromTheAlbumItIsNamedAfter()
    {
        var grouped = ArtistDiscography.Group(
        [
            Release("a", "Blockkids", "album", "2023-05-01"),
            Release("s", "Blockkids", "single", "2022-11-01"),
        ]);

        Assert.Equal(["album:blockkids", "single:blockkids"], grouped.Select(r => r.Key));
    }

    [Fact]
    public void Group_ListsNewestFirst_UndatedLast()
    {
        var grouped = ArtistDiscography.Group(
        [
            Release("old", "Old", date: "1999-01-01"),
            Release("undated", "Undated", date: null),
            Release("new", "New", date: "2024-01-01"),
        ]);

        Assert.Equal(["New", "Old", "Undated"], grouped.Select(r => r.Title));
    }

    [Fact]
    public void ProvesIdentity_AnOwnedAlbumOnTheListingIsProof()
    {
        var owned = new[] { Facts("Digital Love", "Discovery") };
        Assert.True(ArtistDiscography.ProvesIdentity([Release("1", "Discovery (Deluxe)")], owned));
        Assert.False(ArtistDiscography.ProvesIdentity([Release("1", "Something Else")], owned));
    }

    [Fact]
    public void ProvesIdentity_ASingleIsProvenByItsSong()
    {
        var owned = new[] { Facts("Balotelli", "Some Mixtape") };
        Assert.True(ArtistDiscography.ProvesIdentity([Release("1", "Balotelli", "single")], owned));
        // An album named like a song is not a single: its title alone proves nothing.
        Assert.False(ArtistDiscography.ProvesIdentity([Release("1", "Balotelli", "album")], owned));
    }

    [Fact]
    public void ProvesIdentity_AlbumFillAndPlaceholderAlbumsAreNoEvidence()
    {
        // An album-fill download exists because of a catalog, so it cannot vouch for one.
        var filled = new[] { Facts("Track", "Discovery", SongAcquisitionIntent.AlbumFill) };
        Assert.False(ArtistDiscography.ProvesIdentity([Release("1", "Discovery")], filled));

        var placeholder = new[] { Facts("Track", "Unknown Album") };
        Assert.False(ArtistDiscography.ProvesIdentity([Release("1", "Unknown Album")], placeholder));
    }

    [Fact]
    public void IsByArtist_MatchesTheLabelOrADiscreteCredit()
    {
        var key = "thizzy52";
        Assert.True(ArtistDiscography.IsByArtist(Facts("A", "B", albumArtist: "THIZZY52"), key));
        Assert.True(ArtistDiscography.IsByArtist(Facts("A", "B", albumArtist: "Other", artists: "Other;THIZZY52"), key));
        // A substring is not a credit.
        Assert.False(ArtistDiscography.IsByArtist(Facts("A", "B", albumArtist: "THIZZY52 Tribute Band"), key));
    }

    private static ArtistSongFacts Facts(
        string title, string album, SongAcquisitionIntent intent = SongAcquisitionIntent.Explicit,
        string albumArtist = "Daft Punk", string? artists = null) =>
        new(1, title, album, albumArtist, albumArtist, artists, null, null, null, intent);
}

using MusicHoarder.Api.Matching;

namespace MusicHoarder.Api.Tests.Matching;

public class ProviderIdentityTests
{
    private static readonly IdentityMatchOptions Opts = IdentityMatchOptions.Default;

    private static ProviderIdentity Id(
        string? artist = null,
        string? title = null,
        int? duration = null,
        string? isrc = null,
        string? mbid = null,
        string? spotifyId = null,
        VersionQualifiers q = VersionQualifiers.None)
        => new(artist, title, null, duration, isrc, mbid, spotifyId, q);

    [Fact]
    public void Agrees_WhenSharedIsrc_EvenWithDifferentNames()
    {
        var a = Id(artist: "The Beatles", title: "Hey Jude", isrc: "GBAYE6800001");
        var b = Id(artist: "Beatles", title: "Hey Jude (typo)", isrc: "GB-AYE-68-00001");
        Assert.True(a.AgreesWith(b, Opts));
    }

    [Fact]
    public void Agrees_WhenSharedMbid()
    {
        var a = Id(title: "X", mbid: "abc-123");
        var b = Id(title: "totally different", mbid: "abc-123");
        Assert.True(a.AgreesWith(b, Opts));
    }

    [Fact]
    public void Agrees_WhenNamesAndDurationMatch_NoIdentifiers()
    {
        var a = Id(artist: "Daft Punk", title: "One More Time", duration: 320);
        var b = Id(artist: "Daft Punk", title: "One More Time", duration: 322);
        Assert.True(a.AgreesWith(b, Opts));
    }

    [Fact]
    public void Disagrees_WhenDurationsFarApart()
    {
        var a = Id(artist: "Daft Punk", title: "One More Time", duration: 320);
        var b = Id(artist: "Daft Punk", title: "One More Time", duration: 600);
        Assert.False(a.AgreesWith(b, Opts));
    }

    [Fact]
    public void Disagrees_WhenTitlesDiffer()
    {
        var a = Id(artist: "Artist", title: "Song A");
        var b = Id(artist: "Artist", title: "Completely Other Track");
        Assert.False(a.AgreesWith(b, Opts));
    }

    [Fact]
    public void Disagrees_WhenVersionQualifiersConflict_EvenIfNamesMatch()
    {
        var studio = Id(artist: "Band", title: "Anthem", duration: 200, q: VersionQualifiers.None);
        var live = Id(artist: "Band", title: "Anthem", duration: 200, q: VersionQualifiers.Live);
        Assert.False(studio.AgreesWith(live, Opts));
    }

    [Fact]
    public void Disagrees_WhenOneTitleEmpty()
    {
        var a = Id(artist: "Band", title: "");
        var b = Id(artist: "Band", title: "Anthem");
        Assert.False(a.AgreesWith(b, Opts));
    }
}

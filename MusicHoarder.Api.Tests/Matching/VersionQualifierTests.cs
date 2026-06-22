using MusicHoarder.Api.Matching;

namespace MusicHoarder.Api.Tests.Matching;

public class VersionQualifierTests
{
    [Theory]
    [InlineData("Song (Live)", VersionQualifiers.Live)]
    [InlineData("Song - Live at Wembley", VersionQualifiers.Live)]
    [InlineData("Song (Remix)", VersionQualifiers.Remix)]
    [InlineData("Song (Acoustic Version)", VersionQualifiers.Acoustic)]
    [InlineData("Song (Instrumental)", VersionQualifiers.Instrumental)]
    [InlineData("Song (Demo)", VersionQualifiers.Demo)]
    [InlineData("Song - Karaoke", VersionQualifiers.Karaoke)]
    [InlineData("Plain Studio Song", VersionQualifiers.None)]
    public void Detect_Title_FindsQualifier(string title, VersionQualifiers expected)
    {
        Assert.Equal(expected, VersionQualifier.Detect(title) & expected);
        if (expected == VersionQualifiers.None)
            Assert.Equal(VersionQualifiers.None, VersionQualifier.Detect(title) & VersionQualifier.StrongMask);
    }

    [Fact]
    public void Detect_RemasterFromTitle()
    {
        Assert.True(VersionQualifier.Detect("Song (Remastered 2011)").HasFlag(VersionQualifiers.Remaster));
    }

    [Fact]
    public void Detect_DeluxeFromAlbumNotTitle()
    {
        var q = VersionQualifier.Detect("Plain Song", "The Album (Deluxe Edition)");
        Assert.True(q.HasFlag(VersionQualifiers.Deluxe));
        Assert.Equal(VersionQualifiers.None, q & VersionQualifier.StrongMask);
    }

    [Fact]
    public void Detect_LiveFromAlbum_WhenTitleIsPlain()
    {
        // A live bootleg carries the venue on the album, not the track title — it must still register
        // as Live so it can't auto-match the studio recording the catalogs return.
        var q = VersionQualifier.Detect("Promotion (feat. Future) [Phoenix]", "Vultures 2: Live from Phoenix 2024.03.10");

        Assert.True(q.HasFlag(VersionQualifiers.Live));
        Assert.False(VersionQualifier.Compare(q, VersionQualifiers.None), "a live file must not agree with a studio candidate");
    }

    [Theory]
    // Studio vs studio → compatible
    [InlineData(VersionQualifiers.None, VersionQualifiers.None, true)]
    // Remaster is just a re-release of the same recording → compatible with studio
    [InlineData(VersionQualifiers.None, VersionQualifiers.Remaster, true)]
    [InlineData(VersionQualifiers.Remaster, VersionQualifiers.None, true)]
    // Live / Remix / Acoustic are different recordings → incompatible with studio
    [InlineData(VersionQualifiers.None, VersionQualifiers.Live, false)]
    [InlineData(VersionQualifiers.None, VersionQualifiers.Remix, false)]
    [InlineData(VersionQualifiers.Live, VersionQualifiers.None, false)]
    // Same strong qualifier on both sides → compatible
    [InlineData(VersionQualifiers.Live, VersionQualifiers.Live, true)]
    // Different strong qualifiers → incompatible
    [InlineData(VersionQualifiers.Live, VersionQualifiers.Remix, false)]
    public void Compare_RespectsStrongQualifiers(VersionQualifiers expected, VersionQualifiers candidate, bool compatible)
    {
        Assert.Equal(compatible, VersionQualifier.Compare(expected, candidate));
    }
}

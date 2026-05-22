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

    [Theory]
    // The bare everyday word is NOT a version marker — these are studio titles.
    [InlineData("Live and Let Die")]
    [InlineData("Live Forever")]
    [InlineData("Long Live")]
    [InlineData("Live Your Life")]
    [InlineData("Cover Me")]
    [InlineData("Book Cover")]
    [InlineData("Live in Color")]
    public void Detect_AmbiguousWordWithoutDecoration_NotAStrongQualifier(string title)
    {
        Assert.Equal(VersionQualifiers.None, VersionQualifier.Detect(title) & VersionQualifier.StrongMask);
    }

    [Theory]
    // The same words ARE markers when decorated or in an explicit phrase.
    [InlineData("Live Forever (Live)", VersionQualifiers.Live)]
    [InlineData("Wonderwall - Live at Wembley", VersionQualifiers.Live)]
    [InlineData("Hotel California [Live]", VersionQualifiers.Live)]
    [InlineData("Smells Like Teen Spirit (Live Version)", VersionQualifiers.Live)]
    [InlineData("Hurt (Johnny Cash Cover)", VersionQualifiers.Cover)]
    [InlineData("Yesterday - Demo", VersionQualifiers.Demo)]
    [InlineData("Yesterday (Demo)", VersionQualifiers.Demo)]
    public void Detect_DecoratedAmbiguousWord_IsQualifier(string title, VersionQualifiers expected)
    {
        Assert.True(VersionQualifier.Detect(title).HasFlag(expected));
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

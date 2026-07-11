using MusicHoarder.Api.Soulseek;

namespace MusicHoarder.Api.Tests.Soulseek;

public class SoulseekSearchQueryTests
{
    [Theory]
    // Real cases that returned 0 results with the raw {artist} {title} query.
    [InlineData("Beyoncé, JAŸ-Z, Kanye West", "Drunk in Love Remix (feat. JAY-Z & Kanye West)", "beyonce drunk in love remix")]
    [InlineData("BigXthaPlug, Ro$ama, MurdaGang PB, Yung Hood & 600 Ent.", "6WA", "bigxthaplug 6wa")]
    [InlineData("Saint Levant, Haifa Wehbe", "MITSUBISHI [Platinum Edition]", "saint levant mitsubishi")]
    [InlineData("J Balvin & Ryan Castro", "Una A La Vez", "j balvin una a la vez")]
    // Already-clean cases stay effectively the same (lowercased).
    [InlineData("Daft Punk", "Get Lucky", "daft punk get lucky")]
    public void Build_ReducesToPrimaryArtistAndCleanTitle(string artist, string title, string expected)
    {
        Assert.Equal(expected, SoulseekSearchQuery.Build(artist, title));
    }

    [Fact]
    public void Build_DropsFeaturedCreditsAndBrackets()
    {
        // Featured artists and edition tags never appear in peer filenames; strip them.
        Assert.Equal("artist song", SoulseekSearchQuery.Build("Artist ft. Someone", "Song (feat. Someone) [Deluxe]"));
    }

    [Fact]
    public void Build_HandlesBlankArtist()
    {
        Assert.Equal("just a title", SoulseekSearchQuery.Build(null, "Just a Title"));
        Assert.Equal("just a title", SoulseekSearchQuery.Build("", "Just a Title"));
    }

    [Fact]
    public void Build_TakesFirstArtistAcrossSeparators()
    {
        Assert.Equal("primary track", SoulseekSearchQuery.Build("Primary; Second; Third", "Track"));
        Assert.Equal("primary track", SoulseekSearchQuery.Build("Primary x Second", "Track"));
        Assert.Equal("primary track", SoulseekSearchQuery.Build("Primary & Second", "Track"));
    }
}

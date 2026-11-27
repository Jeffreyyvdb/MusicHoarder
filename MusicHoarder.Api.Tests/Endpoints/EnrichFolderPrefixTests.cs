using MusicHoarder.Api.Endpoints;

namespace MusicHoarder.Api.Tests.Endpoints;

public class EnrichFolderPrefixTests
{
    [Theory]
    [InlineData("/music", "Artist/Album", "/music/Artist/Album")]
    [InlineData("/music/", "Artist/Album/", "/music/Artist/Album")]
    [InlineData("/music", "/music/Artist/Album", "/music/Artist/Album")]
    [InlineData("/music", "", "/music")]
    [InlineData("/music", "Artist\\Album", "/music/Artist/Album")]
    public void ResolveFolderPrefix_BuildsNormalizedAbsolutePrefix(string source, string path, string expected)
    {
        Assert.Equal(expected, EnrichmentEndpoints.ResolveFolderPrefix(source, path));
    }

    [Fact]
    public void ResolveFolderPrefix_WithEmptySource_UsesRelativePath()
    {
        Assert.Equal("Artist/Album", EnrichmentEndpoints.ResolveFolderPrefix("", "Artist/Album"));
    }

    [Theory]
    [InlineData("/music/Kanye West", "Kanye West")]
    [InlineData("Kanye West/", "Kanye West")]
    [InlineData("Artist\\Album\\", "Album")]
    [InlineData("Solo", "Solo")]
    public void FolderDisplayName_ReturnsLastSegment(string path, string expected)
    {
        Assert.Equal(expected, EnrichmentEndpoints.FolderDisplayName(path));
    }
}

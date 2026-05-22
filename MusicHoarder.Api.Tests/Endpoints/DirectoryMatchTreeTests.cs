using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Endpoints;

public class DirectoryMatchTreeTests
{
    private static DashboardEndpoints.MatchTreeRow Row(
        string path,
        EnrichmentStatus enrichment,
        LibraryBuildStatus build = LibraryBuildStatus.Pending)
        => new(path, enrichment, build);

    [Fact]
    public void BuildMatchTree_RollsUpCountsThroughEveryAncestor()
    {
        var rows = new[]
        {
            Row("/music/Kanye West/Donda/01.mp3", EnrichmentStatus.Matched, LibraryBuildStatus.Done),
            Row("/music/Kanye West/Donda/02.mp3", EnrichmentStatus.NeedsReview),
            Row("/music/Kanye West/Yandhi/leak.mp3", EnrichmentStatus.NeedsReview),
            Row("/music/Drake/album/hit.mp3", EnrichmentStatus.Matched, LibraryBuildStatus.Done),
        };

        var root = DashboardEndpoints.BuildMatchTree(rows, "/music");

        // Root rolls up everything.
        Assert.Equal(4, root.Total);
        Assert.Equal(2, root.Matched);
        Assert.Equal(2, root.NeedsReview);
        Assert.Equal(2, root.Done);

        var kanye = root.Children.Single(c => c.Name == "Kanye West");
        Assert.Equal(3, kanye.Total);
        Assert.Equal(1, kanye.Matched);
        Assert.Equal(2, kanye.NeedsReview);

        var donda = kanye.Children.Single(c => c.Name == "Donda");
        Assert.Equal(2, donda.Total);
        Assert.Equal(1, donda.Matched);
        Assert.Equal(1, donda.NeedsReview);
        Assert.Equal("Kanye West/Donda", donda.Path);
    }

    [Fact]
    public void BuildMatchTree_CountsPendingAndFailedDistinctly()
    {
        var rows = new[]
        {
            Row("/music/A/1.mp3", EnrichmentStatus.Pending),
            Row("/music/A/2.mp3", EnrichmentStatus.Failed),
        };

        var node = DashboardEndpoints.BuildMatchTree(rows, "/music").Children.Single(c => c.Name == "A");

        Assert.Equal(2, node.Total);
        Assert.Equal(0, node.Matched);
        Assert.Equal(1, node.Pending);
        Assert.Equal(1, node.Failed);
    }

    [Fact]
    public void BuildMatchTree_HandlesTrailingSlashAndBackslashesInSourceRoot()
    {
        var rows = new[]
        {
            Row("/music/A/song.mp3", EnrichmentStatus.Matched),
        };

        // Trailing slash on the configured root must not leak an empty leading segment.
        var root = DashboardEndpoints.BuildMatchTree(rows, "/music/");

        var a = Assert.Single(root.Children);
        Assert.Equal("A", a.Name);
        Assert.Equal("A", a.Path);
    }

    [Fact]
    public void BuildMatchTree_FilesDirectlyInRootDoNotCreateChildNodes()
    {
        var rows = new[]
        {
            Row("/music/loose.mp3", EnrichmentStatus.NeedsReview),
        };

        var root = DashboardEndpoints.BuildMatchTree(rows, "/music");

        Assert.Equal(1, root.Total);
        Assert.Equal(1, root.NeedsReview);
        Assert.Empty(root.Children);
    }
}

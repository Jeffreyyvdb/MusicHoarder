using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Endpoints;

public class DirectoryMatchTreeTests
{
    private static DashboardEndpoints.MatchTreeRow Row(
        string path,
        EnrichmentStatus enrichment,
        LibraryBuildStatus build = LibraryBuildStatus.Pending,
        long sizeBytes = 0)
        => new(path, enrichment, build, sizeBytes);

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

    [Fact]
    public void BuildMatchTree_CountsDirectFilesOnlyAtTheFoldersOwningThem()
    {
        var rows = new[]
        {
            Row("/music/a/b/x.flac", EnrichmentStatus.Matched, sizeBytes: 100),
            Row("/music/a/b/c/y.flac", EnrichmentStatus.Matched, sizeBytes: 200),
            Row("/music/a/loose.mp3", EnrichmentStatus.Pending, sizeBytes: 50),
        };

        var root = DashboardEndpoints.BuildMatchTree(rows, "/music");
        var a = root.Children.Single(c => c.Name == "a");
        var b = a.Children.Single(c => c.Name == "b");
        var c = b.Children.Single(ch => ch.Name == "c");

        // Direct files attach to the owning folder only, never to ancestors.
        Assert.Equal(1, a.DirectFiles);   // loose.mp3
        Assert.Equal(1, b.DirectFiles);   // x.flac
        Assert.Equal(1, c.DirectFiles);   // y.flac
        Assert.Equal(0, root.DirectFiles);

        // Size rolls up cumulatively through every ancestor.
        Assert.Equal(350, a.SizeBytes);
        Assert.Equal(300, b.SizeBytes);
        Assert.Equal(200, c.SizeBytes);
        Assert.Equal(350, root.SizeBytes);
    }

    [Fact]
    public void BuildMatchTree_FlagsExpectedLowNodesByPath()
    {
        var rows = new[]
        {
            Row("/music/Kanye West/Donda/01.mp3", EnrichmentStatus.Matched, LibraryBuildStatus.Done),
            Row("/music/Kanye West/Yandhi/leak.mp3", EnrichmentStatus.Failed),
            Row("/music/Drake/album/hit.mp3", EnrichmentStatus.Matched, LibraryBuildStatus.Done),
        };

        var expectedLow = new HashSet<string>(StringComparer.Ordinal) { "Kanye West/Yandhi" };
        var root = DashboardEndpoints.BuildMatchTree(rows, "/music", expectedLow);

        var kanye = root.Children.Single(c => c.Name == "Kanye West");
        var yandhi = kanye.Children.Single(c => c.Name == "Yandhi");
        var donda = kanye.Children.Single(c => c.Name == "Donda");

        Assert.True(yandhi.ExpectedLow);   // tagged
        Assert.False(donda.ExpectedLow);   // a sibling, not tagged
        Assert.False(kanye.ExpectedLow);   // the parent path was not tagged
        Assert.False(root.ExpectedLow);
    }

    [Fact]
    public void BuildMatchTree_WithoutPreferences_LeavesEveryNodeNotExpectedLow()
    {
        var rows = new[] { Row("/music/A/song.mp3", EnrichmentStatus.Matched) };

        var root = DashboardEndpoints.BuildMatchTree(rows, "/music");

        Assert.False(root.ExpectedLow);
        Assert.False(root.Children.Single().ExpectedLow);
    }

    [Theory]
    [InlineData(EnrichmentStatus.Matched, LibraryBuildStatus.Done, "written")]
    [InlineData(EnrichmentStatus.Matched, LibraryBuildStatus.Pending, "matched")]
    [InlineData(EnrichmentStatus.NeedsReview, LibraryBuildStatus.Pending, "review")]
    [InlineData(EnrichmentStatus.Failed, LibraryBuildStatus.Pending, "failed")]
    [InlineData(EnrichmentStatus.Pending, LibraryBuildStatus.Pending, "queued")]
    public void DeriveFileState_MapsStatusPairsToTheUiState(
        EnrichmentStatus enrichment, LibraryBuildStatus build, string expected)
    {
        Assert.Equal(expected, DashboardEndpoints.DeriveFileState(enrichment, build));
    }

    [Theory]
    [InlineData("a/b/x.flac", "a/b", true)]
    [InlineData("a/b/c/y.flac", "a/b", false)]
    [InlineData("a/b", "a/b", false)]
    [InlineData("loose.mp3", "", true)]
    [InlineData("a/loose.mp3", "", false)]
    public void IsDirectChild_OnlyMatchesFilesImmediatelyInsideTheFolder(
        string sourcePath, string prefix, bool expected)
    {
        Assert.Equal(expected, DashboardEndpoints.IsDirectChild(sourcePath, prefix));
    }
}

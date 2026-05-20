using System.IO.Abstractions.TestingHelpers;
using MusicHoarder.Api.Endpoints;

namespace MusicHoarder.Api.Tests.Endpoints;

public class DirectoryTreeTests
{
    private static MockFileSystem BuildLibrary() => new(new Dictionary<string, MockFileData>
    {
        ["/music/Artist A/2020 - Album/01 - Song.mp3"] = new("xxxx"),
        ["/music/Artist A/2020 - Album/02 - Song.flac"] = new("yyyyyy"),
        ["/music/Artist A/notes.txt"] = new("notes"),
        ["/music/Artist B/Unreleased/demo.opus"] = new("zz"),
    });

    [Fact]
    public void BuildTree_MissingDirectory_ReturnsNotExists()
    {
        var fs = new MockFileSystem();

        var result = DebugEndpoints.BuildTree(fs, "/does/not/exist", maxDepth: 6, maxEntries: 5000);

        Assert.False(result.Exists);
        Assert.Null(result.Tree);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void BuildTree_NestsFoldersAndFiles_WithSizeAndAudioFlag()
    {
        var fs = BuildLibrary();

        var result = DebugEndpoints.BuildTree(fs, "/music", maxDepth: 6, maxEntries: 5000);

        Assert.True(result.Exists);
        Assert.False(result.Truncated);
        Assert.NotNull(result.Tree);

        var artistA = result.Tree!.Children!.Single(c => c.Name == "Artist A");
        Assert.True(artistA.IsDirectory);

        var album = artistA.Children!.Single(c => c.Name == "2020 - Album");
        var mp3 = album.Children!.Single(c => c.Name == "01 - Song.mp3");
        Assert.False(mp3.IsDirectory);
        Assert.Equal(4, mp3.SizeBytes); // "xxxx"
        Assert.True(mp3.IsAudio);

        var txt = artistA.Children!.Single(c => c.Name == "notes.txt");
        Assert.False(txt.IsAudio);
    }

    [Fact]
    public void BuildTree_DepthCap_StopsDescendingAndFlagsTruncated()
    {
        var fs = BuildLibrary();

        // depth 1 => list the immediate children of /music (the artist dirs) but not their contents.
        var result = DebugEndpoints.BuildTree(fs, "/music", maxDepth: 1, maxEntries: 5000);

        Assert.True(result.Exists);
        Assert.True(result.Truncated);

        var artistA = result.Tree!.Children!.Single(c => c.Name == "Artist A");
        Assert.True(artistA.IsDirectory);
        Assert.Null(artistA.Children); // not descended past the cap
    }

    [Fact]
    public void BuildTree_EntryCap_StopsEmittingAndFlagsTruncated()
    {
        var fs = BuildLibrary();

        var result = DebugEndpoints.BuildTree(fs, "/music", maxDepth: 6, maxEntries: 2);

        Assert.True(result.Truncated);
        Assert.True(result.TotalEntries <= 2);
    }

    [Fact]
    public void BuildTree_ComputesRelativePaths()
    {
        var fs = BuildLibrary();

        var result = DebugEndpoints.BuildTree(fs, "/music", maxDepth: 6, maxEntries: 5000);

        var artistA = result.Tree!.Children!.Single(c => c.Name == "Artist A");
        var album = artistA.Children!.Single(c => c.Name == "2020 - Album");
        var mp3 = album.Children!.Single(c => c.Name == "01 - Song.mp3");

        Assert.Equal(string.Empty, result.Tree!.RelativePath);
        Assert.Equal(Path.Combine("Artist A", "2020 - Album", "01 - Song.mp3"), mp3.RelativePath);
    }
}

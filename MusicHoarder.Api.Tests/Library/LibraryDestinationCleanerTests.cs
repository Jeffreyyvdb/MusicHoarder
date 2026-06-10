using System.IO.Abstractions.TestingHelpers;
using MusicHoarder.Api.Library;

namespace MusicHoarder.Api.Tests.Library;

public class LibraryDestinationCleanerTests
{
    [Fact]
    public void DeleteManagedPathAndPrune_PathWithinRoot_DeletesAndPrunes()
    {
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            ["/dest/Artist/Album/01 - Track.flac"] = new MockFileData("audio"),
        });

        new LibraryDestinationCleaner(fileSystem).DeleteManagedPathAndPrune(
            "/dest/Artist/Album/01 - Track.flac", "/dest");

        Assert.False(fileSystem.File.Exists("/dest/Artist/Album/01 - Track.flac"));
        // Now-empty managed folders are pruned up to (but not including) the root.
        Assert.False(fileSystem.Directory.Exists("/dest/Artist/Album"));
        Assert.False(fileSystem.Directory.Exists("/dest/Artist"));
    }

    [Fact]
    public void DeleteManagedPathAndPrune_PathOutsideRoot_RefusesToDelete()
    {
        // The exact data-loss scenario: a demo row whose DestinationPath == SourcePath (the read-only
        // source mount) gets fed in as a "previous destination". The cleaner must never delete it.
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            ["/music-demo/Artist/Album/track.flac"] = new MockFileData("source"),
        });

        new LibraryDestinationCleaner(fileSystem).DeleteManagedPathAndPrune(
            "/music-demo/Artist/Album/track.flac", "/dest");

        Assert.True(fileSystem.File.Exists("/music-demo/Artist/Album/track.flac"));
    }
}

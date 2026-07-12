using MusicHoarder.Api.Navidrome;

namespace MusicHoarder.Api.Tests.Navidrome;

public class NavidromeLikeMatcherTests
{
    private static LikeMatchKey Key(
        string? sourcePath = null, string? destPath = null, string? mbid = null,
        string? artist = null, string? title = null, int? duration = null,
        string sourceDir = "/music/source", string destDir = "/music/destination")
        => NavidromeLikeMatcher.BuildKey(1, sourcePath, destPath, mbid, artist, title, duration, sourceDir, destDir);

    private static NavidromeSong Nav(
        string id, string? path = null, string? mbid = null, string? artist = null,
        string? title = null, int? duration = null)
        => new(id, title, artist, null, path, mbid, duration, null);

    [Fact]
    public void ToRelative_StripsBaseDir_AndNormalizes()
    {
        Assert.Equal("artist/album/01 - song.flac",
            NavidromeLikeMatcher.ToRelative("/music/destination/Artist/Album/01 - Song.flac", "/music/destination"));
    }

    [Fact]
    public void ToRelative_ReturnsNull_WhenNotUnderBase()
    {
        Assert.Null(NavidromeLikeMatcher.ToRelative("/somewhere/else/x.mp3", "/music/destination"));
        Assert.Null(NavidromeLikeMatcher.ToRelative(null, "/music/destination"));
        Assert.Null(NavidromeLikeMatcher.ToRelative("/music/destination/x.mp3", null));
    }

    [Fact]
    public void Find_MatchesByDestinationRelativePath()
    {
        var index = new NavidromeSongIndex([Nav("n1", path: "Artist/Album/01 - Song.flac")], 8);
        var matches = index.Find(Key(destPath: "/music/destination/Artist/Album/01 - Song.flac"));
        Assert.Equal("n1", Assert.Single(matches).Id);
    }

    [Fact]
    public void Find_MatchesBySourceRelativePath_WhenDestinationDiffers()
    {
        // A star on the raw source-library file; MH knows it by its (unchanged) source path even though
        // enrichment reorganized the destination folder.
        var index = new NavidromeSongIndex([Nav("n1", path: "RawArtist/raw file.mp3")], 8);
        var matches = index.Find(Key(
            sourcePath: "/music/source/RawArtist/raw file.mp3",
            destPath: "/music/destination/Clean Artist/Clean Album/01 - Song.flac"));
        Assert.Equal("n1", Assert.Single(matches).Id);
    }

    [Fact]
    public void Find_PathBeatsMbid_WhenBothPresent()
    {
        var pathHit = Nav("byPath", path: "Artist/Album/01 - Song.flac", mbid: "OTHER");
        var mbidHit = Nav("byMbid", path: "Different/Path.flac", mbid: "MB-1");
        var index = new NavidromeSongIndex([pathHit, mbidHit], 8);

        var matches = index.Find(Key(destPath: "/music/destination/Artist/Album/01 - Song.flac", mbid: "MB-1"));

        // Strongest tier only: the path match wins, the mbid-only candidate is not included.
        Assert.Equal("byPath", Assert.Single(matches).Id);
    }

    [Fact]
    public void Find_FallsBackToMbid_ThenFuzzy()
    {
        var mbidHit = Nav("byMbid", path: "Nav/Only/Path.flac", mbid: "MB-1");
        var index = new NavidromeSongIndex([mbidHit], 8);
        Assert.Equal("byMbid", Assert.Single(index.Find(Key(mbid: "MB-1"))).Id);

        var fuzzyHit = Nav("byFuzzy", artist: "Kanye West", title: "RoboCop", duration: 274);
        var fuzzyIndex = new NavidromeSongIndex([fuzzyHit], 8);
        var m = fuzzyIndex.Find(Key(artist: "Kanye West", title: "Robocop", duration: 275));
        Assert.Equal("byFuzzy", Assert.Single(m).Id);
    }

    [Fact]
    public void Find_Fuzzy_RespectsDurationTolerance()
    {
        var index = new NavidromeSongIndex([Nav("n1", artist: "A", title: "T", duration: 100)], 8);
        Assert.Empty(index.Find(Key(artist: "A", title: "T", duration: 200)));
        Assert.Single(index.Find(Key(artist: "A", title: "T", duration: 105)));
    }

    [Fact]
    public void Find_ReturnsAllCopiesSharingAPath()
    {
        // The same relative path exists in multiple Navidrome libraries → all copies match.
        var index = new NavidromeSongIndex(
        [
            Nav("copyA", path: "Artist/Album/01 - Song.flac"),
            Nav("copyB", path: "Artist/Album/01 - Song.flac"),
        ], 8);

        var matches = index.Find(Key(destPath: "/music/destination/Artist/Album/01 - Song.flac"));
        Assert.Equal(["copyA", "copyB"], matches.Select(m => m.Id).OrderBy(x => x));
    }
}

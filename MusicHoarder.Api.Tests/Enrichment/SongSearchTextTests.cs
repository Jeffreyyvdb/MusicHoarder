using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Enrichment;

public class SongSearchTextTests
{
    [Fact]
    public void EmbeddedTags_WinOverPath()
    {
        var song = Song("/root/music/Path Artist/Album/01 Path Title.mp3", artist: "Tag Artist", title: "Tag Title");

        var (artist, title) = SongSearchText.Resolve(song, "/root/music");

        Assert.Equal("Tag Artist", artist);
        Assert.Equal("Tag Title", title);
    }

    [Fact]
    public void Untagged_ArtistAlbumTrackLayout_DerivesTopLevelArtistAndFileTitle()
    {
        var song = Song("/root/music/Juice WRLD/Goodbye & Good Riddance/05 Lucid Dreams.mp3");

        var (artist, title) = SongSearchText.Resolve(song, "/root/music");

        Assert.Equal("Juice WRLD", artist);
        Assert.Equal("Lucid Dreams", title);
    }

    [Fact]
    public void Untagged_ArtistTrackLayout_DerivesArtistDirectlyUnderRoot()
    {
        var song = Song("/music/Adele/Hello.mp3");

        var (artist, title) = SongSearchText.Resolve(song, "/music");

        Assert.Equal("Adele", artist);
        Assert.Equal("Hello", title);
    }

    [Fact]
    public void Untagged_FileDirectlyUnderRoot_HasNoArtist()
    {
        var song = Song("/music/loose-track.mp3");

        var (artist, title) = SongSearchText.Resolve(song, "/music");

        Assert.Null(artist);
        Assert.Equal("loose-track", title);
    }

    [Theory]
    [InlineData("/m/Artist/Album/01 - Song Name.mp3", "Song Name")]
    [InlineData("/m/Artist/Album/01. Song Name.flac", "Song Name")]
    [InlineData("/m/Artist/Album/1-01 Song Name.mp3", "Song Name")]
    [InlineData("/m/Artist/Album/04 Field Trip.mp3", "Field Trip")]
    [InlineData("/m/Artist/Album/Song_Name.opus", "Song Name")]
    public void Untagged_StripsLeadingTrackNumberPrefixes(string path, string expectedTitle)
    {
        var song = Song(path);

        var (_, title) = SongSearchText.Resolve(song, "/m");

        Assert.Equal(expectedTitle, title);
    }

    [Fact]
    public void HasSearchableText_TrueForUntaggedFileWithUsablePath()
    {
        var song = Song("/root/music/Some Artist/Album/Track.mp3");

        Assert.True(SongSearchText.HasSearchableText(song, "/root/music"));
    }

    private static SongMetadata Song(string sourcePath, string? artist = null, string? title = null) => new()
    {
        OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
        SourcePath = sourcePath,
        FileName = sourcePath.Split('/')[^1],
        Extension = ".mp3",
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        Artist = artist,
        Title = title,
        EnrichmentStatus = EnrichmentStatus.Pending,
    };
}

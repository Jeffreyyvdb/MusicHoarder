using MusicHoarder.Api.Import;

namespace MusicHoarder.Api.Tests.Import;

/// <summary>
/// The flat-listing parser in <see cref="YouTubePlaylistReader"/>. The JSON is the shape yt-dlp
/// 2026.06 prints for <c>--flat-playlist --dump-single-json</c> on a playlist page (trimmed): entries
/// carry an id, the raw video title and a duration, and no channel.
/// </summary>
public class YouTubePlaylistReaderTests
{
    [Fact]
    public void Parse_ReadsThePlaylistAndItsVideos()
    {
        var json = """
        {
          "id": "PLexampleMusicVideosPlaylist00001",
          "title": "Music videos",
          "playlist_count": 65,
          "_type": "playlist",
          "thumbnails": [
            { "url": "https://i.ytimg.com/vi/ZJzr2Dsputk/hqdefault.jpg?sqp=small", "height": 94, "width": 168 },
            { "url": "https://i.ytimg.com/vi/ZJzr2Dsputk/hqdefault.jpg?sqp=large", "height": 188, "width": 336 }
          ],
          "entries": [
            { "_type": "url", "ie_key": "Youtube", "id": "ZJzr2Dsputk", "url": "https://www.youtube.com/watch?v=ZJzr2Dsputk",
              "title": "Kendrick Lamar - Alright (Official Music Video)", "duration": 415.0, "timestamp": null },
            { "_type": "url", "ie_key": "Youtube", "id": "tvTRZJ-4EyI", "url": "https://www.youtube.com/watch?v=tvTRZJ-4EyI",
              "title": "Kendrick Lamar - HUMBLE.", "duration": 184.0 }
          ]
        }
        """;

        var playlist = YouTubePlaylistReader.Parse(json);

        Assert.NotNull(playlist);
        Assert.Equal("PLexampleMusicVideosPlaylist00001", playlist!.Id);
        Assert.Equal("Music videos", playlist.Title);
        // The count YouTube reports, not the entries read: a preview reads only the first one.
        Assert.Equal(65, playlist.VideoCount);
        Assert.Equal("https://i.ytimg.com/vi/ZJzr2Dsputk/hqdefault.jpg?sqp=large", playlist.ThumbnailUrl);
        Assert.Collection(playlist.Entries,
            e =>
            {
                Assert.Equal("ZJzr2Dsputk", e.VideoId);
                Assert.Equal("Kendrick Lamar - Alright (Official Music Video)", e.Title);
                Assert.Equal(415_000, e.DurationMs);
            },
            e => Assert.Equal("tvTRZJ-4EyI", e.VideoId));
    }

    [Fact]
    public void Parse_DropsPrivateAndDeletedPlaceholders()
    {
        var json = """
        {
          "id": "PLx", "title": "Mixed", "_type": "playlist",
          "entries": [
            { "id": "aaaaaaaaaaa", "title": "[Private video]", "duration": null },
            { "id": "bbbbbbbbbbb", "title": "[Deleted video]" },
            { "id": "not-an-id", "title": "Broken entry" },
            { "id": "ccccccccccc", "title": "Artist - Song", "duration": 200 }
          ]
        }
        """;

        var playlist = YouTubePlaylistReader.Parse(json);

        var entry = Assert.Single(playlist!.Entries);
        Assert.Equal("ccccccccccc", entry.VideoId);
        // No playlist_count: fall back to what was read.
        Assert.Equal(1, playlist.VideoCount);
    }

    [Fact]
    public void Parse_AnEmptyPlaylistHasNoCover()
    {
        // YouTube reports its generic "no_thumbnail" image for a playlist with no videos.
        var json = """
        {
          "id": "PLempty", "title": "Nothing yet", "_type": "playlist", "playlist_count": 0,
          "thumbnails": [ { "url": "https://i.ytimg.com/img/no_thumbnail.jpg" } ],
          "entries": []
        }
        """;

        var playlist = YouTubePlaylistReader.Parse(json);

        Assert.NotNull(playlist);
        Assert.Null(playlist!.ThumbnailUrl);
        Assert.Empty(playlist.Entries);
        Assert.Equal(0, playlist.VideoCount);
    }

    [Theory]
    [InlineData("""{ "_type": "video", "id": "ZJzr2Dsputk", "title": "One video" }""")]
    [InlineData("""{ "_type": "playlist", "title": "No id" }""")]
    [InlineData("[]")]
    [InlineData("not json")]
    public void Parse_RejectsAnythingButAPlaylist(string json)
    {
        Assert.Null(YouTubePlaylistReader.Parse(json));
    }

    [Fact]
    public void PlaylistUrl_IsTheCanonicalPlaylistPage()
    {
        Assert.Equal(
            "https://www.youtube.com/playlist?list=PLexampleCoolMusic",
            YouTubePlaylistReader.PlaylistUrl("PLexampleCoolMusic"));
    }
}

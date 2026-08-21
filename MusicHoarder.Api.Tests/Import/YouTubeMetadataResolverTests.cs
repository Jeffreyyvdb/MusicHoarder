using MusicHoarder.Api.Import;

namespace MusicHoarder.Api.Tests.Import;

/// <summary>
/// Unit tests for the pure parse/derive helpers in <see cref="YouTubeMetadataResolver"/> — no real
/// yt-dlp binary is invoked. These pin the artist/title derivation, which is the fiddly part.
/// </summary>
public class YouTubeMetadataResolverTests
{
    [Fact]
    public void Parse_PrefersDiscreteArtistTrack_FromYouTubeMusic()
    {
        var json = """
        { "title": "Some Song (Official Video)", "track": "Some Song", "artist": "Real Artist",
          "duration": 213.0, "thumbnail": "https://img/thumb.jpg", "uploader": "Real Artist - Topic" }
        """;

        var result = YouTubeMetadataResolver.Parse(json);

        Assert.NotNull(result);
        Assert.Equal("Some Song", result!.Title);
        Assert.Equal("Real Artist", result.Artist);
        Assert.Equal(213000, result.DurationMs);
        Assert.Equal("https://img/thumb.jpg", result.ThumbnailUrl);
    }

    [Fact]
    public void Parse_SplitsArtistTitle_FromDashInVideoTitle()
    {
        var json = """{ "title": "DJ Cool - Summer Remix", "duration": 180 }""";

        var result = YouTubeMetadataResolver.Parse(json);

        Assert.NotNull(result);
        Assert.Equal("DJ Cool", result!.Artist);
        Assert.Equal("Summer Remix", result.Title);
    }

    [Fact]
    public void Parse_FallsBackToUploader_StrippingTopicSuffix()
    {
        var json = """{ "title": "Untitled Jam", "uploader": "Bedroom Producer - Topic" }""";

        var result = YouTubeMetadataResolver.Parse(json);

        Assert.NotNull(result);
        Assert.Equal("Bedroom Producer", result!.Artist);
        Assert.Equal("Untitled Jam", result.Title);
        Assert.Equal(0, result.DurationMs);
    }

    [Fact]
    public void Parse_ReturnsNull_WhenTitleMissing()
    {
        Assert.Null(YouTubeMetadataResolver.Parse("""{ "duration": 10 }"""));
        Assert.Null(YouTubeMetadataResolver.Parse("not json"));
    }

    [Fact]
    public void Parse_ReadsAlbum_FromYouTubeMusicEntry()
    {
        var json = """
        { "title": "Some Song", "track": "Some Song", "artist": "Real Artist",
          "album": " Real Album ", "duration": 200 }
        """;

        var result = YouTubeMetadataResolver.Parse(json);

        Assert.Equal("Real Album", result!.Album);
    }

    [Fact]
    public void Parse_LeavesAlbumNull_WhenTheVideoHasNone()
    {
        // A plain video carries no album — the import endpoint files it as a single instead.
        var result = YouTubeMetadataResolver.Parse("""{ "title": "DJ Cool - Summer Remix", "album": "  " }""");

        Assert.Null(result!.Album);
    }

    [Fact]
    public void Parse_PrefersTheLargestJpegThumbnail()
    {
        // yt-dlp orders thumbnails worst → best and YouTube's best variants are WebP; embedded WebP
        // artwork is read by far fewer players than JPEG, so the last JPEG wins over the last entry.
        var json = """
        { "title": "Song", "thumbnail": "https://img/flat.webp",
          "thumbnails": [
            { "url": "https://img/small.jpg" },
            { "url": "https://img/large.jpg?sqp=abc" },
            { "url": "https://img/largest.webp" }
          ] }
        """;

        var result = YouTubeMetadataResolver.Parse(json);

        Assert.Equal("https://img/large.jpg?sqp=abc", result!.ThumbnailUrl);
    }

    [Fact]
    public void Parse_FallsBackToFlatThumbnail_WhenNoJpegVariantExists()
    {
        var json = """{ "title": "Song", "thumbnail": "https://img/flat.webp", "thumbnails": [] }""";

        Assert.Equal("https://img/flat.webp", YouTubeMetadataResolver.Parse(json)!.ThumbnailUrl);
    }
}

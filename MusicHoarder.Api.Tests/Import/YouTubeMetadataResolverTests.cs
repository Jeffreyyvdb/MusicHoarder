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

    [Theory]
    [InlineData("ERROR: [youtube] xyz: Sign in to confirm you're not a bot.", "cookies")]
    [InlineData("ERROR: No supported JavaScript runtime could be found", "JavaScript runtime")]
    [InlineData("ERROR: [youtube] xyz: Private video. Sign in if you've been granted access.", "private")]
    [InlineData("ERROR: unable to download: HTTP Error 429: Too Many Requests", "rate-limiting")]
    public void Classify_MapsKnownStderrToActionableHint(string stderr, string expectedFragment)
    {
        var hint = YouTubeMetadataResolver.Classify(stderr);
        Assert.NotNull(hint);
        Assert.Contains(expectedFragment, hint!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("some unrecognized failure")]
    public void Classify_ReturnsNull_ForUnknownStderr(string stderr)
    {
        Assert.Null(YouTubeMetadataResolver.Classify(stderr));
    }
}

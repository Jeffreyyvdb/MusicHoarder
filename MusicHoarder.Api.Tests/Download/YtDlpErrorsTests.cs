using MusicHoarder.Api.Download;

namespace MusicHoarder.Api.Tests.Download;

/// <summary>
/// Pins the yt-dlp stderr classification shared by the wishlist downloader and the URL-import probe.
/// The format-unavailable case matters most: it reads like the track is at fault when it is really
/// the server's extraction environment, and that string lands in the wishlist UI.
/// </summary>
public class YtDlpErrorsTests
{
    [Theory]
    [InlineData("ERROR: [youtube] xyz: Sign in to confirm you're not a bot.", "cookies")]
    [InlineData("ERROR: No supported JavaScript runtime could be found", "JavaScript runtime")]
    [InlineData("ERROR: [youtube] xyz: Private video. Sign in if you've been granted access.", "private")]
    [InlineData("ERROR: unable to download: HTTP Error 429: Too Many Requests", "rate-limiting")]
    [InlineData("ERROR: [youtube] TC2Af_-kK6M: Requested format is not available. Use --list-formats for a list of available formats", "Nothing is wrong with the track")]
    [InlineData("WARNING: Only images are available for download. use --list-formats to see them", "Nothing is wrong with the track")]
    public void Classify_MapsKnownStderrToActionableHint(string stderr, string expectedFragment)
    {
        var hint = YtDlpErrors.Classify(stderr);
        Assert.NotNull(hint);
        Assert.Contains(expectedFragment, hint!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Classify_NamesThePoToken_WhenYtDlpBlamedIt()
    {
        var stderr = """
            WARNING: [youtube] xyz: tv_simply client https formats require a GVS PO Token which was not provided. They will be skipped as they may yield HTTP Error 403.
            ERROR: [youtube] xyz: Requested format is not available. Use --list-formats for a list of available formats
            """;

        Assert.Contains("PO token", YtDlpErrors.Classify(stderr)!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Classify_NamesTheStaleBinary_OverTheBotCheckItCascadesInto()
    {
        // A retired player client 400s on the player API and the extraction then falls through to the
        // bot-check message. New cookies do not fix that; a current yt-dlp does.
        var stderr = """
            WARNING: [youtube] YouTube said: ERROR - Precondition check failed.
            WARNING: [youtube] Unable to download API page: HTTP Error 400: Bad Request
            ERROR: [youtube] xyz: Sign in to confirm you’re not a bot. This helps protect our community.
            """;

        Assert.Contains("out of date", YtDlpErrors.Classify(stderr)!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Classify_PrefersTheBotCheck_OverTheFormatSymptom()
    {
        // The bot check is the cause and the format error its downstream symptom; the hint has to
        // name the fix (cookies), not the symptom.
        var stderr = """
            ERROR: [youtube] xyz: Sign in to confirm you're not a bot.
            ERROR: [youtube] xyz: Requested format is not available.
            """;

        Assert.Contains("cookies", YtDlpErrors.Classify(stderr)!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("some unrecognized failure")]
    public void Classify_ReturnsNull_ForUnknownStderr(string stderr)
    {
        Assert.Null(YtDlpErrors.Classify(stderr));
    }

    [Theory]
    [InlineData("ERROR: Requested format is not available", true)]
    [InlineData("WARNING: Only images are available for download", true)]
    [InlineData("ERROR: Video unavailable", false)]
    [InlineData("", false)]
    public void LooksLikeNoUsableFormats_DetectsTheAnonymousRetryTrigger(string stderr, bool expected)
    {
        Assert.Equal(expected, YtDlpErrors.LooksLikeNoUsableFormats(stderr));
    }

    [Fact]
    public void Describe_KeepsTheRawTail_AlongsideTheHint()
    {
        var described = YtDlpErrors.Describe(1, "ERROR: [youtube] xyz: Requested format is not available.");

        Assert.Contains("Nothing is wrong with the track", described);
        Assert.Contains("exited 1:", described);
        Assert.Contains("Requested format is not available", described);
    }

    [Fact]
    public void Describe_FallsBackToTheBareForm_WhenNothingClassifies()
    {
        Assert.Equal("exited 2: ERROR: something new", YtDlpErrors.Describe(2, "ERROR: something new"));
    }

    [Fact]
    public void Tail_KeepsTheEnd_WhereYtDlpPutsTheError()
    {
        // yt-dlp prints warnings first and the fatal ERROR last, so a head-truncation would drop the
        // one line that says what went wrong.
        var stderr = new string('x', 900) + "ERROR: the actual failure";

        var tail = YtDlpErrors.Tail(stderr);

        Assert.EndsWith("ERROR: the actual failure", tail);
        Assert.StartsWith("…", tail);
    }
}

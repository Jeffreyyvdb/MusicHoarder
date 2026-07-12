using MusicHoarder.Api.Download;

namespace MusicHoarder.Api.Tests.Download;

/// <summary>
/// Unit tests for the pure helpers in <see cref="YtDlpDownloadProvider"/> — no real yt-dlp binary
/// is invoked. These pin behaviors that are easy to regress: thumbnail-only output filtering,
/// extra-arg splitting, and flag-injection-safe query construction.
/// </summary>
public class YtDlpDownloadProviderTests
{
    [Fact]
    public void LocateProducedFile_ReturnsAudio_WhenAudioExists()
    {
        var dir = NewTempDir();
        var stem = "abc123";
        File.WriteAllText(Path.Combine(dir, stem + ".opus"), "audio");
        File.WriteAllText(Path.Combine(dir, stem + ".webp"), "thumb");

        var produced = YtDlpDownloadProvider.LocateProducedFile(dir, stem);

        Assert.NotNull(produced);
        Assert.EndsWith(".opus", produced);
    }

    [Theory]
    [InlineData(".webp")]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".png")]
    public void LocateProducedFile_ReturnsNull_ForThumbnailOnlyOutput(string ext)
    {
        // Regression for commit 6b277fa: a thumbnail-only result must NOT be reported as the
        // downloaded file (it would mark the item Downloaded pointing at an image the scanner ignores).
        var dir = NewTempDir();
        var stem = "thumbonly";
        File.WriteAllText(Path.Combine(dir, stem + ext), "thumb");

        Assert.Null(YtDlpDownloadProvider.LocateProducedFile(dir, stem));
    }

    [Fact]
    public void LocateProducedFile_IgnoresIntermediatePartFiles()
    {
        var dir = NewTempDir();
        var stem = "partial";
        File.WriteAllText(Path.Combine(dir, stem + ".opus.part"), "partial");

        Assert.Null(YtDlpDownloadProvider.LocateProducedFile(dir, stem));
    }

    [Fact]
    public void SplitArgs_Empty_YieldsNothing()
    {
        Assert.Empty(YtDlpDownloadProvider.SplitArgs(null));
        Assert.Empty(YtDlpDownloadProvider.SplitArgs(""));
        Assert.Empty(YtDlpDownloadProvider.SplitArgs("   "));
    }

    [Fact]
    public void SplitArgs_SplitsOnWhitespace()
    {
        var args = YtDlpDownloadProvider.SplitArgs("--extractor-args youtube:player_client=tv --retries 8").ToArray();
        Assert.Equal(new[] { "--extractor-args", "youtube:player_client=tv", "--retries", "8" }, args);
    }

    [Fact]
    public void SplitArgs_KeepsQuotedSegmentsWhole()
    {
        var args = YtDlpDownloadProvider.SplitArgs("--proxy \"http://user:pass@host with space\"").ToArray();
        Assert.Equal(new[] { "--proxy", "http://user:pass@host with space" }, args);
    }

    [Fact]
    public void BuildSearchQuery_PrefixesYtsearch_NeutralizingFlagInjection()
    {
        // A malicious-looking title must remain a single positional arg that can't be parsed as a flag,
        // because the ytsearch1: prefix means the value never starts with '-'.
        var req = new DownloadRequest("--exec rm -rf", "; whoami", null, null, 1000, "/tmp");
        var query = YtDlpDownloadProvider.BuildSearchQuery(req);

        Assert.StartsWith("ytsearch1:", query);
        Assert.DoesNotContain('\n', query);
    }

    [Fact]
    public void BuildSearchQuery_UsesTitleOnly_WhenArtistBlank()
    {
        var req = new DownloadRequest("", "Some Song", null, null, 1000, "/tmp");
        Assert.Equal("ytsearch1:Some Song", YtDlpDownloadProvider.BuildSearchQuery(req));
    }

    [Fact]
    public void BuildTarget_UsesSourceUrlDirectly_ForUrlImports()
    {
        // A single-track URL import must download that exact URL, not an artist/title search — the only
        // way to acquire a specific YouTube remix that has no streaming-service equivalent.
        var req = new DownloadRequest("DJ Cool", "Summer Remix", null, null, 1000, "/tmp",
            SourceUrl: "https://www.youtube.com/watch?v=dQw4w9WgXcQ");

        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", YtDlpDownloadProvider.BuildTarget(req));
    }

    [Fact]
    public void BuildTarget_FallsBackToSearch_WhenNoSourceUrl()
    {
        var req = new DownloadRequest("Artist", "Song", null, null, 1000, "/tmp");
        Assert.Equal("ytsearch1:Artist Song", YtDlpDownloadProvider.BuildTarget(req));
    }

    [Theory]
    [InlineData("ERROR: Unable to download webpage", true)]
    [InlineData("no results found", true)]
    [InlineData("ERROR: unable to download video data: HTTP Error 403: Forbidden", false)]
    public void LooksLikeNoResults_ClassifiesTransientFailuresVsEmpty(string stderr, bool expected)
    {
        Assert.Equal(expected, YtDlpDownloadProvider.LooksLikeNoResults(stderr));
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mh-ytdlp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Tests.Download;

/// <summary>
/// What a clip fetch records, and what it leaves on disk. A video+audio format downloads each
/// stream to <c>&lt;stem&gt;.f&lt;format&gt;.&lt;ext&gt;</c> and only the merge writes <c>&lt;stem&gt;.mp4</c>.
/// Regression: YouTube cut the video stream off mid-download, yt-dlp (which carries on after a
/// download error by default) still finished the audio stream, and the fetch recorded that
/// <c>.f140.m4a</c> as the clip. The videos directory sits inside the download root, so the scanner
/// then indexed the audio stream as a nameless song and the builder put it in the library.
/// These run a stand-in yt-dlp that writes what the real one would.
/// </summary>
public class MusicVideoDownloadOutputTests : IDisposable
{
    private readonly string tempDir =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"mh-videofetch-{Guid.NewGuid():N}")).FullName;

    private string VideoDir => Path.Combine(tempDir, "videos");

    [Fact]
    public async Task VideoStreamDiesMidway_FailsAndLeavesNoAudioStreamBehind()
    {
        var downloader = Downloader("""
            printf 'partial' > "$stem.f137.mp4.part"
            printf 'audio' > "$stem.f140.m4a"
            echo "ERROR: unable to download video data: HTTP Error 403: Forbidden" >&2
            exit 1
            """);

        var result = await downloader.DownloadAsync(PinnedRequest(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.NotFound); // a transient failure, so the fetch can be retried
        Assert.Null(result.FilePath);
        Assert.Empty(Directory.EnumerateFiles(VideoDir));
    }

    [Fact]
    public async Task MergedClip_IsTheResult()
    {
        var downloader = Downloader("""
            printf 'video' > "$stem.mp4"
            echo "dQw4w9WgXcQ"
            echo "212.0"
            exit 0
            """);

        var result = await downloader.DownloadAsync(PinnedRequest(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.EndsWith(".mp4", result.FilePath);
        Assert.Equal("dQw4w9WgXcQ", result.YouTubeVideoId);
        Assert.Equal(212, result.DurationSeconds);
        Assert.Equal(result.FilePath, Assert.Single(Directory.EnumerateFiles(VideoDir)));
    }

    [Fact]
    public async Task MergedClip_UnmergedStreamsKeptAlongsideItAreRemoved()
    {
        // e.g. a --keep-video in the configured extra args: the clip is fine, the streams must go.
        var downloader = Downloader("""
            printf 'video' > "$stem.mp4"
            printf 'video-only' > "$stem.f137.mp4"
            printf 'audio' > "$stem.f140.m4a"
            exit 0
            """);

        var result = await downloader.DownloadAsync(PinnedRequest(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(result.FilePath, Assert.Single(Directory.EnumerateFiles(VideoDir)));
    }

    [Fact]
    public async Task CancelledBeforeTheMerge_LeavesNoAudioStreamBehind()
    {
        // The API stopping (a deploy) between the streams finishing and the merge.
        var downloader = Downloader("""
            printf 'video-only' > "$stem.f137.mp4"
            printf 'audio' > "$stem.f140.m4a"
            sleep 30
            """);
        using var cts = new CancellationTokenSource();

        var fetch = downloader.DownloadAsync(PinnedRequest(), cts.Token);
        await WaitForAsync(() => Directory.Exists(VideoDir)
            && Directory.EnumerateFiles(VideoDir, "*.f140.m4a").Any());
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
        Assert.Empty(Directory.EnumerateFiles(VideoDir));
    }

    [Theory]
    [InlineData("abc.mp4", true)]
    [InlineData("abc.webm", true)]
    [InlineData("abc.f140.m4a", false)] // the unmerged audio stream
    [InlineData("abc.f137.mp4", false)] // the unmerged video stream: no sound
    [InlineData("abc.mp4.part", false)]
    [InlineData("abc.temp.mp4", false)] // the merge's in-flight output
    [InlineData("abc.m4a", false)]      // an audio-only result is not a clip
    [InlineData("abc.jpg", false)]
    public void LocateFinishedVideo_AcceptsOnlyTheFinishedClip(string fileName, bool accepted)
    {
        File.WriteAllText(Path.Combine(tempDir, fileName), "x");

        var found = MusicVideoDownloader.LocateFinishedVideo(tempDir, "abc");

        Assert.Equal(accepted ? Path.Combine(tempDir, fileName) : null, found);
    }

    /// <summary>
    /// A downloader whose yt-dlp is a shell script: it reads the <c>-o</c> template into
    /// <c>$stem</c> (the path without <c>.%(ext)s</c>) and then runs <paramref name="scenario"/>.
    /// </summary>
    private MusicVideoDownloader Downloader(string scenario)
    {
        var script = Path.Combine(tempDir, "yt-dlp");
        File.WriteAllText(script, $$"""
            #!/bin/sh
            while [ $# -gt 0 ]; do
              if [ "$1" = "-o" ]; then out="$2"; shift; fi
              shift
            done
            stem="${out%".%(ext)s"}"
            {{scenario}}
            """);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        return new MusicVideoDownloader(
            Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
            {
                SourceDirectory = "/source",
                DestinationDirectory = "/dest",
                MusicVideoDirectory = VideoDir,
                YtDlpPath = script,
                DownloadSleepSeconds = 0,
            }),
            new UnusedProbe(),
            NullLogger<MusicVideoDownloader>.Instance);
    }

    /// <summary>An explicit pin is downloaded verbatim: no search, no probe.</summary>
    private static MusicVideoFetchRequest PinnedRequest() =>
        new("dQw4w9WgXcQ", PinIsExplicit: true, "Artist", "Song");

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("the stand-in yt-dlp never wrote its streams");
            await Task.Delay(20);
        }
    }

    private sealed class UnusedProbe : IMusicVideoProbe
    {
        public Task<MusicVideoProbeResult> ProbeAsync(string videoIdOrUrl, CancellationToken ct) =>
            throw new InvalidOperationException("an explicit pin is never probed");
    }

    public void Dispose()
    {
        try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
    }
}

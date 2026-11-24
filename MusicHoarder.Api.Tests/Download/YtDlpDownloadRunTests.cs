using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Tests.Download;

/// <summary>
/// <see cref="YtDlpDownloadProvider.DownloadAsync"/> end to end against a stand-in yt-dlp: a shell script
/// that answers the flat search with canned results, "downloads" by writing a file, and logs every call.
/// </summary>
public sealed class YtDlpDownloadRunTests : IDisposable
{
    private const string MakeHerSaySearch = """
        {"entries":[
          {"id":"Xt22KvnRSL4","title":"Kid Cudi - Make Her Say (Clean Version) ft. Kanye West, Common","ie_key":"Youtube"},
          {"id":"N4yOHlNKcxs","title":"Kid Cudi - Make Her Say (Dirty Official Video)","ie_key":"Youtube"}
        ]}
        """;

    private readonly string tempDir = Directory.CreateTempSubdirectory("ytdlp-run-").FullName;
    private string CallLog => Path.Combine(tempDir, "calls.log");
    private string DownloadDir => Path.Combine(tempDir, "downloads");

    [Fact]
    public async Task Search_SkipsTheCleanEdit_AndDownloadsTheOriginalByItsWatchUrl()
    {
        var provider = Provider(search: Answer(MakeHerSaySearch), download: WritesAudio);

        var result = await provider.DownloadAsync(Request(), CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal("N4yOHlNKcxs", result.SourceId);
        Assert.True(File.Exists(result.FilePath));
        var calls = Calls();
        Assert.Equal(2, calls.Length);
        Assert.Contains("ytsearch5:Kid Cudi Make Her Say", calls[0]);
        Assert.EndsWith("https://www.youtube.com/watch?v=N4yOHlNKcxs", calls[1]);
    }

    [Fact]
    public async Task Search_WithOnlyCleanEdits_IsMissing_AndDownloadsNothing()
    {
        var provider = Provider(
            search: Answer("""{"entries":[{"id":"a","title":"Make Her Say (Clean)"},{"id":"b","title":"Make Her Say [Edited]"}]}"""),
            download: WritesAudio);

        var result = await provider.DownloadAsync(Request(), CancellationToken.None);

        Assert.True(result.NotFound);
        Assert.True(result.FallsThrough);
        Assert.Single(Calls());
    }

    [Theory]
    [InlineData("echo 'ERROR: [youtube:search] something broke' >&2; exit 1")]
    [InlineData("exit 137")]
    public async Task Search_ThatCrashes_FailsInsteadOfReportingNoResults(string search)
    {
        var provider = Provider(search, download: WritesAudio);

        var result = await provider.DownloadAsync(Request(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.NotFound);
        Assert.False(result.FallsThrough);
        Assert.Single(Calls());
    }

    [Fact]
    public async Task Search_WithNoResults_IsMissing()
    {
        var provider = Provider(search: Answer("""{"entries":[]}"""), download: WritesAudio);

        var result = await provider.DownloadAsync(Request(), CancellationToken.None);

        Assert.True(result.NotFound);
        Assert.Single(Calls());
    }

    [Fact]
    public async Task PastedUrl_IsDownloadedAsIs_WithoutASearch()
    {
        var provider = Provider(search: "exit 1", download: WritesAudio);

        var result = await provider.DownloadAsync(
            Request() with { SourceUrl = "https://www.youtube.com/watch?v=dQw4w9WgXcQ" }, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var call = Assert.Single(Calls());
        Assert.EndsWith("https://www.youtube.com/watch?v=dQw4w9WgXcQ", call);
    }

    private const string WritesAudio = """
        printf 'x' > "$stem.opus"
        echo "${target##*=}"
        """;

    private static string Answer(string json) => $"cat <<'JSON'\n{json}\nJSON";

    private DownloadRequest Request() => new("Kid Cudi", "Make Her Say", null, null, 216_000, DownloadDir);

    private string[] Calls() => File.Exists(CallLog) ? File.ReadAllLines(CallLog) : [];

    /// <summary>
    /// A provider whose yt-dlp runs <paramref name="search"/> for a flat search and otherwise
    /// <paramref name="download"/>, with <c>$stem</c> (the <c>-o</c> template without <c>.%(ext)s</c>)
    /// and <c>$target</c> (the last argument) set.
    /// </summary>
    private YtDlpDownloadProvider Provider(string search, string download)
    {
        var script = Path.Combine(tempDir, "yt-dlp");
        File.WriteAllText(script, $$"""
            #!/bin/sh
            echo "$*" >> '{{CallLog}}'
            case " $* " in
              *" --flat-playlist "*)
            {{search}}
                exit 0
                ;;
            esac
            for arg; do target="$arg"; done
            while [ $# -gt 0 ]; do
              if [ "$1" = "-o" ]; then out="$2"; shift; fi
              shift
            done
            stem="${out%".%(ext)s"}"
            {{download}}
            """);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        return new YtDlpDownloadProvider(
            Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
            {
                SourceDirectory = "/source",
                DestinationDirectory = "/dest",
                YtDlpPath = script,
                DownloadSleepSeconds = 0,
            }),
            NullLogger<YtDlpDownloadProvider>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
    }
}

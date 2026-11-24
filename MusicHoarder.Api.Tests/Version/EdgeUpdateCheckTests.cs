using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Version;

namespace MusicHoarder.Api.Tests.Version;

/// <summary>
/// An edge build is labelled with the release before its commit, so when that commit is then released the
/// label reads older than the latest release. These pin that the banner follows the commit, not the label.
/// </summary>
public class EdgeUpdateCheckTests
{
    private const string Edge = "2.6.0-edge.gbb0add0";
    private const string ReleaseUrl = "https://api.github.com/repos/o/r/releases/latest";
    private const string CompareUrl = "https://api.github.com/repos/o/r/compare/v2.7.0...bb0add0?per_page=1";

    [Theory]
    [InlineData("2.6.0-edge.gbb0add0", "bb0add0")]
    [InlineData("2.6.0-edge.g0123abc", "0123abc")]
    [InlineData("2.7.0", null)]
    [InlineData("2.7.0-rc1", null)]
    [InlineData("dev", null)]
    [InlineData("2.6.0-edge.gxyz", null)]
    [InlineData(null, null)]
    public void EdgeCommit_ReadsTheShaAnEdgeBuildCarries(string? version, string? expected)
    {
        Assert.Equal(expected, SemVerComparer.EdgeCommit(version));
    }

    [Theory]
    [InlineData(null, false)]  // not answered yet: no banner rather than a wrong one
    [InlineData(true, false)]  // the running commit holds the release
    [InlineData(false, true)]  // it does not
    public void EdgeBuild_FollowsTheCommitAnswer(bool? hasLatest, bool expected)
    {
        var snapshot = Snapshot("2.7.0", hasLatest);
        Assert.Equal(expected, SemVerComparer.IsUpdateAvailable(Edge, snapshot));
    }

    [Fact]
    public void ReleaseBuild_StillComparesVersions()
    {
        Assert.True(SemVerComparer.IsUpdateAvailable("2.6.0", Snapshot("2.7.0", null)));
        Assert.False(SemVerComparer.IsUpdateAvailable("2.7.0", Snapshot("2.7.0", null)));
    }

    [Fact]
    public void EdgeBuild_OfTheLatestReleaseNeverAsks()
    {
        Assert.False(SemVerComparer.IsUpdateAvailable("2.7.0-edge.gabcdef0", Snapshot("2.7.0", false)));
    }

    [Theory]
    [InlineData("identical", true)]
    [InlineData("ahead", true)]
    [InlineData("behind", false)]
    [InlineData("diverged", false)]
    public async Task Monitor_AsksGitHubHowTheRunningCommitRelatesToTheRelease(string status, bool expected)
    {
        var handler = new Handler
        {
            [ReleaseUrl] = (HttpStatusCode.OK, ReleaseJson),
            [CompareUrl] = (HttpStatusCode.OK, $$"""{"status":"{{status}}"}"""),
        };
        var monitor = Monitor(handler, Edge);

        await monitor.RefreshAsync(CancellationToken.None);

        Assert.Equal("v2.7.0", monitor.Current.LatestTag);
        Assert.Equal(expected, monitor.Current.RunningBuildHasLatest);
        Assert.Equal(!expected, SemVerComparer.IsUpdateAvailable(Edge, monitor.Current));
    }

    [Fact]
    public async Task Monitor_AsksOncePerRelease()
    {
        var handler = new Handler
        {
            [ReleaseUrl] = (HttpStatusCode.OK, ReleaseJson),
            [CompareUrl] = (HttpStatusCode.OK, """{"status":"identical"}"""),
        };
        var monitor = Monitor(handler, Edge);

        await monitor.RefreshAsync(CancellationToken.None);
        handler[ReleaseUrl] = (HttpStatusCode.NotModified, "");
        await monitor.RefreshAsync(CancellationToken.None);

        Assert.Equal(1, handler.Requested.Count(u => u == CompareUrl));
        Assert.True(monitor.Current.RunningBuildHasLatest);
    }

    [Fact]
    public async Task Monitor_RetriesAFailedCompareOnTheNextCycle()
    {
        var handler = new Handler
        {
            [ReleaseUrl] = (HttpStatusCode.OK, ReleaseJson),
            [CompareUrl] = (HttpStatusCode.Forbidden, ""),
        };
        var monitor = Monitor(handler, Edge);

        await monitor.RefreshAsync(CancellationToken.None);
        Assert.Null(monitor.Current.RunningBuildHasLatest);
        Assert.False(SemVerComparer.IsUpdateAvailable(Edge, monitor.Current));

        handler[ReleaseUrl] = (HttpStatusCode.NotModified, "");
        handler[CompareUrl] = (HttpStatusCode.OK, """{"status":"behind"}""");
        await monitor.RefreshAsync(CancellationToken.None);

        Assert.False(monitor.Current.RunningBuildHasLatest);
        Assert.True(SemVerComparer.IsUpdateAvailable(Edge, monitor.Current));
    }

    [Theory]
    [InlineData("2.6.0")]               // a release build compares versions only
    [InlineData("2.7.0-edge.gabcdef0")] // an edge build already numbered after the latest release
    public async Task Monitor_DoesNotCompareWhenTheVersionSettlesIt(string running)
    {
        var handler = new Handler { [ReleaseUrl] = (HttpStatusCode.OK, ReleaseJson) };
        var monitor = Monitor(handler, running);

        await monitor.RefreshAsync(CancellationToken.None);

        Assert.Equal([ReleaseUrl], handler.Requested);
        Assert.Null(monitor.Current.RunningBuildHasLatest);
    }

    private const string ReleaseJson = """
        {"tag_name":"v2.7.0","html_url":"https://github.com/o/r/releases/tag/v2.7.0","published_at":"2026-11-23T19:04:38Z"}
        """;

    private static ReleaseUpdateSnapshot Snapshot(string latest, bool? hasLatest) =>
        new(latest, null, null, DateTime.UtcNow, $"v{latest}", hasLatest);

    private static ReleaseUpdateMonitor Monitor(Handler handler, string running) =>
        new(new Factory(handler),
            Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
            {
                SourceDirectory = "/s",
                DestinationDirectory = "/d",
                UpdateCheckRepo = "o/r",
            }),
            NullLogger<ReleaseUpdateMonitor>.Instance)
        {
            RunningVersion = running,
        };

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.github.com/") };
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _responses = [];
        public List<string> Requested { get; } = [];

        public (HttpStatusCode Status, string Body) this[string url]
        {
            set => _responses[url] = value;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requested.Add(url);
            var (status, body) = _responses.TryGetValue(url, out var r) ? r : (HttpStatusCode.NotFound, "");
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}

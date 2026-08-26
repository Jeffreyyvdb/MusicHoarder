using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Tests.Download;

/// <summary>
/// The automatic path's use of the probe: a candidate may be skipped only when it was positively
/// measured as a single still image. Everything else — an unmeasurable candidate, a probe failure,
/// a candidate past the probe budget — stays eligible, because a broken probe must never leave a
/// song with no video at all.
/// </summary>
public class MusicVideoStaticSkipTests
{
    private sealed class FakeProbe : IMusicVideoProbe
    {
        public required Dictionary<string, MusicVideoMotion> Motions { get; init; }
        public List<string> Probed { get; } = [];

        public Task<MusicVideoProbeResult> ProbeAsync(string videoIdOrUrl, CancellationToken ct)
        {
            Probed.Add(videoIdOrUrl);
            var motion = Motions.GetValueOrDefault(videoIdOrUrl, MusicVideoMotion.Unknown);
            return Task.FromResult(new MusicVideoProbeResult(
                videoIdOrUrl, "title", "channel", 200, motion, 10_000_000, null, null, false, null));
        }
    }

    private static MusicVideoDownloader Downloader(IMusicVideoProbe probe) =>
        new(Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/tmp",
            DestinationDirectory = "/tmp",
        }), probe, NullLogger<MusicVideoDownloader>.Instance);

    private static List<MusicVideoDownloader.SearchCandidate> Ranked(params string[] ids) =>
        [.. ids.Select(id => new MusicVideoDownloader.SearchCandidate(id, $"{id} title", "channel", 200))];

    private static MusicVideoFetchRequest Request() =>
        new(null, PinIsExplicit: false, "Artist", "Song");

    [Fact]
    public async Task SkipsAStaticTopHitAndTakesTheNextRealVideo()
    {
        var probe = new FakeProbe
        {
            Motions = new() { ["a"] = MusicVideoMotion.Static, ["b"] = MusicVideoMotion.RealVideo },
        };

        var picked = await Downloader(probe).PickWatchableAsync(
            Ranked("a", "b", "c"), probeLimit: 3, Request(), CancellationToken.None);

        Assert.Equal("b", picked!.Id);
        Assert.Equal(["a", "b"], probe.Probed); // stopped as soon as one passed
    }

    [Fact]
    public async Task AcceptsALowMotionCandidate_OnlyStillImagesAreSkipped()
    {
        // A lyric video or a slideshow is a legitimate backdrop; the complaint this guards against
        // is specifically the cover image held for the whole song.
        var probe = new FakeProbe { Motions = new() { ["a"] = MusicVideoMotion.LowMotion } };

        var picked = await Downloader(probe).PickWatchableAsync(
            Ranked("a", "b"), probeLimit: 3, Request(), CancellationToken.None);

        Assert.Equal("a", picked!.Id);
    }

    [Fact]
    public async Task AcceptsAnUnmeasurableCandidate_AProbeFailureIsNotAVeto()
    {
        var probe = new FakeProbe { Motions = new() { ["a"] = MusicVideoMotion.Unknown } };

        var picked = await Downloader(probe).PickWatchableAsync(
            Ranked("a", "b"), probeLimit: 3, Request(), CancellationToken.None);

        Assert.Equal("a", picked!.Id);
    }

    [Fact]
    public async Task StopsProbingAtTheBudgetAndFallsBackToTheNextUnprobedCandidate()
    {
        var probe = new FakeProbe
        {
            Motions = new()
            {
                ["a"] = MusicVideoMotion.Static,
                ["b"] = MusicVideoMotion.Static,
                ["c"] = MusicVideoMotion.Static,
            },
        };

        var picked = await Downloader(probe).PickWatchableAsync(
            Ranked("a", "b", "c", "d"), probeLimit: 2, Request(), CancellationToken.None);

        Assert.Equal(["a", "b"], probe.Probed);
        Assert.Equal("c", picked!.Id); // unprobed, so still a legitimate choice
    }

    [Fact]
    public async Task EverythingIsAStillImage_DownloadsNothing()
    {
        var probe = new FakeProbe
        {
            Motions = new() { ["a"] = MusicVideoMotion.Static, ["b"] = MusicVideoMotion.Static },
        };

        var picked = await Downloader(probe).PickWatchableAsync(
            Ranked("a", "b"), probeLimit: 3, Request(), CancellationToken.None);

        Assert.Null(picked);
    }

    [Fact]
    public async Task NoCandidates_ReturnsNullWithoutProbing()
    {
        var probe = new FakeProbe { Motions = [] };

        var picked = await Downloader(probe).PickWatchableAsync(
            [], probeLimit: 3, Request(), CancellationToken.None);

        Assert.Null(picked);
        Assert.Empty(probe.Probed);
    }
}

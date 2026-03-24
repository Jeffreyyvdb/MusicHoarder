using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Scanner;

namespace MusicHoarder.Api.Tests.Jobs;

public class ProgressSnapshotFactoryTests
{
    [Fact]
    public void Create_WithRunningStepsAndProgress_AggregatesCountersAndLabels()
    {
        var manager = new JobManager();
        manager.TryStartJob(JobType.Scan, out _, out _);
        manager.TryStartJob(JobType.Enrich, out _, out _);

        var scanTracker = new ScanProgressTracker();
        var fpTracker = new FingerprintProgressTracker();
        var enrichmentTracker = new EnrichmentProgressTracker();
        var buildTracker = new LibraryBuilderProgressTracker();

        var scanId = Guid.NewGuid();
        scanTracker.Start(scanId, totalFiles: 12);
        scanTracker.IncrementNew();
        scanTracker.IncrementChanged();
        scanTracker.IncrementFailed();
        scanTracker.AddSkipped(4);

        var fpRunId = Guid.NewGuid();
        fpTracker.StartRun(fpRunId, totalTracks: 5);
        fpTracker.IncrementFingerprinted();
        fpTracker.IncrementFailed();

        var enrichRunId = Guid.NewGuid();
        enrichmentTracker.StartCycle(enrichRunId, totalTracks: 3);
        enrichmentTracker.IncrementEnriched();
        enrichmentTracker.IncrementNeedsReview();
        enrichmentTracker.IncrementFailed();

        var buildRunId = Guid.NewGuid();
        buildTracker.StartRun(buildRunId, totalTracks: 2);
        buildTracker.IncrementBuilt();
        buildTracker.IncrementFailed();

        var snapshot = ProgressSnapshotFactory.Create(
            manager, scanTracker, fpTracker, enrichmentTracker, buildTracker);

        Assert.Equal("Scanning, Enriching", snapshot.Status);
        Assert.False(snapshot.IsComplete);
        Assert.Equal(12, snapshot.Discovered);
        Assert.Equal(7, snapshot.Scanned); // processed (3) + skipped (4)
        Assert.Equal(1, snapshot.Fingerprinted);
        Assert.Equal(1, snapshot.Enriched);
        Assert.Equal(1, snapshot.Built);
        Assert.Equal(4, snapshot.Failed); // scan + fp + enrich + build

        Assert.Equal("Running", snapshot.Scan.Status);
        Assert.Equal("Idle", snapshot.Fingerprint.Status);
        Assert.Equal("Running", snapshot.Enrich.Status);
        Assert.Equal("Idle", snapshot.Build.Status);
    }

    [Fact]
    public void Create_WhenNoStepsRunning_ReturnsIdleAndComplete()
    {
        var manager = new JobManager();
        var scanTracker = new ScanProgressTracker();
        var fpTracker = new FingerprintProgressTracker();
        var enrichmentTracker = new EnrichmentProgressTracker();
        var buildTracker = new LibraryBuilderProgressTracker();

        manager.PauseStep(JobType.Build);

        var snapshot = ProgressSnapshotFactory.Create(
            manager, scanTracker, fpTracker, enrichmentTracker, buildTracker);

        Assert.Equal("Idle", snapshot.Status);
        Assert.True(snapshot.IsComplete);
        Assert.Equal("Paused", snapshot.Build.Status);
        Assert.True(snapshot.Build.IsPaused);
    }

    [Theory]
    [InlineData("scan", JobType.Scan)]
    [InlineData(" fingerprint ", JobType.Fingerprint)]
    [InlineData("ENRICH", JobType.Enrich)]
    [InlineData("Build", JobType.Build)]
    public void TryParseJobType_ValidInputs_ReturnsExpectedStep(string input, JobType expected)
    {
        var parsed = ProgressSnapshotFactory.TryParseJobType(input, out var type);

        Assert.True(parsed);
        Assert.Equal(expected, type);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("index")]
    [InlineData("lyrics")]
    public void TryParseJobType_InvalidInput_ReturnsFalse(string input)
    {
        var parsed = ProgressSnapshotFactory.TryParseJobType(input, out var type);

        Assert.False(parsed);
        Assert.Equal(JobType.None, type);
    }
}

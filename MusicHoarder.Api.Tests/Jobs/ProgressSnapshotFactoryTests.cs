using MusicHoarder.Api.Download;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Scanner;

namespace MusicHoarder.Api.Tests.Jobs;

public class ProgressSnapshotFactoryTests
{
    [Fact]
    public void Create_NoRunsStarted_ReturnsIdleCompleteAndZeroCounts()
    {
        var jobManager = new JobManager();
        var scanTracker = new ScanProgressTracker();
        var fingerprintTracker = new FingerprintProgressTracker();
        var enrichmentTracker = new EnrichmentProgressTracker();
        var buildTracker = new LibraryBuilderProgressTracker();

        var snapshot = ProgressSnapshotFactory.Create(
            jobManager,
            scanTracker,
            fingerprintTracker,
            enrichmentTracker,
            buildTracker,
            new DownloadProgressTracker());

        Assert.Equal("Idle", snapshot.Status);
        Assert.True(snapshot.IsComplete);
        Assert.Equal(0, snapshot.Discovered);
        Assert.Equal(0, snapshot.Scanned);
        Assert.Equal(0, snapshot.Fingerprinted);
        Assert.Equal(0, snapshot.Enriched);
        Assert.Equal(0, snapshot.Built);
        Assert.Equal(0, snapshot.Failed);
        Assert.Equal("Idle", snapshot.Scan.Status);
        Assert.Equal("Idle", snapshot.Fingerprint.Status);
        Assert.Equal("Idle", snapshot.Enrich.Status);
        Assert.Equal("Idle", snapshot.Build.Status);
    }

    [Fact]
    public void Create_NoRunningJobs_ReturnsIdleAndComplete()
    {
        var manager = new JobManager();
        var scanTracker = new ScanProgressTracker();
        var fingerprintTracker = new FingerprintProgressTracker();
        var enrichmentTracker = new EnrichmentProgressTracker();
        var buildTracker = new LibraryBuilderProgressTracker();

        var runId = Guid.NewGuid();
        scanTracker.Start(runId, totalFiles: 4);
        scanTracker.IncrementChanged();
        scanTracker.Complete(runId);

        var snapshot = ProgressSnapshotFactory.Create(
            manager,
            scanTracker,
            fingerprintTracker,
            enrichmentTracker,
            buildTracker,
            new DownloadProgressTracker());

        Assert.Equal("Idle", snapshot.Status);
        Assert.True(snapshot.IsComplete);
        Assert.Equal(4, snapshot.Discovered);
        Assert.Equal(1, snapshot.Scanned);
    }

    [Fact]
    public void Create_WhenStepPausedWhileIdle_ShowsPausedStepAndIdleOverallStatus()
    {
        var manager = new JobManager();
        var scanTracker = new ScanProgressTracker();
        var fpTracker = new FingerprintProgressTracker();
        var enrichmentTracker = new EnrichmentProgressTracker();
        var buildTracker = new LibraryBuilderProgressTracker();

        manager.PauseStep(JobType.Build);

        var snapshot = ProgressSnapshotFactory.Create(
            manager, scanTracker, fpTracker, enrichmentTracker, buildTracker, new DownloadProgressTracker());

        Assert.Equal("Idle", snapshot.Status);
        Assert.True(snapshot.IsComplete);
        Assert.Equal("Paused", snapshot.Build.Status);
        Assert.True(snapshot.Build.IsPaused);
    }

    [Fact]
    public void Create_MultipleRunningSteps_ReturnsCompositeRunningStatus()
    {
        var jobManager = new JobManager();
        Assert.True(jobManager.TryStartJob(JobType.Scan, out _, out _));
        Assert.True(jobManager.TryStartJob(JobType.Build, out _, out _));

        var snapshot = ProgressSnapshotFactory.Create(
            jobManager,
            new ScanProgressTracker(),
            new FingerprintProgressTracker(),
            new EnrichmentProgressTracker(),
            new LibraryBuilderProgressTracker(),
            new DownloadProgressTracker());

        Assert.Equal("Scanning, Building", snapshot.Status);
        Assert.False(snapshot.IsComplete);
        Assert.Equal("Running", snapshot.Scan.Status);
        Assert.Equal("Running", snapshot.Build.Status);
        Assert.Equal("Idle", snapshot.Fingerprint.Status);
        Assert.Equal("Idle", snapshot.Enrich.Status);
    }

    [Fact]
    public void Create_MultipleRunningSteps_BuildsCompositeStatusAndAggregatesCounters()
    {
        var manager = new JobManager();
        var scanTracker = new ScanProgressTracker();
        var fingerprintTracker = new FingerprintProgressTracker();
        var enrichmentTracker = new EnrichmentProgressTracker();
        var buildTracker = new LibraryBuilderProgressTracker();

        manager.TryStartJob(JobType.Scan, out var scanId, out _);
        manager.TryStartJob(JobType.Enrich, out var enrichId, out _);

        scanTracker.Start(scanId, totalFiles: 10);
        scanTracker.IncrementNew();
        scanTracker.IncrementFailed();
        scanTracker.IncrementSkipped();

        fingerprintTracker.StartRun(Guid.NewGuid(), totalTracks: 5);
        fingerprintTracker.IncrementFailed();

        enrichmentTracker.StartCycle(enrichId, totalTracks: 3);
        enrichmentTracker.IncrementEnriched();

        buildTracker.StartRun(Guid.NewGuid(), totalTracks: 2);
        buildTracker.IncrementBuilt();

        var snapshot = ProgressSnapshotFactory.Create(
            manager,
            scanTracker,
            fingerprintTracker,
            enrichmentTracker,
            buildTracker,
            new DownloadProgressTracker());

        Assert.Equal("Scanning, Enriching", snapshot.Status);
        Assert.False(snapshot.IsComplete);
        Assert.Equal(10, snapshot.Discovered);
        Assert.Equal(3, snapshot.Scanned);
        Assert.Equal(0, snapshot.Fingerprinted);
        Assert.Equal(1, snapshot.Enriched);
        Assert.Equal(1, snapshot.Built);
        Assert.Equal(2, snapshot.Failed);
        Assert.Equal("Running", snapshot.Scan.Status);
        Assert.Equal("Idle", snapshot.Fingerprint.Status);
        Assert.Equal("Running", snapshot.Enrich.Status);
        Assert.Equal("Idle", snapshot.Build.Status);
    }

    [Fact]
    public void Create_AggregatesProgressAndFailureCountersAcrossTrackers()
    {
        var jobManager = new JobManager();
        var scanTracker = new ScanProgressTracker();
        var fingerprintTracker = new FingerprintProgressTracker();
        var enrichmentTracker = new EnrichmentProgressTracker();
        var buildTracker = new LibraryBuilderProgressTracker();

        var scanId = Guid.NewGuid();
        scanTracker.Start(scanId, totalFiles: 10);
        scanTracker.IncrementNew();
        scanTracker.IncrementChanged();
        scanTracker.IncrementFailed();
        scanTracker.AddSkipped(3);

        var fpRunId = Guid.NewGuid();
        fingerprintTracker.StartRun(fpRunId, totalTracks: 5);
        fingerprintTracker.IncrementFingerprinted();
        fingerprintTracker.IncrementFingerprinted();
        fingerprintTracker.IncrementFailed();

        var enrichRunId = Guid.NewGuid();
        enrichmentTracker.StartCycle(enrichRunId, totalTracks: 4);
        enrichmentTracker.IncrementEnriched();
        enrichmentTracker.IncrementNeedsReview();
        enrichmentTracker.IncrementFailed();

        var buildRunId = Guid.NewGuid();
        buildTracker.StartRun(buildRunId, totalTracks: 6);
        buildTracker.IncrementBuilt();
        buildTracker.IncrementBuilt();
        buildTracker.IncrementFailed();
        buildTracker.IncrementFailed();

        var snapshot = ProgressSnapshotFactory.Create(
            jobManager,
            scanTracker,
            fingerprintTracker,
            enrichmentTracker,
            buildTracker,
            new DownloadProgressTracker());

        Assert.Equal(10, snapshot.Discovered);
        Assert.Equal(6, snapshot.Scanned); // processed (3) + skipped (3)
        Assert.Equal(2, snapshot.Fingerprinted);
        Assert.Equal(1, snapshot.Enriched);
        Assert.Equal(2, snapshot.Built);
        Assert.Equal(5, snapshot.Failed); // scan 1 + fp 1 + enrich 1 + build 2
    }

    [Theory]
    [InlineData("scan", JobType.Scan)]
    [InlineData("  SCAN ", JobType.Scan)]
    [InlineData("fingerprint", JobType.Fingerprint)]
    [InlineData(" EnRiCh ", JobType.Enrich)]
    [InlineData("BUILD", JobType.Build)]
    [InlineData("download", JobType.Download)]
    public void TryParseJobType_ValidInput_ReturnsExpectedType(string input, JobType expected)
    {
        var parsed = ProgressSnapshotFactory.TryParseJobType(input, out var jobType);

        Assert.True(parsed);
        Assert.Equal(expected, jobType);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("index")]
    [InlineData("scan_all")]
    [InlineData("unknown")]
    [InlineData("lyrics")]
    public void TryParseJobType_InvalidInput_ReturnsFalseAndNone(string input)
    {
        var parsed = ProgressSnapshotFactory.TryParseJobType(input, out var jobType);

        Assert.False(parsed);
        Assert.Equal(JobType.None, jobType);
    }
}

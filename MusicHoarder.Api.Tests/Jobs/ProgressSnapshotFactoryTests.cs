using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Scanner;

namespace MusicHoarder.Api.Tests.Jobs;

public class ProgressSnapshotFactoryTests
{
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
            buildTracker);

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
            buildTracker);

        Assert.Equal("Idle", snapshot.Status);
        Assert.True(snapshot.IsComplete);
        Assert.Equal(4, snapshot.Discovered);
        Assert.Equal(1, snapshot.Scanned);
    }

    [Theory]
    [InlineData("scan", true, JobType.Scan)]
    [InlineData("  fingerprint  ", true, JobType.Fingerprint)]
    [InlineData("ENRICH", true, JobType.Enrich)]
    [InlineData("Build", true, JobType.Build)]
    [InlineData("unknown", false, JobType.None)]
    [InlineData(" ", false, JobType.None)]
    public void TryParseJobType_HandlesCasingWhitespaceAndInvalidValues(string input, bool expected, JobType expectedType)
    {
        var parsed = ProgressSnapshotFactory.TryParseJobType(input, out var jobType);

        Assert.Equal(expected, parsed);
        Assert.Equal(expectedType, jobType);
    }
}

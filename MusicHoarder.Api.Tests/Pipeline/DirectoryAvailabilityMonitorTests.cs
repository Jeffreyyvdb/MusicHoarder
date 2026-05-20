using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Pipeline;

namespace MusicHoarder.Api.Tests.Pipeline;

public class DirectoryAvailabilityMonitorTests
{
    private static DirectoryAvailabilityMonitor CreateMonitor(
        JobManager jobManager, string sourceDir, string destDir)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = sourceDir,
            DestinationDirectory = destDir,
            DirectoryProbeTimeoutSeconds = 2,
        });

        return new DirectoryAvailabilityMonitor(
            jobManager, options, NullLogger<DirectoryAvailabilityMonitor>.Instance);
    }

    [Fact]
    public async Task ProbeNowAsync_ReflectsDirectoryExistence()
    {
        var source = Directory.CreateTempSubdirectory("mh-src-").FullName;
        var dest = Directory.CreateTempSubdirectory("mh-dst-").FullName;
        try
        {
            var monitor = CreateMonitor(new JobManager(), source, dest);

            var available = await monitor.ProbeNowAsync();
            Assert.True(available.SourceAvailable);
            Assert.True(available.DestinationAvailable);
            Assert.True(available.AllAvailable);

            Directory.Delete(source, recursive: true);

            var afterRemoval = await monitor.ProbeNowAsync();
            Assert.False(afterRemoval.SourceAvailable);
            Assert.True(afterRemoval.DestinationAvailable);
            Assert.False(afterRemoval.AllAvailable);
        }
        finally
        {
            if (Directory.Exists(source)) Directory.Delete(source, recursive: true);
            if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
        }
    }

    [Fact]
    public async Task ProbeNowAsync_WhenSourceBecomesAvailable_TriggersScan()
    {
        var dest = Directory.CreateTempSubdirectory("mh-dst-").FullName;
        var source = Path.Combine(Path.GetTempPath(), $"mh-src-{Guid.NewGuid():N}");
        try
        {
            var jobManager = new JobManager();
            var monitor = CreateMonitor(jobManager, source, dest);

            // First probe: source is missing → no scan should be triggered.
            var first = await monitor.ProbeNowAsync();
            Assert.False(first.SourceAvailable);
            Assert.Equal("Idle", jobManager.GetStepSnapshot(JobType.Scan).Status);

            // Source reappears (reconnected to the home network).
            Directory.CreateDirectory(source);
            var second = await monitor.ProbeNowAsync();

            Assert.True(second.SourceAvailable);
            Assert.Equal("Running", jobManager.GetStepSnapshot(JobType.Scan).Status);
            Assert.True(jobManager.ScanTriggers.TryRead(out _));
        }
        finally
        {
            if (Directory.Exists(source)) Directory.Delete(source, recursive: true);
            if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
        }
    }

    [Fact]
    public async Task ProbeNowAsync_WhenSourceAvailableOnFirstProbe_TriggersStartupScan()
    {
        var source = Directory.CreateTempSubdirectory("mh-src-").FullName;
        var dest = Directory.CreateTempSubdirectory("mh-dst-").FullName;
        try
        {
            var jobManager = new JobManager();
            var monitor = CreateMonitor(jobManager, source, dest);

            var snapshot = await monitor.ProbeNowAsync();

            Assert.True(snapshot.SourceAvailable);
            Assert.Equal("Running", jobManager.GetStepSnapshot(JobType.Scan).Status);
        }
        finally
        {
            if (Directory.Exists(source)) Directory.Delete(source, recursive: true);
            if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
        }
    }
}

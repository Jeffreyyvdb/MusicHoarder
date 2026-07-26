using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Pipeline;
using MusicHoarder.Api.Scanner;

namespace MusicHoarder.Api.Tests.Scanner;

/// <summary>
/// The scheduling decision only — <c>ExecuteAsync</c> is a plain delay loop around <c>Tick</c>, so the
/// tests drive <c>Tick</c> directly rather than waiting on a timer.
/// </summary>
public class AutoScanBackgroundServiceTests
{
    private static AutoScanBackgroundService CreateService(
        JobManager jobManager,
        bool sourceAvailable = true,
        int intervalMinutes = 15) =>
        new(jobManager,
            new StubAvailability(sourceAvailable),
            new StubOptionsMonitor(new MusicEnricherOptions
            {
                SourceDirectory = "/source",
                DestinationDirectory = "/dest",
                AutoScanIntervalMinutes = intervalMinutes,
            }),
            NullLogger<AutoScanBackgroundService>.Instance);

    [Fact]
    public void Tick_StartsScan_WhenSourceAvailableAndNothingRunning()
    {
        var jobManager = new JobManager();

        Assert.True(CreateService(jobManager).Tick());

        Assert.Equal("Running", jobManager.GetStepSnapshot(JobType.Scan).Status);
        Assert.True(jobManager.ScanTriggers.TryRead(out _));
    }

    [Fact]
    public void Tick_DoesNothing_WhenSourceUnavailable()
    {
        var jobManager = new JobManager();

        Assert.False(CreateService(jobManager, sourceAvailable: false).Tick());

        Assert.NotEqual("Running", jobManager.GetStepSnapshot(JobType.Scan).Status);
        Assert.False(jobManager.ScanTriggers.TryRead(out _));
    }

    [Fact]
    public void Tick_DoesNothing_WhenIntervalIsZero()
    {
        var jobManager = new JobManager();

        Assert.False(CreateService(jobManager, intervalMinutes: 0).Tick());

        Assert.False(jobManager.ScanTriggers.TryRead(out _));
    }

    [Fact]
    public void Tick_DoesNothing_WhenScanAlreadyRunning()
    {
        var jobManager = new JobManager();
        Assert.True(jobManager.TryStartJob(JobType.Scan, out _, out _));
        Assert.True(jobManager.ScanTriggers.TryRead(out _)); // drain the first trigger

        Assert.False(CreateService(jobManager).Tick());

        Assert.False(jobManager.ScanTriggers.TryRead(out _));
    }

    [Fact]
    public void Tick_RespectsPausedScanStep_AndLeavesItPaused()
    {
        // TryStartJob clears IsPaused because it models a user-initiated action, so an unguarded
        // periodic trigger would silently un-pause a step the user paused on purpose.
        var jobManager = new JobManager();
        jobManager.PauseStep(JobType.Scan);

        Assert.False(CreateService(jobManager).Tick());

        Assert.True(jobManager.IsStepPaused(JobType.Scan));
        Assert.False(jobManager.ScanTriggers.TryRead(out _));
    }

    [Fact]
    public void Tick_ResumesTriggering_AfterStepIsResumed()
    {
        var jobManager = new JobManager();
        jobManager.PauseStep(JobType.Scan);
        var service = CreateService(jobManager);
        Assert.False(service.Tick());

        jobManager.ResumeStep(JobType.Scan);

        Assert.True(service.Tick());
        Assert.True(jobManager.ScanTriggers.TryRead(out _));
    }

    private sealed class StubAvailability(bool sourceAvailable) : IDirectoryAvailability
    {
        public DirectoryAvailabilitySnapshot Current { get; } = new(
            SourceAvailable: sourceAvailable,
            DestinationAvailable: true,
            SourceDirectory: "/source",
            DestinationDirectory: "/dest",
            CheckedAtUtc: DateTime.UtcNow);

        public Task<DirectoryAvailabilitySnapshot> ProbeNowAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Current);
    }

    private sealed class StubOptionsMonitor(MusicEnricherOptions value) : IOptionsMonitor<MusicEnricherOptions>
    {
        public MusicEnricherOptions CurrentValue { get; } = value;
        public MusicEnricherOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<MusicEnricherOptions, string?> listener) => null;
    }
}

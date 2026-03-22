using MusicHoarder.Api.Jobs;

namespace MusicHoarder.Api.Tests.Jobs;

public class JobManagerTests
{
    [Fact]
    public void PauseStep_PreventsAutoJobs_UntilResumed()
    {
        var manager = new JobManager();

        manager.PauseStep(JobType.Scan);

        var startedWhilePaused = manager.TryRegisterAutoJob(JobType.Scan, Guid.NewGuid(), out var pausedToken);
        var pausedSnapshot = manager.GetStepSnapshot(JobType.Scan);

        Assert.False(startedWhilePaused);
        Assert.Equal(CancellationToken.None, pausedToken);
        Assert.Equal("Paused", pausedSnapshot.Status);
        Assert.True(pausedSnapshot.IsPaused);

        manager.ResumeStep(JobType.Scan);
        var startedAfterResume = manager.TryRegisterAutoJob(JobType.Scan, Guid.NewGuid(), out _);

        Assert.True(startedAfterResume);
    }

    [Fact]
    public void TryStartJob_ClearsPausedState_AndQueuesTrigger()
    {
        var manager = new JobManager();
        manager.PauseStep(JobType.Fingerprint);

        var started = manager.TryStartJob(JobType.Fingerprint, out var jobId, out var cancellationToken);
        var duplicateStart = manager.TryStartJob(JobType.Fingerprint, out _, out _);
        var triggerRead = manager.FingerprintTriggers.TryRead(out var queuedId);
        var snapshot = manager.GetStepSnapshot(JobType.Fingerprint);

        Assert.True(started);
        Assert.False(duplicateStart);
        Assert.NotEqual(Guid.Empty, jobId);
        Assert.True(triggerRead);
        Assert.Equal(jobId, queuedId);
        Assert.False(cancellationToken.IsCancellationRequested);
        Assert.False(manager.IsStepPaused(JobType.Fingerprint));
        Assert.Equal("Running", snapshot.Status);
        Assert.False(snapshot.IsPaused);
    }

    [Fact]
    public void Cancel_ReturnsFalse_WhenNothingRunning()
    {
        var manager = new JobManager();

        var cancelled = manager.Cancel();

        Assert.False(cancelled);
    }

    [Fact]
    public void Cancel_CancelsRunningStepTokens()
    {
        var manager = new JobManager();
        manager.TryStartJob(JobType.Scan, out _, out var scanToken);
        manager.TryStartJob(JobType.Build, out _, out var buildToken);

        var cancelled = manager.Cancel();

        Assert.True(cancelled);
        Assert.True(scanToken.IsCancellationRequested);
        Assert.True(buildToken.IsCancellationRequested);
    }
}

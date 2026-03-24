using MusicHoarder.Api.Jobs;

namespace MusicHoarder.Api.Tests.Jobs;

public class JobManagerTests
{
    [Fact]
    public void TryStartJob_SameStepAlreadyRunning_ReturnsFalse()
    {
        var manager = new JobManager();

        var started = manager.TryStartJob(JobType.Scan, out var firstJobId, out var firstToken);
        var startedAgain = manager.TryStartJob(JobType.Scan, out var secondJobId, out var secondToken);

        Assert.True(started);
        Assert.NotEqual(Guid.Empty, firstJobId);
        Assert.True(firstToken.CanBeCanceled);
        Assert.False(startedAgain);
        Assert.Equal(Guid.Empty, secondJobId);
        Assert.False(secondToken.CanBeCanceled);
        Assert.Equal("Running", manager.GetStepSnapshot(JobType.Scan).Status);
    }

    [Fact]
    public void TryStartJob_DifferentSteps_CanRunConcurrently()
    {
        var manager = new JobManager();

        var scanStarted = manager.TryStartJob(JobType.Scan, out _, out _);
        var enrichStarted = manager.TryStartJob(JobType.Enrich, out _, out _);

        Assert.True(scanStarted);
        Assert.True(enrichStarted);
        Assert.True(manager.IsAnyRunning());
        Assert.Equal("Running", manager.GetStepSnapshot(JobType.Scan).Status);
        Assert.Equal("Running", manager.GetStepSnapshot(JobType.Enrich).Status);
    }

    [Fact]
    public void PauseAndResume_WhenIdle_BlocksAndReenablesAutoJobs()
    {
        var manager = new JobManager();

        manager.PauseStep(JobType.Fingerprint);
        var pausedSnapshot = manager.GetStepSnapshot(JobType.Fingerprint);
        var autoWhilePaused = manager.TryRegisterAutoJob(JobType.Fingerprint, Guid.NewGuid(), out var pausedToken);

        manager.ResumeStep(JobType.Fingerprint);
        var resumedSnapshot = manager.GetStepSnapshot(JobType.Fingerprint);
        var autoAfterResume = manager.TryRegisterAutoJob(JobType.Fingerprint, Guid.NewGuid(), out var resumedToken);

        Assert.Equal("Paused", pausedSnapshot.Status);
        Assert.True(pausedSnapshot.IsPaused);
        Assert.False(autoWhilePaused);
        Assert.False(pausedToken.CanBeCanceled);

        Assert.Equal("Idle", resumedSnapshot.Status);
        Assert.False(resumedSnapshot.IsPaused);
        Assert.True(autoAfterResume);
        Assert.True(resumedToken.CanBeCanceled);
        Assert.Equal("Running", manager.GetStepSnapshot(JobType.Fingerprint).Status);
    }

    [Fact]
    public void PauseStep_WhenRunning_CancelsInFlightToken()
    {
        var manager = new JobManager();
        manager.TryStartJob(JobType.Build, out _, out var token);

        manager.PauseStep(JobType.Build);
        var snapshot = manager.GetStepSnapshot(JobType.Build);

        Assert.True(token.IsCancellationRequested);
        Assert.True(snapshot.IsPaused);
        Assert.Equal("Running", snapshot.Status);
    }

    [Fact]
    public void SignalComplete_WithMismatchedId_DoesNotMutateRunningStep()
    {
        var manager = new JobManager();
        manager.TryStartJob(JobType.Scan, out var jobId, out _);

        manager.SignalComplete(JobType.Scan, Guid.NewGuid());
        manager.SignalFailed(JobType.Scan, Guid.NewGuid());
        var stillRunning = manager.GetStepSnapshot(JobType.Scan);

        manager.SignalComplete(JobType.Scan, jobId);
        var completed = manager.GetStepSnapshot(JobType.Scan);

        Assert.Equal("Running", stillRunning.Status);
        Assert.Equal("Completed", completed.Status);
        Assert.False(manager.IsAnyRunning());
    }

    [Fact]
    public void SignalComplete_ByJobId_OnlyCompletesOwningStep()
    {
        var manager = new JobManager();
        manager.TryStartJob(JobType.Scan, out _, out _);
        manager.TryStartJob(JobType.Enrich, out var enrichJobId, out _);

        manager.SignalComplete(enrichJobId);

        Assert.Equal("Running", manager.GetStepSnapshot(JobType.Scan).Status);
        Assert.Equal("Completed", manager.GetStepSnapshot(JobType.Enrich).Status);
        Assert.True(manager.IsAnyRunning());
    }

    [Fact]
    public void Cancel_WhenNoStepsRunning_ReturnsFalse()
    {
        var manager = new JobManager();

        var cancelled = manager.Cancel();

        Assert.False(cancelled);
    }
}

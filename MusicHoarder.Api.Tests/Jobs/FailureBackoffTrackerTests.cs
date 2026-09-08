using MusicHoarder.Api.Jobs;

namespace MusicHoarder.Api.Tests.Jobs;

public class FailureBackoffTrackerTests
{
    private static readonly DateTime Now = new(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void UnknownId_IsNotBackingOff()
    {
        var sut = new FailureBackoffTracker();

        Assert.False(sut.IsBackingOff(42, Now));
        Assert.Equal(0, sut.Count);
    }

    [Fact]
    public void MarkFailed_BacksOffForExactlyTheWindow()
    {
        var sut = new FailureBackoffTracker();
        var before = DateTime.UtcNow;
        sut.MarkFailed(42, TimeSpan.FromMinutes(30));
        var after = DateTime.UtcNow;

        Assert.True(sut.IsBackingOff(42, before + TimeSpan.FromMinutes(29)));
        Assert.False(sut.IsBackingOff(42, after + TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void MarkFailed_WithZeroWindow_ExpiresImmediately()
    {
        var sut = new FailureBackoffTracker();
        sut.MarkFailed(42, TimeSpan.Zero);

        // The instant it was set is not inside the window (the window is exclusive at its end).
        Assert.False(sut.IsBackingOff(42, DateTime.UtcNow));
    }

    [Fact]
    public void MarkFailed_Again_ReplacesTheWindow()
    {
        var sut = new FailureBackoffTracker();
        sut.MarkFailed(42, TimeSpan.FromHours(2));
        sut.MarkFailed(42, TimeSpan.FromMinutes(1));

        Assert.False(sut.IsBackingOff(42, DateTime.UtcNow + TimeSpan.FromMinutes(2)));
        Assert.Equal(1, sut.Count);
    }

    [Fact]
    public void Clear_ForgetsTheBackoff()
    {
        var sut = new FailureBackoffTracker();
        sut.MarkFailed(42, TimeSpan.FromHours(1));

        sut.Clear(42);

        Assert.False(sut.IsBackingOff(42, DateTime.UtcNow));
        Assert.Equal(0, sut.Count);
    }

    [Fact]
    public void Clear_OfAnUnknownId_IsANoOp()
    {
        var sut = new FailureBackoffTracker();
        sut.MarkFailed(1, TimeSpan.FromHours(1));

        sut.Clear(2);

        Assert.True(sut.IsBackingOff(1, DateTime.UtcNow));
        Assert.Equal(1, sut.Count);
    }

    [Fact]
    public void Prune_DropsOnlyExpiredEntries()
    {
        var sut = new FailureBackoffTracker();
        sut.MarkFailed(1, TimeSpan.FromMinutes(1));
        sut.MarkFailed(2, TimeSpan.FromHours(1));

        sut.Prune(DateTime.UtcNow + TimeSpan.FromMinutes(5));

        Assert.Equal(1, sut.Count);
        Assert.False(sut.IsBackingOff(1, DateTime.UtcNow));
        Assert.True(sut.IsBackingOff(2, DateTime.UtcNow));
    }

    [Fact]
    public void Prune_TreatsAnEntryExpiringExactlyNow_AsExpired()
    {
        var sut = new FailureBackoffTracker();
        sut.MarkFailed(1, TimeSpan.Zero);

        sut.Prune(DateTime.UtcNow);

        Assert.Equal(0, sut.Count);
    }

    [Fact]
    public void IsBackingOff_IsIndependentPerId()
    {
        var sut = new FailureBackoffTracker();
        sut.MarkFailed(1, TimeSpan.FromHours(1));

        Assert.True(sut.IsBackingOff(1, DateTime.UtcNow));
        Assert.False(sut.IsBackingOff(2, DateTime.UtcNow));
    }
}

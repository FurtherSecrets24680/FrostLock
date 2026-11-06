using FrostLock.Services;

namespace FrostLock.Tests;

public class IdleMonitorTests
{
    private static readonly TimeSpan Threshold = TimeSpan.FromMinutes(10);

    [Fact]
    public void TriggersWhenIdleAndBaselineBothReachTheThreshold()
        => Assert.True(IdleMonitorService.ShouldTrigger(Threshold, Threshold, Threshold, sessionLocked: false));

    [Fact]
    public void DoesNotTriggerWhileTheUserIsActive()
        => Assert.False(IdleMonitorService.ShouldTrigger(
            TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(30), Threshold, sessionLocked: false));

    [Fact]
    public void StaleIdleTimeAfterStartOrResumeDoesNotTrigger()
        => Assert.False(IdleMonitorService.ShouldTrigger(
            TimeSpan.FromHours(5), TimeSpan.FromMinutes(1), Threshold, sessionLocked: false));

    [Fact]
    public void DoesNotTriggerWhileTheSessionIsLocked()
        => Assert.False(IdleMonitorService.ShouldTrigger(
            TimeSpan.FromHours(5), TimeSpan.FromHours(5), Threshold, sessionLocked: true));

    [Fact]
    public void DisabledThresholdNeverTriggers()
        => Assert.False(IdleMonitorService.ShouldTrigger(
            TimeSpan.FromHours(5), TimeSpan.FromHours(5), TimeSpan.Zero, sessionLocked: false));

    [Fact]
    public void ReportsPlausibleSystemIdleTime()
    {
        var idle = IdleMonitorService.CurrentIdleTime();

        Assert.True(idle >= TimeSpan.Zero);
        Assert.True(idle < TimeSpan.FromDays(50));
    }
}

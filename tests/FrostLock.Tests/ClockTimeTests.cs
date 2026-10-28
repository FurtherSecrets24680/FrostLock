using FrostLock.Services;

namespace FrostLock.Tests;

public class ClockTimeTests
{
    [Fact]
    public void ResolvesLaterTimeToday()
    {
        var now = new DateTime(2026, 10, 28, 10, 15, 30);

        Assert.Equal(new DateTime(2026, 10, 28, 15, 30, 0),
            ClockTime.Resolve(TimeSpan.FromHours(15.5), now));
    }

    [Fact]
    public void ResolvesPassedTimeTomorrow()
    {
        var now = new DateTime(2026, 10, 28, 15, 30, 0);

        Assert.Equal(new DateTime(2026, 10, 29, 15, 30, 0),
            ClockTime.Resolve(TimeSpan.FromHours(15.5), now));
    }

    [Fact]
    public void SecondsUntilRoundsUpAndStaysWithinOneDay()
    {
        var now = new DateTime(2026, 10, 28, 10, 0, 0, 500);

        Assert.Equal(1, ClockTime.SecondsUntil(TimeSpan.FromHours(10) + TimeSpan.FromSeconds(1), now));
        Assert.Equal(24 * 60 * 60, ClockTime.SecondsUntil(TimeSpan.FromHours(10), now));
    }
}

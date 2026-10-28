using FrostLock.Services;

namespace FrostLock.Tests;

public class PinThrottleTests
{
    [Fact]
    public void FourWrongAttemptsDoNotStartCooldown()
    {
        var throttle = new PinThrottle();

        for (var i = 0; i < PinThrottle.AttemptsPerRound - 1; i++)
            Assert.Equal(PinAttemptResult.Wrong, throttle.Try("wrong", _ => false));

        Assert.False(throttle.IsLockedOut);
        Assert.Equal(1, throttle.AttemptsRemaining);
        Assert.Equal(0, throttle.LockoutLevel);
    }

    [Fact]
    public void FifthWrongAttemptStartsThirtySecondCooldown()
    {
        var throttle = new PinThrottle();

        for (var i = 0; i < PinThrottle.AttemptsPerRound - 1; i++)
            throttle.Try("wrong", _ => false);

        Assert.Equal(PinAttemptResult.LockedOut, throttle.Try("wrong", _ => false));
        Assert.True(throttle.IsLockedOut);
        Assert.InRange(throttle.CooldownRemaining, TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(31));
        Assert.Equal(PinThrottle.AttemptsPerRound, throttle.AttemptsRemaining);
    }

    [Fact]
    public void CorrectSecretClearsFailuresAndLevel()
    {
        var throttle = new PinThrottle(failures: 3, lockoutLevel: 2);

        Assert.Equal(PinAttemptResult.Success, throttle.Try("right", value => value == "right"));

        Assert.False(throttle.IsLockedOut);
        Assert.Equal(0, throttle.Failures);
        Assert.Equal(0, throttle.LockoutLevel);
        Assert.Equal(PinThrottle.AttemptsPerRound, throttle.AttemptsRemaining);
    }

    [Fact]
    public void LockedOutRefusesAttemptsWithoutCallingVerifier()
    {
        var throttle = new PinThrottle(lockedUntilUtc: DateTime.UtcNow.AddMinutes(5));
        var called = false;

        var result = throttle.Try("anything", _ =>
        {
            called = true;
            return true;
        });

        Assert.Equal(PinAttemptResult.LockedOut, result);
        Assert.False(called);
    }

    [Fact]
    public void ExpiredLockoutAllowsAnotherAttempt()
    {
        var throttle = new PinThrottle(lockedUntilUtc: DateTime.UtcNow.AddSeconds(-1));

        Assert.False(throttle.IsLockedOut);
        Assert.Equal(PinAttemptResult.Wrong, throttle.Try("wrong", _ => false));
    }

    [Fact]
    public void CooldownEscalatesWithPersistedLevel()
    {
        var throttle = new PinThrottle(failures: 4, lockoutLevel: 2);

        Assert.Equal(PinAttemptResult.LockedOut, throttle.Try("wrong", _ => false));

        Assert.Equal(3, throttle.LockoutLevel);
        Assert.InRange(throttle.CooldownRemaining, TimeSpan.FromMinutes(4.9), TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void LevelStopsAtMaximum()
    {
        var throttle = new PinThrottle(failures: 4, lockoutLevel: PinThrottle.MaxLockoutLevel);

        Assert.Equal(PinAttemptResult.LockedOut, throttle.Try("wrong", _ => false));

        Assert.Equal(PinThrottle.MaxLockoutLevel, throttle.LockoutLevel);
        Assert.InRange(throttle.CooldownRemaining, TimeSpan.FromMinutes(59.9), TimeSpan.FromMinutes(60));
    }

    [Fact]
    public void ConstructorClampsPersistedValues()
    {
        var throttle = new PinThrottle(failures: 99, lockoutLevel: 99);

        Assert.Equal(PinThrottle.AttemptsPerRound - 1, throttle.Failures);
        Assert.Equal(PinThrottle.MaxLockoutLevel, throttle.LockoutLevel);
        Assert.Equal(1, throttle.AttemptsRemaining);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(3, 3)]
    [InlineData(99, 4)]
    public void CooldownForClampsTheLevel(int level, int expectedIndex)
    {
        Assert.Equal(
            TimeSpan.FromSeconds(new[] { 30, 60, 300, 900, 3600 }[expectedIndex]),
            PinThrottle.CooldownFor(level));
    }
}

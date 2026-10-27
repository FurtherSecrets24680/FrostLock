namespace FrostLock.Models;

/// <summary>
/// Result of a PIN or emergency-phrase attempt.
/// </summary>
public readonly record struct UnlockAttempt(
    bool Success,
    bool LockedOut,
    int AttemptsRemaining,
    TimeSpan Cooldown)
{
    public static UnlockAttempt Ok() => new(true, false, 0, TimeSpan.Zero);

    public static UnlockAttempt Denied(int attemptsRemaining) =>
        new(false, false, attemptsRemaining, TimeSpan.Zero);

    public static UnlockAttempt Cooling(TimeSpan cooldown) =>
        new(false, true, 0, cooldown);
}

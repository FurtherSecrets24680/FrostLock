namespace FrostLock.Services;

public enum PinAttemptResult
{
    Success,
    Wrong,
    LockedOut
}

/// <summary>
/// Slows down PIN guessing. After a handful of wrong entries the prompt refuses input for
/// a short cooldown, so a freeze cannot be brute-forced by holding the keyboard.
/// </summary>
public sealed class PinThrottle
{
    private const int MaxAttempts = 5;
    private static readonly TimeSpan Lockout = TimeSpan.FromSeconds(30);

    private int _failures;
    private DateTime _lockedUntilUtc = DateTime.MinValue;

    public TimeSpan CooldownRemaining
    {
        get
        {
            var left = _lockedUntilUtc - DateTime.UtcNow;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    public bool IsLockedOut => CooldownRemaining > TimeSpan.Zero;

    public PinAttemptResult Try(string pin, Func<string, bool> verify)
    {
        if (IsLockedOut) return PinAttemptResult.LockedOut;

        if (verify(pin))
        {
            _failures = 0;
            return PinAttemptResult.Success;
        }

        _failures++;
        if (_failures >= MaxAttempts)
        {
            _failures = 0;
            _lockedUntilUtc = DateTime.UtcNow + Lockout;
            return PinAttemptResult.LockedOut;
        }

        return PinAttemptResult.Wrong;
    }
}

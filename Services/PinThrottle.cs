namespace FrostLock.Services;

public enum PinAttemptResult
{
    Success,
    Wrong,
    LockedOut
}

/// <summary>
/// Applies escalating cooldowns after repeated incorrect secret entries.
/// </summary>
public sealed class PinThrottle
{
    private const int MaxAttempts = 5;

    /// <summary>
    /// Cooldowns for consecutive rounds of wrong entries.
    /// </summary>
    private static readonly TimeSpan[] Cooldowns =
    {
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(60)
    };

    public static int AttemptsPerRound => MaxAttempts;

    public static int MaxLockoutLevel => Cooldowns.Length - 1;

    public static TimeSpan MaxCooldown => Cooldowns[^1];

    /// <summary>Cooldown used after the given number of completed lockout rounds.</summary>
    public static TimeSpan CooldownFor(int level) =>
        Cooldowns[Math.Clamp(level, 0, Cooldowns.Length - 1)];

    private readonly Action? _changed;
    private int _failures;
    private int _level;
    private DateTime _lockedUntilUtc = DateTime.MinValue;

    /// <summary>
    /// Starts from persisted state so a restart or a reboot does not clear the cooldown or
    /// drop the escalation back to the shortest wait.
    /// </summary>
    public PinThrottle(int failures = 0, DateTime? lockedUntilUtc = null, Action? changed = null,
        int lockoutLevel = 0)
    {
        _failures = Math.Clamp(failures, 0, MaxAttempts - 1);
        _level = Math.Clamp(lockoutLevel, 0, Cooldowns.Length - 1);
        _lockedUntilUtc = lockedUntilUtc ?? DateTime.MinValue;
        _changed = changed;
    }

    public int Failures => _failures;

    /// <summary>How many lockout rounds have been completed. Escalates the next cooldown.</summary>
    public int LockoutLevel => _level;

    /// <summary>Entries left before the next cooldown starts.</summary>
    public int AttemptsRemaining => Math.Max(0, MaxAttempts - _failures);

    public DateTime? LockedUntilUtc => _lockedUntilUtc > DateTime.UtcNow ? _lockedUntilUtc : null;

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
            _level = 0;
            _lockedUntilUtc = DateTime.MinValue;
            _changed?.Invoke();
            return PinAttemptResult.Success;
        }

        _failures++;

        if (_failures >= MaxAttempts)
        {
            _failures = 0;
            _lockedUntilUtc = DateTime.UtcNow + CooldownFor(_level);
            _level = Math.Min(_level + 1, MaxLockoutLevel);
            _changed?.Invoke();
            return PinAttemptResult.LockedOut;
        }

        _changed?.Invoke();
        return PinAttemptResult.Wrong;
    }
}

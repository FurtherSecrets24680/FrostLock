namespace FrostLock.Services;

/// <summary>
/// Turns a local time of day into the next real deadline. A time that has already
/// passed means tomorrow, which is how alarm clocks read.
/// </summary>
public static class ClockTime
{
    public const int MaxSeconds = 24 * 60 * 60;

    public static DateTime Resolve(TimeSpan timeOfDay, DateTime now)
    {
        var target = now.Date + timeOfDay;
        return target <= now ? target.AddDays(1) : target;
    }

    public static int SecondsUntil(TimeSpan timeOfDay, DateTime now)
    {
        var seconds = (int)Math.Ceiling((Resolve(timeOfDay, now) - now).TotalSeconds);
        return Math.Clamp(seconds, 1, MaxSeconds);
    }
}

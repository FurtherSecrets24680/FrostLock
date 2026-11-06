using FrostLock.Interop;

namespace FrostLock.Services;

public enum HardReleaseReason
{
    None,
    Timer,
    SafetyCap,
    Watchdog
}

public sealed class HardBlocker : IDisposable
{
    private static readonly object ActiveLock = new();
    private static HardBlocker? _active;

    private volatile bool _release;
    private Thread? _thread;
    private ManualResetEventSlim? _done;
    private DateTime? _releaseAtUtc;
    private TimeSpan _watchdogAfter;
    private long _lastHeartbeatTicks;
    private volatile int _autoReason;

    public event Action<HardReleaseReason>? AutoReleased;

    public bool Succeeded { get; private set; }
    public bool Attempted { get; private set; }

    public bool Start(DateTime? releaseAtUtc, TimeSpan watchdogAfter)
    {
        _releaseAtUtc = releaseAtUtc;
        _watchdogAfter = watchdogAfter;
        _lastHeartbeatTicks = DateTime.UtcNow.Ticks;
        _autoReason = (int)HardReleaseReason.None;

        var ready = new ManualResetEventSlim(false);
        _done = new ManualResetEventSlim(false);
        bool result = false;

        _thread = new Thread(() =>
        {
            try
            {
                result = Native.BlockInput(true);
                Attempted = true;
                ready.Set();

                if (!result) return;

                lock (ActiveLock) _active = this;

                while (!_release)
                {
                    var now = DateTime.UtcNow;

                    if (_releaseAtUtc is { } deadline && now >= deadline)
                    {
                        _autoReason = (int)HardReleaseReason.Timer;
                        break;
                    }

                    if (_watchdogAfter > TimeSpan.Zero)
                    {
                        var heartbeat = new DateTime(Interlocked.Read(ref _lastHeartbeatTicks), DateTimeKind.Utc);
                        if (now - heartbeat > _watchdogAfter)
                        {
                            _autoReason = (int)HardReleaseReason.Watchdog;
                            break;
                        }
                    }

                    Thread.Sleep(50);
                }

                Native.BlockInput(false);
            }
            catch
            {
            }
            finally
            {
                ready.Set();
                lock (ActiveLock)
                {
                    if (ReferenceEquals(_active, this)) _active = null;
                }

                _done.Set();

                var reason = (HardReleaseReason)_autoReason;
                if (reason != HardReleaseReason.None)
                {
                    try { AutoReleased?.Invoke(reason); } catch { /* ignore */ }
                }
            }
        })
        {
            IsBackground = true,
            Name = "FrostLock.HardBlock"
        };

        _thread.Start();
        ready.Wait(2000);

        Succeeded = result;
        if (!result)
        {
            _release = true;
            return false;
        }

        return true;
    }

    /// <summary>Called from the UI timer. Stops the watchdog from treating a busy app as hung.</summary>
    public void Heartbeat()
        => Interlocked.Exchange(ref _lastHeartbeatTicks, DateTime.UtcNow.Ticks);

    public void Release() => _release = true;

    /// <summary>Last-resort release for crash handlers; the owning thread still does the unblock.</summary>
    public static void EmergencyReleaseAll()
    {
        HardBlocker? active;
        lock (ActiveLock) active = _active;
        active?.Release();
    }

    public void Dispose()
    {
        _release = true;

        try { _done?.Wait(1500); } catch { /* ignore */ }
    }
}

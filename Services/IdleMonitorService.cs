using System.Runtime.InteropServices;
using System.Windows.Threading;
using FrostLock.Interop;
using Microsoft.Win32;

namespace FrostLock.Services;

public sealed class IdleMonitorService : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MinimumThreshold = TimeSpan.FromMinutes(1);

    private readonly DispatcherTimer _timer = new() { Interval = PollInterval };
    private DateTime _baselineUtc = DateTime.UtcNow;
    private bool _started;
    private bool _sessionLocked;

    public IdleMonitorService()
    {
        _timer.Tick += (_, _) => Poll();
    }

    /// <summary>Raised on the UI thread once the idle threshold has been reached.</summary>
    public event Action? IdleDetected;

    public bool Enabled { get; private set; }

    public TimeSpan Threshold { get; private set; } = TimeSpan.FromMinutes(10);

    public void Start()
    {
        if (_started) return;
        _started = true;

        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _timer.Start();
    }

    /// <summary>Turns monitoring on or off and restarts the idle countdown on a change.</summary>
    public void Configure(bool enabled, TimeSpan threshold)
    {
        Threshold = threshold < MinimumThreshold ? MinimumThreshold : threshold;

        if (enabled == Enabled) return;

        Enabled = enabled;
        Reset();
    }

    /// <summary>Restarts the countdown, for example after a freeze or a session unlock.</summary>
    public void Reset() => _baselineUtc = DateTime.UtcNow;

    private void Poll()
    {
        if (!Enabled
            || !ShouldTrigger(CurrentIdleTime(), DateTime.UtcNow - _baselineUtc, Threshold, _sessionLocked))
            return;

        Reset();
        IdleDetected?.Invoke();
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.SessionLogoff:
                _sessionLocked = true;
                break;

            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.SessionLogon:
                _sessionLocked = false;
                Reset();
                break;
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) Reset();
    }

    internal static bool ShouldTrigger(TimeSpan idle, TimeSpan sinceBaseline, TimeSpan threshold, bool sessionLocked)
    {
        if (sessionLocked || threshold <= TimeSpan.Zero) return false;

        return idle >= threshold && sinceBaseline >= threshold;
    }

    /// <summary>Time since the last keyboard or mouse input in this session.</summary>
    internal static TimeSpan CurrentIdleTime()
    {
        var info = new Native.LASTINPUTINFO
        {
            cbSize = (uint)Marshal.SizeOf<Native.LASTINPUTINFO>()
        };

        if (!Native.GetLastInputInfo(ref info)) return TimeSpan.Zero;

        var now = unchecked((uint)Native.GetTickCount64());
        var elapsed = unchecked(now - info.dwTime);
        return TimeSpan.FromMilliseconds(elapsed);
    }

    public void Dispose()
    {
        _timer.Stop();

        if (!_started) return;

        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _started = false;
    }
}

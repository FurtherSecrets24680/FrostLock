using System.Security.Principal;
using System.Windows.Forms;
using System.Windows.Threading;
using FrostLock.Interop;
using FrostLock.Models;
using FrostLock.Views;

namespace FrostLock.Services;

public enum StartResult
{
    Started,
    NeedsElevation,
    PinNotSet,
    Failed,
    AlreadyActive
}

public sealed class FreezeStartedEventArgs : EventArgs
{
    public FreezeStartedEventArgs(FreezeMode mode, TimeSpan? planned)
    {
        Mode = mode;
        Planned = planned;
    }

    public FreezeMode Mode { get; }
    public TimeSpan? Planned { get; }
}

public sealed class FreezeEndedEventArgs : EventArgs
{
    public FreezeEndedEventArgs(FreezeEndReason reason, TimeSpan elapsed)
    {
        Reason = reason;
        Elapsed = elapsed;
    }

    public FreezeEndReason Reason { get; }
    public TimeSpan Elapsed { get; }
}

public sealed class FreezeController : IDisposable
{
    private readonly AppSettings _settings;
    private readonly Dispatcher _dispatcher;

    private HookBlocker? _hook;
    private HardBlocker? _hard;
    private readonly List<OverlayWindow> _overlays = new();
    private DispatcherTimer? _timer;

    private FreezePhase _phase = FreezePhase.Idle;
    private FreezeMode _mode = FreezeMode.Gentle;
    private TimeSpan? _planned;
    private TimeSpan _cap = TimeSpan.Zero;

    private DateTime _armStartedUtc;
    private DateTime _frozenAtUtc;
    private int _armSeconds;

    public FreezeController(AppSettings settings, Dispatcher dispatcher)
    {
        _settings = settings;
        _dispatcher = dispatcher;
    }

    public FreezePhase Phase => _phase;
    public bool IsActive => _phase != FreezePhase.Idle;
    public FreezeMode Mode => _mode;

    /// <summary>Session-only CLI overrides. Null means "use the saved setting".</summary>
    public int? DurationOverrideSeconds { get; set; }
    public int? ArmOverrideSeconds { get; set; }

    public event EventHandler<FreezeStartedEventArgs>? Started;
    public event EventHandler<FreezeEndedEventArgs>? Ended;
    public event EventHandler<OverlayState>? Tick;

    public static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public StartResult TryStart(FreezeMode mode)
    {
        if (_phase != FreezePhase.Idle)
            return StartResult.AlreadyActive;

        if (mode == FreezeMode.Hard && !IsAdministrator())
            return StartResult.NeedsElevation;

        if (mode == FreezeMode.Pin && !_settings.HasPin)
            return StartResult.PinNotSet;

        _mode = mode;

        int durationSeconds = DurationOverrideSeconds ?? _settings.DurationSeconds;
        int armSeconds = ArmOverrideSeconds ?? _settings.ArmSeconds;

        _planned = durationSeconds <= 0 ? null : TimeSpan.FromSeconds(durationSeconds);
        _cap = _settings.SafetyMaxSeconds > 0
            ? TimeSpan.FromSeconds(_settings.SafetyMaxSeconds)
            : TimeSpan.Zero;

        _armSeconds = Math.Max(0, armSeconds);
        _armStartedUtc = DateTime.UtcNow;

        // Mark the freeze as live *before* anything can fail, so a failure during Engage()
        // can always tear the overlays down through Release().
        _phase = FreezePhase.Arming;

        ShowOverlays();
        StartTimer();

        Started?.Invoke(this, new FreezeStartedEventArgs(_mode, _planned));

        if (_armSeconds <= 0)
            Engage();
        else
            PushTick();

        return StartResult.Started;
    }

    private void Engage()
    {
        if (_phase == FreezePhase.Frozen) return;

        if (_mode == FreezeMode.Hard)
        {
            _hard = new HardBlocker();
            if (!_hard.Start())
            {
                _hard.Dispose();
                _hard = null;
                Release(FreezeEndReason.Aborted);
                return;
            }
        }
        else
        {
            // PIN mode keeps the keyboard and mouse alive so the PIN can be typed, but it
            // always swallows Win / Alt+Tab / Alt+F4 / Ctrl+Esc, like Strict does, and it
            // offers no hotkey bypass: the PIN is the only way out.
            bool pinMode = _mode == FreezeMode.Pin;

            var binding = !pinMode && Hotkeys.TryParse(_settings.UnlockHotkey, out var parsed)
                ? parsed
                : HotkeyBinding.None;

            _hook = new HookBlocker
            {
                BlockSystemKeys = pinMode || _settings.BlockSystemKeys,
                BlockAllKeys = _mode == FreezeMode.Strict && _settings.BlockAllKeys,
                BlockMouse = _mode == FreezeMode.Strict && _settings.BlockMouse,
                UnlockBinding = binding
            };

            _hook.UnlockComboPressed +=
                () => _dispatcher.BeginInvoke(() => Release(FreezeEndReason.HotkeyUnlocked));

            if (!_hook.Start())
            {
                _hook.Dispose();
                _hook = null;
                Release(FreezeEndReason.Aborted);
                return;
            }
        }

        if (_settings.KeepDisplayAwake)
            Native.SetThreadExecutionState(Native.ES_CONTINUOUS | Native.ES_SYSTEM_REQUIRED | Native.ES_DISPLAY_REQUIRED);

        _frozenAtUtc = DateTime.UtcNow;
        _phase = FreezePhase.Frozen;

        _settings.FreezeCount++;
        _settings.LastFrozenUtc = DateTime.UtcNow;
        SettingsService.Save(_settings);

        if (_settings.PlaySound)
            Native.MessageBeep(Native.MB_ICONEXCLAMATION);

        PushTick();
    }

    public void Release(FreezeEndReason reason)
    {
        if (_phase == FreezePhase.Idle) return;

        var elapsed = DateTime.UtcNow - _frozenAtUtc;
        bool wasFrozen = _phase == FreezePhase.Frozen;

        _phase = FreezePhase.Idle;

        _timer?.Stop();
        _timer = null;

        if (_settings.KeepDisplayAwake)
            Native.SetThreadExecutionState(Native.ES_CONTINUOUS);

        try { _hook?.Dispose(); } catch { /* ignore */ }
        _hook = null;

        try { _hard?.Release(); _hard?.Dispose(); } catch { /* ignore */ }
        _hard = null;

        foreach (var overlay in _overlays)
        {
            try
            {
                overlay.AllowClose = true;
                overlay.Close();
            }
            catch
            {
                // ignore
            }
        }
        _overlays.Clear();

        if (wasFrozen && reason is not (FreezeEndReason.Shutdown or FreezeEndReason.Aborted))
        {
            _settings.TotalFrozenSeconds += (long)Math.Max(0, elapsed.TotalSeconds);
            SettingsService.Save(_settings);
        }

        if (wasFrozen && _settings.PlaySound)
            Native.MessageBeep(Native.MB_ICONASTERISK);

        Ended?.Invoke(this, new FreezeEndedEventArgs(reason, elapsed));
    }

    private void StartTimer()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(80) };
        _timer.Tick += (_, _) => OnTimerTick();
        _timer.Start();
    }

    private void OnTimerTick()
    {
        if (_phase == FreezePhase.Arming)
        {
            var elapsed = (DateTime.UtcNow - _armStartedUtc).TotalSeconds;
            if (elapsed >= _armSeconds)
            {
                Engage();
                return;
            }
            PushTick();
            return;
        }

        if (_phase == FreezePhase.Frozen)
        {
            var elapsed = DateTime.UtcNow - _frozenAtUtc;

            if (_planned is { } planned && elapsed >= planned)
            {
                Release(FreezeEndReason.TimerExpired);
                return;
            }

            if (_cap > TimeSpan.Zero && elapsed >= _cap)
            {
                Release(FreezeEndReason.SafetyCap);
                return;
            }

            PushTick();
        }
    }

    private void PushTick()
    {
        if (_phase == FreezePhase.Idle) return;

        var now = DateTime.UtcNow;

        TimeSpan? remaining;
        double progress;
        string hint;
        bool mouseBlocked;
        bool interactive;

        if (_phase == FreezePhase.Arming)
        {
            var left = Math.Max(0, _armSeconds - (now - _armStartedUtc).TotalSeconds);
            remaining = TimeSpan.FromSeconds(left);
            progress = _armSeconds <= 0 ? 1.0 : Math.Clamp(left / _armSeconds, 0, 1);
            hint = "Starting soon. Click Cancel to abort.";
            // Escape is still deliverable during arming.
            mouseBlocked = false;
            interactive = true;
        }
        else
        {
            var elapsed = now - _frozenAtUtc;
            mouseBlocked = _hook?.MouseBlocked ?? _mode == FreezeMode.Hard;

            if (_planned is { } planned)
            {
                TimeSpan? left = planned - elapsed;
                if (left is { } l && l < TimeSpan.Zero) left = TimeSpan.Zero;
                remaining = left;
                progress = Math.Clamp(1.0 - elapsed.TotalSeconds / planned.TotalSeconds, 0, 1);
            }
            else
            {
                // An "until I unlock" freeze is truly indefinite: no countdown is shown, even
                // when the optional safety ceiling is enabled (that ceiling is a silent backstop).
                remaining = null;
                progress = 1.0;
            }

            hint = BuildUnlockHint(mouseBlocked);
            interactive = !mouseBlocked;
        }

        var state = new OverlayState
        {
            Phase = _phase,
            Mode = _mode,
            Remaining = remaining,
            Progress = progress,
            MouseBlocked = mouseBlocked,
            ShowCountdown = _settings.ShowCountdownOnOverlay,
            UnlockHint = hint,
            ModeLabel = ModeLabel(_mode),
            Interactive = interactive,
            RequiresPin = _mode == FreezeMode.Pin
        };

        foreach (var overlay in _overlays)
            overlay.UpdateState(state);

        Tick?.Invoke(this, state);
    }

    /// <summary>
    /// Hints shown on the lock screen. Deliberately never prints the unlock combination or
    /// the Escape-hold trick: anyone looking at the screen should not learn how to get out.
    /// </summary>
    private string BuildUnlockHint(bool mouseBlocked)
    {
        if (_mode == FreezeMode.Hard)
            return "Press Ctrl+Alt+Del to release the OS freeze, then click Unlock. A timer will also release it.";

        if (_mode == FreezeMode.Pin)
            return "Enter your PIN to unlock.";

        if (!mouseBlocked)
            return "Click Unlock when you are ready to continue.";

        return "Use your unlock shortcut to release this lock.";
    }

    private static string ModeLabel(FreezeMode mode) => mode switch
    {
        FreezeMode.Gentle => "Gentle lock",
        FreezeMode.Strict => "Strict input block",
        FreezeMode.Hard => "Hard OS freeze",
        FreezeMode.Pin => "PIN lock",
        _ => "Freeze"
    };

    private void ShowOverlays()
    {
        var screens = Screen.AllScreens
            .OrderByDescending(s => s.Primary)
            .ThenBy(s => s.Bounds.X)
            .ToList();

        bool pinMode = _mode == FreezeMode.Pin;
        Func<string, bool>? verifyPin = pinMode ? _settings.VerifyPin : null;

        foreach (var screen in screens)
        {
            var overlay = new OverlayWindow(
                screen,
                onUnlock: () => Release(FreezeEndReason.UserUnlocked),
                verifyPin: verifyPin,
                closeRequestsUnlock: !pinMode);

            overlay.Show();
            overlay.PositionToScreen();
            _overlays.Add(overlay);
        }

        if (_overlays.Count > 0)
        {
            _overlays[0].Activate();
            _overlays[0].Focus();
        }
    }

    public void Dispose() => Release(FreezeEndReason.Shutdown);
}
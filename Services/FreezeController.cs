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
    private bool _allowUnlockHotkey = true;
    private TimeSpan? _planned;
    private TimeSpan _cap = TimeSpan.Zero;

    private DateTime _armStartedUtc;
    private DateTime _frozenAtUtc;
    private int _armSeconds;
    private PinThrottle? _pinThrottle;
    private PinThrottle? _panicThrottle;

    public FreezeController(AppSettings settings, Dispatcher dispatcher)
    {
        _settings = settings;
        _dispatcher = dispatcher;
    }

    public FreezePhase Phase => _phase;
    public bool IsActive => _phase != FreezePhase.Idle;
    public FreezeMode Mode => _mode;

    /// <summary>True while a PIN freeze is active, so release must come from the PIN prompt.</summary>
    public bool RequiresPin => IsActive && _mode == FreezeMode.Pin;

    /// <summary>Session-only CLI overrides. Null means "use the saved setting".</summary>
    public int? DurationOverrideSeconds { get; set; }
    public int? ArmOverrideSeconds { get; set; }
    public bool? PlaySoundOverride { get; set; }

    private bool PlaySound => PlaySoundOverride ?? _settings.PlaySound;

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

    public StartResult TryStart(FreezeMode mode, bool allowUnlockHotkey = true)
    {
        if (_phase != FreezePhase.Idle)
            return StartResult.AlreadyActive;

        if (mode == FreezeMode.Hard && !IsAdministrator())
            return StartResult.NeedsElevation;

        if (mode == FreezeMode.Pin && !_settings.HasPin)
            return StartResult.PinNotSet;

        _mode = mode;
        _allowUnlockHotkey = allowUnlockHotkey;

        // A fresh throttle per freeze, seeded from disk so a restart or reboot does not
        // clear the cooldown or the escalation.
        _pinThrottle = mode == FreezeMode.Pin
            ? new PinThrottle(_settings.PinFailures, _settings.PinLockedUntilUtc,
                PersistPinThrottle, _settings.PinLockoutLevel)
            : null;

        _panicThrottle = mode == FreezeMode.Pin && _settings.HasPanicPhrase
            ? new PinThrottle(_settings.PanicFailures, _settings.PanicLockedUntilUtc,
                PersistPanicThrottle, _settings.PanicLockoutLevel)
            : null;

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

        _frozenAtUtc = DateTime.UtcNow;

        if (_mode == FreezeMode.Hard)
        {
            // The blocker owns its own deadline and a watchdog. If the UI thread stalls,
            // input is still released instead of waiting for Ctrl+Alt+Del.
            DateTime? releaseAt = null;
            if (_planned is { } planned)
                releaseAt = _frozenAtUtc + planned;
            if (_cap > TimeSpan.Zero)
            {
                var capAt = _frozenAtUtc + _cap;
                if (releaseAt is null || capAt < releaseAt) releaseAt = capAt;
            }

            _hard = new HardBlocker();
            _hard.AutoReleased += OnHardAutoReleased;

            if (!_hard.Start(releaseAt, TimeSpan.FromSeconds(5)))
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

            var binding = !pinMode && _allowUnlockHotkey && Hotkeys.TryParse(_settings.UnlockHotkey, out var parsed)
                ? parsed
                : HotkeyBinding.None;

            _hook = new HookBlocker
            {
                BlockSystemKeys = pinMode || _settings.BlockSystemKeys,
                BlockAllKeys = _mode == FreezeMode.Strict && _settings.BlockAllKeys,
                BlockMouse = _mode == FreezeMode.Strict && _settings.BlockMouse,
                UnlockBinding = binding,
                UnlockHoldMs = _settings.UnlockHoldMs
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

        _phase = FreezePhase.Frozen;

        if (PlaySound)
            Native.MessageBeep(Native.MB_ICONEXCLAMATION);

        PushTick();
    }

    private void OnHardAutoReleased(HardReleaseReason reason)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (_phase == FreezePhase.Idle) return;

            Release(reason switch
            {
                HardReleaseReason.Timer => FreezeEndReason.TimerExpired,
                HardReleaseReason.Watchdog => FreezeEndReason.Watchdog,
                _ => FreezeEndReason.SafetyCap
            });
        });
    }

    /// <summary>
    /// Release requested from outside the lock screen (tray, hotkey, CLI, main window).
    /// A PIN freeze deliberately refuses, so those paths cannot bypass the PIN.
    /// </summary>
    public bool RequestRelease(FreezeEndReason reason)
    {
        if (RequiresPin) return false;

        Release(reason);
        return true;
    }

    /// <summary>Best-effort release for crash handlers. Safe to call from any thread.</summary>
    public void EmergencyRelease()
    {
        try { _hook?.Dispose(); } catch { /* ignore */ }
        _hook = null;

        try { _hard?.Release(); } catch { /* ignore */ }
        HardBlocker.EmergencyReleaseAll();
    }

    public void Release(FreezeEndReason reason)
    {
        if (_phase == FreezePhase.Idle) return;

        bool wasFrozen = _phase == FreezePhase.Frozen;
        var elapsed = wasFrozen ? DateTime.UtcNow - _frozenAtUtc : TimeSpan.Zero;

        _phase = FreezePhase.Idle;

        _timer?.Stop();
        _timer = null;

        if (_settings.KeepDisplayAwake)
            Native.SetThreadExecutionState(Native.ES_CONTINUOUS);

        try { _hook?.Dispose(); } catch { /* ignore */ }
        _hook = null;

        try { _hard?.Release(); _hard?.Dispose(); } catch { /* ignore */ }
        _hard = null;
        _pinThrottle = null;
        _panicThrottle = null;

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
            _settings.FreezeCount++;
            _settings.LastFrozenUtc = _frozenAtUtc;
            _settings.TotalFrozenSeconds += (long)Math.Max(0, elapsed.TotalSeconds);
            SettingsService.Save(_settings);
        }

        if (wasFrozen && PlaySound)
            Native.MessageBeep(Native.MB_ICONASTERISK);

        Ended?.Invoke(this, new FreezeEndedEventArgs(reason, elapsed));

        if (wasFrozen && _settings.LockWindowsOnRelease
            && reason is not (FreezeEndReason.Shutdown or FreezeEndReason.Aborted))
        {
            try { Native.LockWorkStation(); } catch { /* ignore */ }
        }
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

        _hard?.Heartbeat();

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
            hint = "Starting soon. Press Esc or click Cancel to abort.";
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
            Message = _settings.LockScreenMessage,
            ShowClock = _settings.ShowClockOnOverlay,
            UnlockAt = _phase == FreezePhase.Frozen && _planned is not null
                ? (_frozenAtUtc + _planned.Value).ToLocalTime()
                : null,
            PinCooldownRemaining = _pinThrottle?.CooldownRemaining ?? TimeSpan.Zero,
            PinAttemptsRemaining = _pinThrottle?.AttemptsRemaining ?? 0,
            PanicCooldownRemaining = _panicThrottle?.CooldownRemaining ?? TimeSpan.Zero,
            PanicAttemptsRemaining = _panicThrottle?.AttemptsRemaining ?? 0,
            PanicAvailable = _panicThrottle is not null,
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
    /// Hints shown on the lock screen without exposing the unlock combination.
    /// </summary>
    private string BuildUnlockHint(bool mouseBlocked)
    {
        if (_mode == FreezeMode.Hard)
            return "Input is held by the operating system.";

        if (_mode == FreezeMode.Pin)
            return "Enter your PIN to unlock.";

        var shortcutHint = UnlockShortcutHint();
        if (!mouseBlocked)
            return shortcutHint is null
                ? "Click Unlock when you are ready to continue."
                : $"Click Unlock when you are ready to continue, or use your unlock shortcut.";

        return shortcutHint ?? "Use your unlock shortcut to release this lock.";
    }

    /// <summary>Generic shortcut hint when a hold-to-unlock delay is configured.</summary>
    private string? UnlockShortcutHint()
    {
        if (_settings.UnlockHoldMs <= 0) return null;
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

    /// <summary>PIN verification with escalating lockouts after repeated wrong entries.</summary>
    private UnlockAttempt VerifyPinThrottled(string pin) =>
        Attempt(_pinThrottle, _settings.VerifyPin, pin);

    /// <summary>Emergency-phrase verification with an independent throttle.</summary>
    private UnlockAttempt VerifyPanicThrottled(string phrase) =>
        Attempt(_panicThrottle, _settings.VerifyPanicPhrase, phrase);

    private static UnlockAttempt Attempt(PinThrottle? throttle, Func<string, bool> verify, string secret)
    {
        if (throttle is null)
            return verify(secret) ? UnlockAttempt.Ok() : UnlockAttempt.Denied(0);

        return throttle.Try(secret, verify) switch
        {
            PinAttemptResult.Success => UnlockAttempt.Ok(),
            PinAttemptResult.LockedOut => UnlockAttempt.Cooling(throttle.CooldownRemaining),
            _ => UnlockAttempt.Denied(throttle.AttemptsRemaining)
        };
    }

    private void PersistPinThrottle()
    {
        if (_pinThrottle is null) return;

        _settings.PinFailures = _pinThrottle.Failures;
        _settings.PinLockedUntilUtc = _pinThrottle.LockedUntilUtc;
        _settings.PinLockoutLevel = _pinThrottle.LockoutLevel;
        SettingsService.Save(_settings);
    }

    private void PersistPanicThrottle()
    {
        if (_panicThrottle is null) return;

        _settings.PanicFailures = _panicThrottle.Failures;
        _settings.PanicLockedUntilUtc = _panicThrottle.LockedUntilUtc;
        _settings.PanicLockoutLevel = _panicThrottle.LockoutLevel;
        SettingsService.Save(_settings);
    }

    private void ShowOverlays()
    {
        bool pinMode = _mode == FreezeMode.Pin;

        // PIN mode always needs a lock screen: it is the only surface where the PIN can be
        // typed. "No lock screen" therefore falls back to a solid one for that mode.
        var style = _settings.LockScreenStyle;
        if (pinMode && style == LockScreenStyle.None)
            style = LockScreenStyle.Solid;

        // With no cover, the hooks alone do the work and nothing is drawn.
        if (style == LockScreenStyle.None) return;

        var screens = Screen.AllScreens
            .Where(s => _settings.LockScreenOnAllDisplays || s.Primary)
            .OrderByDescending(s => s.Primary)
            .ThenBy(s => s.Bounds.X)
            .ToList();

        Func<string, UnlockAttempt>? verifyPin = pinMode ? VerifyPinThrottled : null;
        Func<string, UnlockAttempt>? verifyPhrase = pinMode && _settings.HasPanicPhrase
            ? VerifyPanicThrottled
            : null;

        foreach (var screen in screens)
        {
            var overlay = new OverlayWindow(
                screen,
                onUnlock: () => Release(FreezeEndReason.UserUnlocked),
                verifyPin: verifyPin,
                verifyPhrase: verifyPhrase,
                closeRequestsUnlock: !pinMode,
                style: style,
                backgroundPath: _settings.LockScreenBackgroundPath);

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

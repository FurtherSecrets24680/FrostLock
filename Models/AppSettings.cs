using System.Text.Json.Serialization;
using FrostLock.Services;

namespace FrostLock.Models;

/// <summary>How aggressively input is taken away.</summary>
public enum FreezeMode
{
    /// <summary>Full-screen overlay plus system-key suppression. Mouse stays editable on the overlay.</summary>
    Gentle,

    /// <summary>Overlay plus a global low-level input block. No administrator rights needed.</summary>
    Strict,

    /// <summary>Win32 BlockInput freeze at the OS level. Requires administrator rights.</summary>
    Hard,

    /// <summary>
    /// Soft lock: input stays live, system keys are swallowed, and the lock screen is released
    /// only by the PIN. No timer is required, and no unlock hotkey is honoured.
    /// </summary>
    Pin,

    /// <summary>Watch a selected removable drive: insertion freezes; removal releases.</summary>
    UsbKey
}

public enum FreezeEndReason
{
    UserUnlocked,
    HotkeyUnlocked,
    TimerExpired,
    SafetyCap,
    Watchdog,
    Aborted,
    Shutdown
}

/// <summary>How the lock screen covers the desktop while a freeze is active.</summary>
public enum LockScreenStyle
{
    /// <summary>An opaque, full-screen cover on every chosen display.</summary>
    Solid,

    /// <summary>A see-through dark veil: the desktop stays visible but dimmed.</summary>
    Dim,

    /// <summary>No cover at all. Input is blocked, but nothing is drawn.</summary>
    None
}

/// <summary>Light or dark appearance. System follows Windows.</summary>
public enum AppTheme
{
    System,
    Light,
    Dark
}

/// <summary>User preferences, persisted as JSON under %AppData%\FrostLock\settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>Bumped when a stored value changes meaning, so old files can be migrated.</summary>
    public int SettingsVersion { get; set; } = 5;

    public FreezeMode Mode { get; set; } = FreezeMode.Gentle;

    /// <summary>Freeze length in seconds. 0 means "until I unlock".</summary>
    public int DurationSeconds { get; set; } = 60;

    /// <summary>Grace period before input is actually taken away, so the action can be aborted.</summary>
    public int ArmSeconds { get; set; } = 3;

    /// <summary>Strict mode: swallow every keystroke, not just system keys.</summary>
    public bool BlockAllKeys { get; set; } = true;

    /// <summary>Strict mode: swallow mouse input globally.</summary>
    public bool BlockMouse { get; set; } = true;

    /// <summary>Swallow Win / Alt+Tab / Alt+F4 / Ctrl+Esc style escapes. Recommended.</summary>
    public bool BlockSystemKeys { get; set; } = true;

    public bool ShowCountdownOnOverlay { get; set; } = true;

    /// <summary>Solid cover, see-through dim, or no lock screen at all.</summary>
    public LockScreenStyle LockScreenStyle { get; set; } = LockScreenStyle.Solid;

    /// <summary>True: a lock screen on every display. False: the primary display only.</summary>
    public bool LockScreenOnAllDisplays { get; set; } = true;

    /// <summary>Optional line shown on the lock screen, under the hint. Empty hides it.</summary>
    public string LockScreenMessage { get; set; } = string.Empty;

    /// <summary>Optional full-screen image behind the lock screen text. Empty means a plain cover.</summary>
    public string LockScreenBackgroundPath { get; set; } = string.Empty;

    /// <summary>Show a wall clock and, for timed freezes, the projected unlock time.</summary>
    public bool ShowClockOnOverlay { get; set; } = true;

    /// <summary>Light, dark, or follow the Windows theme.</summary>
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>Quietly look for a newer release when the app starts.</summary>
    public bool CheckForUpdatesOnStartup { get; set; } = true;

    public string UnlockHotkey { get; set; } = "Ctrl+Alt+U";
    public string StartHotkey { get; set; } = "Ctrl+Alt+L";

    /// <summary>How long the unlock shortcut must be held, in milliseconds. 0 releases instantly.</summary>
    public int UnlockHoldMs { get; set; } = 1200;

    /// <summary>
    /// Absolute ceiling in seconds even for an "until I unlock" freeze. 0 (the default)
    /// disables the cap, so an indefinite freeze really is indefinite.
    /// </summary>
    public int SafetyMaxSeconds { get; set; }

    /// <summary>Base64 PBKDF2 salt for <see cref="PinHash"/>. Null when no PIN is configured.</summary>
    public string? PinSalt { get; set; }

    /// <summary>Base64 PBKDF2-SHA256 hash of the PIN. Null when no PIN is configured.</summary>
    public string? PinHash { get; set; }

    // Lockout state persists across restarts.
    public int PinFailures { get; set; }
    public DateTime? PinLockedUntilUtc { get; set; }
    public int PinLockoutLevel { get; set; }

    /// <summary>
    /// Optional emergency phrase for the PIN lock screen.
    /// </summary>
    public string? PanicSalt { get; set; }
    public string? PanicHash { get; set; }
    public int PanicFailures { get; set; }
    public DateTime? PanicLockedUntilUtc { get; set; }
    public int PanicLockoutLevel { get; set; }

    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }

    /// <summary>Keep the display awake while frozen.</summary>
    public bool KeepDisplayAwake { get; set; }
    public bool PlaySound { get; set; } = true;

    /// <summary>Lock the Windows session when a freeze ends. Off by default.</summary>
    public bool LockWindowsOnRelease { get; set; }

    // Lightweight stats, purely informational.
    public int FreezeCount { get; set; }
    public long TotalFrozenSeconds { get; set; }
    public DateTime? LastFrozenUtc { get; set; }

    [JsonIgnore]
    public bool IsIndefinite => DurationSeconds <= 0;

    [JsonIgnore]
    public bool HasPin => !string.IsNullOrEmpty(PinSalt) && !string.IsNullOrEmpty(PinHash);

    [JsonIgnore]
    public bool HasPanicPhrase =>
        !string.IsNullOrEmpty(PanicSalt) && !string.IsNullOrEmpty(PanicHash);

    /// <summary>Stores a new PIN. Only the salted hash is ever written to disk.</summary>
    public void SetPin(string pin)
    {
        var (salt, hash) = PinService.Create(pin);
        PinSalt = salt;
        PinHash = hash;

        ResetPinThrottle();
    }

    public void ClearPin()
    {
        PinSalt = null;
        PinHash = null;
        ResetPinThrottle();
    }

    private void ResetPinThrottle()
    {
        PinFailures = 0;
        PinLockedUntilUtc = null;
        PinLockoutLevel = 0;
    }

    /// <summary>Stores a new emergency phrase. Hashed and wrapped exactly like the PIN.</summary>
    public void SetPanicPhrase(string phrase)
    {
        var (salt, hash) = PinService.Create(phrase);
        PanicSalt = salt;
        PanicHash = hash;

        PanicFailures = 0;
        PanicLockedUntilUtc = null;
        PanicLockoutLevel = 0;
    }

    public void ClearPanicPhrase()
    {
        PanicSalt = null;
        PanicHash = null;
        PanicFailures = 0;
        PanicLockedUntilUtc = null;
        PanicLockoutLevel = 0;
    }

    public bool VerifyPin(string pin) => HasPin && PinService.Verify(pin, PinSalt, PinHash);

    public bool VerifyPanicPhrase(string phrase) =>
        HasPanicPhrase && PinService.Verify(phrase, PanicSalt, PanicHash);

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    /// <summary>Copies editable settings; copies statistics only when requested.</summary>
    public void CopyFrom(AppSettings other, bool includeStats = false)
    {
        Mode = other.Mode;
        DurationSeconds = other.DurationSeconds;
        ArmSeconds = other.ArmSeconds;
        BlockAllKeys = other.BlockAllKeys;
        BlockMouse = other.BlockMouse;
        BlockSystemKeys = other.BlockSystemKeys;
        ShowCountdownOnOverlay = other.ShowCountdownOnOverlay;
        LockScreenStyle = other.LockScreenStyle;
        LockScreenOnAllDisplays = other.LockScreenOnAllDisplays;
        LockScreenMessage = other.LockScreenMessage;
        LockScreenBackgroundPath = other.LockScreenBackgroundPath;
        ShowClockOnOverlay = other.ShowClockOnOverlay;
        Theme = other.Theme;
        CheckForUpdatesOnStartup = other.CheckForUpdatesOnStartup;
        UnlockHotkey = other.UnlockHotkey;
        StartHotkey = other.StartHotkey;
        UnlockHoldMs = other.UnlockHoldMs;
        SafetyMaxSeconds = other.SafetyMaxSeconds;
        MinimizeToTray = other.MinimizeToTray;
        CloseToTray = other.CloseToTray;
        StartWithWindows = other.StartWithWindows;
        StartMinimized = other.StartMinimized;
        KeepDisplayAwake = other.KeepDisplayAwake;
        PlaySound = other.PlaySound;
        LockWindowsOnRelease = other.LockWindowsOnRelease;
        PinSalt = other.PinSalt;
        PinHash = other.PinHash;
        PinFailures = other.PinFailures;
        PinLockedUntilUtc = other.PinLockedUntilUtc;
        PinLockoutLevel = other.PinLockoutLevel;
        PanicSalt = other.PanicSalt;
        PanicHash = other.PanicHash;
        PanicFailures = other.PanicFailures;
        PanicLockedUntilUtc = other.PanicLockedUntilUtc;
        PanicLockoutLevel = other.PanicLockoutLevel;

        if (!includeStats) return;

        FreezeCount = other.FreezeCount;
        TotalFrozenSeconds = other.TotalFrozenSeconds;
        LastFrozenUtc = other.LastFrozenUtc;
    }
}

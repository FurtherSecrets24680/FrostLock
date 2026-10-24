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
    Pin
}

public enum FreezeEndReason
{
    UserUnlocked,
    HotkeyUnlocked,
    TimerExpired,
    SafetyCap,
    Aborted,
    Shutdown
}

/// <summary>User preferences, persisted as JSON under %AppData%\FrostLock\settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>Bumped when a stored value changes meaning, so old files can be migrated.</summary>
    public int SettingsVersion { get; set; } = 2;

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

    public string UnlockHotkey { get; set; } = "Ctrl+Alt+U";
    public string StartHotkey { get; set; } = "Ctrl+Alt+L";

    /// <summary>
    /// Absolute ceiling in seconds even for an "until I unlock" freeze. 0 (the default)
    /// disables the cap, so an indefinite freeze really is indefinite.
    /// </summary>
    public int SafetyMaxSeconds { get; set; }

    /// <summary>Base64 PBKDF2 salt for <see cref="PinHash"/>. Null when no PIN is configured.</summary>
    public string? PinSalt { get; set; }

    /// <summary>Base64 PBKDF2-SHA256 hash of the PIN. Null when no PIN is configured.</summary>
    public string? PinHash { get; set; }

    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }

    /// <summary>Keep the display awake while frozen.</summary>
    public bool KeepDisplayAwake { get; set; }
    public bool PlaySound { get; set; } = true;

    // Lightweight stats, purely informational.
    public int FreezeCount { get; set; }
    public long TotalFrozenSeconds { get; set; }
    public DateTime? LastFrozenUtc { get; set; }

    [JsonIgnore]
    public bool IsIndefinite => DurationSeconds <= 0;

    [JsonIgnore]
    public bool HasPin => !string.IsNullOrEmpty(PinSalt) && !string.IsNullOrEmpty(PinHash);

    /// <summary>Stores a new PIN. Only the salted hash is ever written to disk.</summary>
    public void SetPin(string pin)
    {
        var (salt, hash) = PinService.Create(pin);
        PinSalt = salt;
        PinHash = hash;
    }

    public void ClearPin()
    {
        PinSalt = null;
        PinHash = null;
    }

    public bool VerifyPin(string pin) => HasPin && PinService.Verify(pin, PinSalt, PinHash);

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    /// <summary>Copies every editable value in place (references to this instance stay valid).</summary>
    public void CopyFrom(AppSettings other)
    {
        Mode = other.Mode;
        DurationSeconds = other.DurationSeconds;
        ArmSeconds = other.ArmSeconds;
        BlockAllKeys = other.BlockAllKeys;
        BlockMouse = other.BlockMouse;
        BlockSystemKeys = other.BlockSystemKeys;
        ShowCountdownOnOverlay = other.ShowCountdownOnOverlay;
        UnlockHotkey = other.UnlockHotkey;
        StartHotkey = other.StartHotkey;
        SafetyMaxSeconds = other.SafetyMaxSeconds;
        MinimizeToTray = other.MinimizeToTray;
        CloseToTray = other.CloseToTray;
        StartWithWindows = other.StartWithWindows;
        StartMinimized = other.StartMinimized;
        KeepDisplayAwake = other.KeepDisplayAwake;
        PlaySound = other.PlaySound;
        PinSalt = other.PinSalt;
        PinHash = other.PinHash;
    }
}
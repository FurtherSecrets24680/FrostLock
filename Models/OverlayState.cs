namespace FrostLock.Models;

public enum FreezePhase
{
    Idle,
    Arming,
    Frozen
}

/// <summary>Snapshot pushed to every overlay window on each tick.</summary>
public sealed class OverlayState
{
    public FreezePhase Phase { get; init; }
    public FreezeMode Mode { get; init; }
    public TimeSpan? Remaining { get; init; }
    public double Progress { get; init; }
    public bool MouseBlocked { get; init; }
    public bool ShowCountdown { get; init; } = true;

    /// <summary>Optional message shown on the lock screen. Empty means nothing extra.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Show the wall clock and the projected unlock time.</summary>
    public bool ShowClock { get; init; } = true;

    /// <summary>Local time the current freeze is scheduled to end. Null when indefinite.</summary>
    public DateTime? UnlockAt { get; init; }

    /// <summary>Seconds left before PIN entry is accepted again (throttling). Zero when open.</summary>
    public TimeSpan PinCooldownRemaining { get; init; }

    /// <summary>Entries left before the next PIN cooldown starts.</summary>
    public int PinAttemptsRemaining { get; init; }

    /// <summary>Same as <see cref="PinCooldownRemaining"/>, for the panic phrase.</summary>
    public TimeSpan PanicCooldownRemaining { get; init; }

    /// <summary>Entries left before the next panic-phrase cooldown starts.</summary>
    public int PanicAttemptsRemaining { get; init; }

    /// <summary>True when a panic phrase is configured and can be offered on the lock screen.</summary>
    public bool PanicAvailable { get; init; }

    public string UnlockHint { get; init; } = string.Empty;
    public string ModeLabel { get; init; } = string.Empty;
    public bool Interactive { get; init; } = true;

    /// <summary>PIN mode: the lock screen shows a PIN prompt instead of an Unlock button.</summary>
    public bool RequiresPin { get; init; }

    /// <summary>Whether the lock screen can extend this freeze.</summary>
    public bool CanExtend { get; init; }

    /// <summary>Minutes added by one extension.</summary>
    public int ExtendMinutes { get; init; } = 5;

    /// <summary>Extension instruction shown on the lock screen.</summary>
    public string ExtendHint { get; init; } = string.Empty;
}

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
    public string UnlockHint { get; init; } = string.Empty;
    public string ModeLabel { get; init; } = string.Empty;
    public bool Interactive { get; init; } = true;

    /// <summary>PIN mode: the lock screen shows a PIN prompt instead of an Unlock button.</summary>
    public bool RequiresPin { get; init; }
}
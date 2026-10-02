namespace SnapFloat.Core.Previews;

/// <summary>Things that keep a preview on screen while they are active.</summary>
[Flags]
public enum HoldReason
{
    None = 0,
    Hover = 1,
    Drag = 2,
    Menu = 4,
    Pinned = 8,
}

/// <summary>
/// Pure state machine deciding when a preview should auto-dismiss. The UI owns the actual timer: whenever this
/// returns a non-null delay the UI (re)starts its timer with that delay; null means "stop the timer".
/// </summary>
public sealed class DismissSchedule
{
    /// <summary>After an interaction ends the preview stays at least this long, so it doesn't vanish under the cursor.</summary>
    public static readonly TimeSpan ResumeGrace = TimeSpan.FromSeconds(3);

    private readonly TimeSpan _duration;
    private readonly bool _autoClose;
    private HoldReason _holds;

    public DismissSchedule(TimeSpan duration, bool autoClose)
    {
        _duration = duration;
        _autoClose = autoClose;
    }

    public HoldReason Holds => _holds;
    public bool IsHeld => _holds != HoldReason.None;

    /// <summary>Delay to use when the preview first appears.</summary>
    public TimeSpan? Start() => _autoClose && !IsHeld ? _duration : null;

    /// <summary>Adds a hold. Always stops the timer.</summary>
    public TimeSpan? Hold(HoldReason reason)
    {
        _holds |= reason;
        return null;
    }

    /// <summary>Removes a hold; restarts the timer once nothing holds the preview any more.</summary>
    public TimeSpan? Release(HoldReason reason)
    {
        _holds &= ~reason;
        if (!_autoClose || IsHeld) return null;
        return _duration < ResumeGrace ? _duration : ResumeGrace;
    }
}

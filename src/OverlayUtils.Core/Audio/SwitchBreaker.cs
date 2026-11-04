namespace OverlayUtils.Core.Audio;

public enum BreakerDecision
{
    /// <summary>Go ahead (when a switch was needed, it is counted).</summary>
    Allowed,
    /// <summary>Too many switches: refused, and auto-switch is now paused (log this once).</summary>
    Tripped,
    /// <summary>Refused: still paused.</summary>
    Paused,
}

/// <summary>
/// Auto-switch circuit breaker for one direction: at most <see cref="MaxSwitches"/> automatic
/// switches within <see cref="Window"/>; one more trips it, and it stays paused until a device
/// event arrives after <see cref="Window"/> without events. Pure: the caller passes the time.
/// Not thread-safe (used on the audio thread only).
/// </summary>
public sealed class SwitchBreaker
{
    public const int MaxSwitches = 3;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    private readonly Queue<DateTime> _switches = new();
    private DateTime? _lastEvent;
    private bool _paused;

    /// <summary>
    /// Called for every device event that re-evaluates auto-switching in this direction;
    /// <paramref name="switchNeeded"/> says whether the rule would change the default.
    /// </summary>
    public BreakerDecision Check(DateTime now, bool switchNeeded)
    {
        var quiet = _lastEvent is not { } last || now - last >= Window;
        _lastEvent = now;
        if (_paused)
        {
            if (!quiet) return BreakerDecision.Paused;
            _paused = false;
            _switches.Clear();
        }
        if (!switchNeeded) return BreakerDecision.Allowed;
        while (_switches.Count > 0 && now - _switches.Peek() >= Window) _switches.Dequeue();
        if (_switches.Count >= MaxSwitches)
        {
            _paused = true;
            return BreakerDecision.Tripped;
        }
        _switches.Enqueue(now);
        return BreakerDecision.Allowed;
    }
}

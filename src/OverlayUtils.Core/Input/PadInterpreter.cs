using OverlayUtils.Core.Overlay;

namespace OverlayUtils.Core.Input;

/// <summary>Gamepad buttons in SDL's positional layout (A = bottom face button).</summary>
public enum PadButton { A, B, X, Y, Back, Guide, Start, LeftStick, RightStick, LB, RB, DpadUp, DpadDown, DpadLeft, DpadRight }

public enum PadAxis { LeftX, LeftY, RightX, RightY, LeftTrigger, RightTrigger }

public enum PadActionKind { ToggleOverlay, MonitorTv, MonitorDesk, Command }

public readonly record struct PadAction(PadActionKind Kind, OverlayCommand Command = default);

/// <summary>
/// Turns pad button and axis events into actions: Back+RB toggles the
/// overlay (on release), Back+D-pad Up/Down switch monitor profiles while
/// the overlay is closed, and while it is open the pad drives it, with
/// repeat on held directions and Back held alone opening Settings
/// (Python: _check_combos, _overlay_armed, _back_hold, _nav_repeat).
/// No SDL here, so the self-test can drive it; not thread-safe, the caller
/// serialises calls (PadService does everything on its own thread).
/// </summary>
public sealed class PadInterpreter
{
    public static readonly TimeSpan RepeatDelay = TimeSpan.FromSeconds(0.4);
    public static readonly TimeSpan RepeatInterval = TimeSpan.FromSeconds(0.12);
    public static readonly TimeSpan SettingsHold = TimeSpan.FromSeconds(0.6);
    public const short PressThreshold = 16000, ReleaseThreshold = 8000;

    // Triggers behave as buttons; they get keys after the PadButton values.
    private const int LT = 100, RT = 101;
    // Repeat sources for the stick, distinct from button keys.
    private const int StickX = 200, StickY = 201;

    private static readonly IReadOnlyList<PadAction> None = [];

    private sealed class Pad
    {
        public readonly HashSet<int> Held = [];
        public bool Armed;              // exactly Back+RB held: release toggles the overlay
        public bool BackConsumed;       // this Back hold already opened Settings
        public DateTimeOffset? BackSince; // Back pressed with nothing else held
        public bool LtDown, RtDown;
        public int StickXDir, StickYDir;
        public readonly Dictionary<int, (OverlayCommand Repeat, DateTimeOffset Next)> Repeats = [];
    }

    private readonly TimeProvider _time;
    private readonly Dictionary<uint, Pad> _pads = [];

    public PadInterpreter(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public bool OverlayOpen { get; set; }
    public bool CombosEnabled { get; set; } = true;
    public uint? LastUsedPad { get; private set; }

    public IReadOnlyList<PadAction> ButtonDown(uint pad, PadButton button)
    {
        LastUsedPad = pad;
        return button == PadButton.Guide ? None : Press(Get(pad), (int)button);
    }

    public IReadOnlyList<PadAction> ButtonUp(uint pad, PadButton button)
    {
        LastUsedPad = pad;
        return button == PadButton.Guide ? None : Release(Get(pad), (int)button);
    }

    public IReadOnlyList<PadAction> Axis(uint pad, PadAxis axis, short value)
    {
        var state = Get(pad);
        switch (axis)
        {
            case PadAxis.LeftTrigger:
                return Trigger(state, ref state.LtDown, LT, value);
            case PadAxis.RightTrigger:
                return Trigger(state, ref state.RtDown, RT, value);
            case PadAxis.LeftX:
                return Stick(state, ref state.StickXDir, StickX, value, OverlayCommand.Left, OverlayCommand.Right);
            case PadAxis.LeftY:
                return Stick(state, ref state.StickYDir, StickY, value, OverlayCommand.Up, OverlayCommand.Down);
            default:
                return None;
        }
    }

    public IReadOnlyList<PadAction> Tick()
    {
        if (!OverlayOpen)
        {
            foreach (var state in _pads.Values) state.Repeats.Clear();
            return None;
        }
        var now = _time.GetUtcNow();
        var actions = new List<PadAction>();
        foreach (var state in _pads.Values)
        {
            if (state.BackSince is { } since && now - since >= SettingsHold)
            {
                state.BackSince = null;
                state.BackConsumed = true;
                actions.Add(Command(OverlayCommand.Settings));
            }
            foreach (var (source, (repeat, next)) in state.Repeats.ToList())
            {
                if (now < next) continue;
                state.Repeats[source] = (repeat, now + RepeatInterval);
                actions.Add(Command(repeat));
            }
        }
        return actions;
    }

    public void PadRemoved(uint pad)
    {
        _pads.Remove(pad);
        if (LastUsedPad == pad) LastUsedPad = null;
    }

    /// <summary>SDL power state and percent to a 0..3 level (empty..full), or null if unknown.</summary>
    public static int? BatteryLevel(int sdlPowerState, int percent)
    {
        const int onBattery = 1, charging = 3, charged = 4;
        if (sdlPowerState == charged) return 3;
        if (sdlPowerState is not (onBattery or charging) || percent < 0) return null;
        return percent switch { <= 10 => 0, <= 40 => 1, <= 70 => 2, _ => 3 };
    }

    private Pad Get(uint pad)
    {
        if (!_pads.TryGetValue(pad, out var state)) _pads[pad] = state = new Pad();
        return state;
    }

    private IReadOnlyList<PadAction> Press(Pad state, int key)
    {
        state.Held.Add(key);
        state.Armed = !state.BackConsumed && state.Held.Count == 2
            && state.Held.Contains((int)PadButton.Back) && state.Held.Contains((int)PadButton.RB);
        state.BackSince = key == (int)PadButton.Back && state.Held.Count == 1 ? _time.GetUtcNow() : null;

        if (!OverlayOpen)
        {
            if (!CombosEnabled || !state.Held.Contains((int)PadButton.Back)) return None;
            return key switch
            {
                (int)PadButton.DpadUp => [new PadAction(PadActionKind.MonitorTv)],
                (int)PadButton.DpadDown => [new PadAction(PadActionKind.MonitorDesk)],
                _ => None,
            };
        }

        return key switch
        {
            (int)PadButton.DpadUp => Navigate(state, key, OverlayCommand.Up, OverlayCommand.Up),
            (int)PadButton.DpadDown => Navigate(state, key, OverlayCommand.Down, OverlayCommand.Down),
            (int)PadButton.DpadLeft => Navigate(state, key, OverlayCommand.Left, OverlayCommand.LeftRepeat),
            (int)PadButton.DpadRight => Navigate(state, key, OverlayCommand.Right, OverlayCommand.RightRepeat),
            (int)PadButton.A => [Command(OverlayCommand.A)],
            (int)PadButton.B => [Command(OverlayCommand.B)],
            LT => [Command(OverlayCommand.PrevTab)],
            RT => [Command(OverlayCommand.NextTab)],
            _ => None,
        };
    }

    private IReadOnlyList<PadAction> Release(Pad state, int key)
    {
        var fire = state.Armed && key is (int)PadButton.Back or (int)PadButton.RB;
        state.Held.Remove(key);
        state.Repeats.Remove(key);
        if (key == (int)PadButton.Back)
        {
            state.BackSince = null;
            state.BackConsumed = false;
        }
        if (!fire) return None;
        state.Armed = false;
        return [new PadAction(PadActionKind.ToggleOverlay)];
    }

    private IReadOnlyList<PadAction> Trigger(Pad state, ref bool down, int key, short value)
    {
        if (!down && value > PressThreshold)
        {
            down = true;
            return Press(state, key);
        }
        if (down && value < ReleaseThreshold)
        {
            down = false;
            return Release(state, key);
        }
        return None;
    }

    private IReadOnlyList<PadAction> Stick(Pad state, ref int dir, int source, short value,
        OverlayCommand negative, OverlayCommand positive)
    {
        var next = value > PressThreshold ? 1
            : value < -PressThreshold ? -1
            : Math.Abs((int)value) < ReleaseThreshold ? 0
            : dir;
        if (next == dir) return None;
        dir = next;
        state.Repeats.Remove(source);
        if (next == 0 || !OverlayOpen) return None;
        var command = next < 0 ? negative : positive;
        var repeat = command switch
        {
            OverlayCommand.Left => OverlayCommand.LeftRepeat,
            OverlayCommand.Right => OverlayCommand.RightRepeat,
            _ => command,
        };
        return Navigate(state, source, command, repeat);
    }

    private IReadOnlyList<PadAction> Navigate(Pad state, int source, OverlayCommand command, OverlayCommand repeat)
    {
        state.Repeats[source] = (repeat, _time.GetUtcNow() + RepeatDelay);
        return [Command(command)];
    }

    private static PadAction Command(OverlayCommand command) => new(PadActionKind.Command, command);
}

using OverlayUtils.Core.Overlay;

namespace OverlayUtils.Core.Input;

/// <summary>Gamepad buttons in SDL's positional layout (A = bottom face button).</summary>
public enum PadButton { A, B, X, Y, Back, Guide, Start, LeftStick, RightStick, LB, RB, DpadUp, DpadDown, DpadLeft, DpadRight }

public enum PadAxis { LeftX, LeftY, RightX, RightY, LeftTrigger, RightTrigger }

public enum PadActionKind { ToggleOverlay, MonitorTv, MonitorDesk, Command, VolumeDown, VolumeUp, MicMute }

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

    // One physical pad can show up twice (DS4Windows/ViGEm, reWASD: real pad + virtual
    // Xbox pad), so each press arrives from two pads. The same action from another pad
    // this close in time is dropped; a toggle shortly after a toggle is always dropped.
    public static readonly TimeSpan DuplicateWindow = TimeSpan.FromMilliseconds(50);
    public static readonly TimeSpan ToggleDebounce = TimeSpan.FromMilliseconds(250);

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
        public readonly Dictionary<int, (PadAction Repeat, DateTimeOffset Next)> Repeats = [];
    }

    private readonly TimeProvider _time;
    private readonly Dictionary<uint, Pad> _pads = [];
    private readonly Dictionary<PadAction, (uint Pad, DateTimeOffset At)> _lastEmitted = [];
    private bool _overlayOpen;

    public PadInterpreter(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public bool OverlayOpen
    {
        get => _overlayOpen;
        set
        {
            if (value == _overlayOpen) return;
            _overlayOpen = value;
            // A Back hold that started before the change must not open Settings.
            foreach (var state in _pads.Values)
            {
                state.BackSince = null;
                state.Repeats.Clear();
            }
        }
    }
    public bool CombosEnabled { get; set; } = true;
    public uint? LastUsedPad { get; private set; }

    public IReadOnlyList<PadAction> ButtonDown(uint pad, PadButton button)
    {
        LastUsedPad = pad;
        return button == PadButton.Guide ? None : Emit(pad, Press(Get(pad), (int)button));
    }

    public IReadOnlyList<PadAction> ButtonUp(uint pad, PadButton button)
    {
        LastUsedPad = pad;
        return button == PadButton.Guide ? None : Emit(pad, Release(Get(pad), (int)button));
    }

    public IReadOnlyList<PadAction> Axis(uint pad, PadAxis axis, short value)
    {
        var state = Get(pad);
        return Emit(pad, axis switch
        {
            PadAxis.LeftTrigger => Trigger(state, ref state.LtDown, LT, value),
            PadAxis.RightTrigger => Trigger(state, ref state.RtDown, RT, value),
            PadAxis.LeftX => Stick(state, ref state.StickXDir, StickX, value, OverlayCommand.Left, OverlayCommand.Right),
            PadAxis.LeftY => Stick(state, ref state.StickYDir, StickY, value, OverlayCommand.Up, OverlayCommand.Down),
            _ => None,
        });
    }

    public IReadOnlyList<PadAction> Tick()
    {
        var now = _time.GetUtcNow();
        var actions = new List<PadAction>();
        foreach (var (pad, state) in _pads)
        {
            var mine = new List<PadAction>();
            // Closed: only the Back+D-pad volume repeat runs, and only while Back is still held.
            if (!OverlayOpen && (!CombosEnabled || !state.Held.Contains((int)PadButton.Back))) state.Repeats.Clear();
            if (OverlayOpen && state.BackSince is { } since && now - since >= SettingsHold)
            {
                state.BackSince = null;
                state.BackConsumed = true;
                mine.Add(Command(OverlayCommand.Settings));
            }
            foreach (var (source, (repeat, next)) in state.Repeats.ToList())
            {
                if (now < next) continue;
                state.Repeats[source] = (repeat, now + RepeatInterval);
                mine.Add(repeat);
            }
            actions.AddRange(Emit(pad, mine));
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

    /// <summary>Drops actions a duplicate pad already produced (see <see cref="DuplicateWindow"/>).</summary>
    private IReadOnlyList<PadAction> Emit(uint pad, IReadOnlyList<PadAction> actions)
    {
        if (actions.Count == 0) return actions;
        var now = _time.GetUtcNow();
        var kept = new List<PadAction>(actions.Count);
        foreach (var action in actions)
        {
            if (_lastEmitted.TryGetValue(action, out var last))
            {
                var window = action.Kind == PadActionKind.ToggleOverlay ? ToggleDebounce : DuplicateWindow;
                var otherPad = last.Pad != pad || action.Kind == PadActionKind.ToggleOverlay;
                if (otherPad && now - last.At < window) continue;
            }
            _lastEmitted[action] = (pad, now);
            kept.Add(action);
        }
        return kept;
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
                (int)PadButton.DpadLeft => Volume(state, key, PadActionKind.VolumeDown),
                (int)PadButton.DpadRight => Volume(state, key, PadActionKind.VolumeUp),
                (int)PadButton.Y => [new PadAction(PadActionKind.MicMute)],
                _ => None,
            };
        }

        // LB/RB with Back held belong to the Back+RB chord (and the Moonlight quit chord): no media then.
        var backHeld = state.Held.Contains((int)PadButton.Back);

        return key switch
        {
            (int)PadButton.DpadUp => Navigate(state, key, OverlayCommand.Up, OverlayCommand.Up),
            (int)PadButton.DpadDown => Navigate(state, key, OverlayCommand.Down, OverlayCommand.Down),
            (int)PadButton.DpadLeft => Navigate(state, key, OverlayCommand.Left, OverlayCommand.LeftRepeat),
            (int)PadButton.DpadRight => Navigate(state, key, OverlayCommand.Right, OverlayCommand.RightRepeat),
            (int)PadButton.A => [Command(OverlayCommand.A)],
            (int)PadButton.B => [Command(OverlayCommand.B)],
            (int)PadButton.X => [Command(OverlayCommand.PlayPause)],
            (int)PadButton.Y => [Command(OverlayCommand.Remove)],
            (int)PadButton.LB when !backHeld => [Command(OverlayCommand.PrevTrack)],
            (int)PadButton.RB when !backHeld => [Command(OverlayCommand.NextTrack)],
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
            // The Back+D-pad volume repeat stops with Back.
            foreach (var source in state.Repeats.Where(r => r.Value.Repeat.Kind != PadActionKind.Command).Select(r => r.Key).ToList())
                state.Repeats.Remove(source);
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
        state.Repeats[source] = (Command(repeat), _time.GetUtcNow() + RepeatDelay);
        return [Command(command)];
    }

    private IReadOnlyList<PadAction> Volume(Pad state, int source, PadActionKind kind)
    {
        var action = new PadAction(kind);
        state.Repeats[source] = (action, _time.GetUtcNow() + RepeatDelay);
        return [action];
    }

    private static PadAction Command(OverlayCommand command) => new(PadActionKind.Command, command);
}

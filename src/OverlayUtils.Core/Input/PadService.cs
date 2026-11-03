using OverlayUtils.Core.Diagnostics;

namespace OverlayUtils.Core.Input;

/// <summary>
/// Reads gamepads with SDL3 on its own thread and feeds a
/// <see cref="PadInterpreter"/>. All SDL calls happen on that thread.
/// If SDL3.dll is missing or fails, it logs once and pads stay disabled;
/// the rest of the app is unaffected.
/// </summary>
public sealed class PadService : IDisposable
{
    private const int WaitMs = 15;
    private static readonly TimeSpan BatteryPoll = TimeSpan.FromSeconds(30);

    private readonly PadInterpreter _interpreter;
    private readonly object _lock = new();
    private readonly Dictionary<uint, nint> _pads = [];
    private Thread? _thread;
    private volatile bool _stop;
    private int? _battery;

    public PadService(PadInterpreter interpreter) => _interpreter = interpreter;

    /// <summary>Raised on the pad thread.</summary>
    public event Action<PadAction>? ActionRaised;

    /// <summary>Pads added or removed, or battery changed. Raised on the pad thread.</summary>
    public event Action? StateChanged;

    public bool Available { get; private set; }

    public int PadCount { get { lock (_lock) return _pads.Count; } }

    public int? BatteryLevel { get { lock (_lock) return _battery; } }

    public bool OverlayOpen { set { lock (_lock) _interpreter.OverlayOpen = value; } }

    public bool CombosEnabled { set { lock (_lock) _interpreter.CombosEnabled = value; } }

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "Pads" };
        _thread.Start();
    }

    private void Run()
    {
        try
        {
            Sdl.SetHint(Sdl.HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS, "1"); // pads must work while a game has focus
            if (!Sdl.Init(Sdl.INIT_GAMEPAD))
            {
                Log.Error("SDL init failed: " + Sdl.GetError());
                return;
            }
        }
        catch (Exception e)
        {
            Log.Error("Loading SDL3 (pads disabled)", e);
            return;
        }

        Available = true;
        var nextBattery = DateTime.UtcNow + BatteryPoll;
        try
        {
            while (!_stop)
            {
                if (Sdl.WaitEventTimeout(out var e, WaitMs))
                {
                    do Handle(e);
                    while (!_stop && Sdl.WaitEventTimeout(out e, 0));
                }
                IReadOnlyList<PadAction> ticked;
                lock (_lock) ticked = _interpreter.Tick();
                Raise(ticked);
                if (DateTime.UtcNow >= nextBattery)
                {
                    nextBattery = DateTime.UtcNow + BatteryPoll;
                    UpdateBattery();
                }
            }
        }
        catch (Exception e)
        {
            Log.Error("Pad thread", e);
        }
        finally
        {
            Available = false;
            try
            {
                lock (_lock)
                {
                    foreach (var pad in _pads.Values) Sdl.CloseGamepad(pad);
                    _pads.Clear();
                }
                Sdl.Quit();
            }
            catch (Exception e)
            {
                Log.Error("Closing SDL", e);
            }
        }
    }

    private void Handle(Sdl.Event e)
    {
        switch (e.Type)
        {
            case Sdl.EVENT_GAMEPAD_ADDED:
                var handle = Sdl.OpenGamepad(e.Which);
                if (handle == 0)
                {
                    Log.Error("Opening gamepad failed: " + Sdl.GetError());
                    return;
                }
                lock (_lock) _pads[e.Which] = handle;
                Log.Info("Gamepad connected: " + Sdl.GetGamepadName(handle));
                UpdateBattery(force: true);
                break;
            case Sdl.EVENT_GAMEPAD_REMOVED:
                lock (_lock)
                {
                    if (_pads.Remove(e.Which, out var removed)) Sdl.CloseGamepad(removed);
                    _interpreter.PadRemoved(e.Which);
                }
                UpdateBattery(force: true);
                break;
            case Sdl.EVENT_GAMEPAD_BUTTON_DOWN or Sdl.EVENT_GAMEPAD_BUTTON_UP:
                if (ToButton(e.Control) is not { } button) return;
                IReadOnlyList<PadAction> actions;
                lock (_lock)
                {
                    actions = e.Type == Sdl.EVENT_GAMEPAD_BUTTON_DOWN
                        ? _interpreter.ButtonDown(e.Which, button)
                        : _interpreter.ButtonUp(e.Which, button);
                }
                Raise(actions);
                break;
            case Sdl.EVENT_GAMEPAD_AXIS_MOTION:
                if (e.Control > (byte)PadAxis.RightTrigger) return;
                IReadOnlyList<PadAction> moved;
                lock (_lock) moved = _interpreter.Axis(e.Which, (PadAxis)e.Control, e.Value);
                Raise(moved);
                break;
        }
    }

    /// <summary>SDL_GamepadButton (SOUTH=0 ... DPAD_RIGHT=14) to our enum, which uses the same order.</summary>
    private static PadButton? ToButton(byte sdl) => sdl <= (byte)PadButton.DpadRight ? (PadButton)sdl : null;

    private void UpdateBattery(bool force = false)
    {
        int? level = null;
        lock (_lock)
        {
            var order = _interpreter.LastUsedPad is { } last && _pads.ContainsKey(last)
                ? _pads.Keys.OrderBy(id => id == last ? 0 : 1)
                : _pads.Keys.AsEnumerable();
            foreach (var id in order.ToList())
            {
                level = PadInterpreter.BatteryLevel(Sdl.GetGamepadPowerInfo(_pads[id], out var percent), percent);
                if (level is not null) break;
            }
            if (!force && level == _battery) return;
            _battery = level;
        }
        try { StateChanged?.Invoke(); }
        catch (Exception e) { Log.Error("Pad state", e); }
    }

    private void Raise(IReadOnlyList<PadAction> actions)
    {
        foreach (var action in actions)
        {
            try { ActionRaised?.Invoke(action); }
            catch (Exception e) { Log.Error("Pad action", e); }
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(TimeSpan.FromSeconds(1));
    }
}

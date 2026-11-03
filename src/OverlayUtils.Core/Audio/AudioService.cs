using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using OverlayUtils.Core.Config;
using OverlayUtils.Core.Diagnostics;
using OverlayUtils.Core.Interop;

namespace OverlayUtils.Core.Audio;

public enum AudioFlow { Output, Input }

/// <summary>
/// Windows audio devices, master volume and microphone mute, on its own MTA
/// thread: every COM call happens there. Windows notifications (any thread)
/// only queue work to it. Public members read a cached snapshot or queue a
/// command, so they are safe and non-blocking from any thread. Errors are
/// logged, never thrown.
/// </summary>
public sealed partial class AudioService : IDisposable
{
    /// <summary>Device events come in bursts (added + state + default per role): handle them once.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);
    private static readonly Guid EventContext = new("3F6C2E0A-6B1D-4B8E-9C61-2D8A0F4B7E15");
    private static readonly CoreAudio.ERole[] AllRoles =
        [CoreAudio.ERole.Console, CoreAudio.ERole.Multimedia, CoreAudio.ERole.Communications];

    private readonly ConfigStore _config;
    private readonly BlockingCollection<Action> _queue = [];
    private readonly object _lock = new();

    // Cached state, guarded by _lock.
    private List<AudioDeviceRef> _outputs = [];
    private List<AudioDeviceRef> _inputs = [];
    private string? _defaultOutput;
    private string? _defaultInput;
    private double _volume;
    private bool _muted;
    private bool _micMuted;

    // Worker-thread only.
    private Thread? _thread;
    private IMMDeviceEnumerator? _enumerator;
    private IPolicyConfig? _policy;
    private NotificationClient? _client;
    private IAudioEndpointVolume? _outputVolume;
    private IAudioEndpointVolume? _inputVolume;
    private VolumeCallback? _outputCallback;
    private VolumeCallback? _inputCallback;
    private string? _boundOutput;
    private string? _boundInput;
    private DateTime? _refreshDue;
    private bool _disposed;

    public AudioService(ConfigStore config) => _config = config;

    /// <summary>Devices, defaults, volume or mute changed. Raised on the audio thread.</summary>
    public event Action? Changed;

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "Audio" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        _config.Changed += OnConfigChanged;
    }

    /// <summary>Connected devices of that direction, in priority-list order (unlisted ones last).</summary>
    public IReadOnlyList<AudioDeviceRef> Available(AudioFlow flow)
    {
        lock (_lock) return flow == AudioFlow.Output ? _outputs : _inputs;
    }

    public AudioDeviceRef? Default(AudioFlow flow)
    {
        lock (_lock)
        {
            var (list, id) = flow == AudioFlow.Output ? (_outputs, _defaultOutput) : (_inputs, _defaultInput);
            return id is null ? null : list.Find(d => d.Id == id) ?? new AudioDeviceRef(id, id);
        }
    }

    /// <summary>Master volume of the default output, 0..1.</summary>
    public double Volume { get { lock (_lock) return _volume; } }

    public bool Muted { get { lock (_lock) return _muted; } }

    /// <summary>Mute of the default input device.</summary>
    public bool MicMuted { get { lock (_lock) return _micMuted; } }

    /// <summary>Manual pick: makes the device the default for all roles.</summary>
    public void SetDefault(AudioFlow flow, string id) => Post(() => ApplyDefault(flow, id));

    /// <summary>Switches to the next (+1) or previous (-1) available device, wrapping around.</summary>
    public void Cycle(AudioFlow flow, int direction) => Post(() =>
    {
        List<AudioDeviceRef> list;
        string? current;
        lock (_lock)
        {
            (list, current) = flow == AudioFlow.Output ? (_outputs, _defaultOutput) : (_inputs, _defaultInput);
        }
        if (list.Count == 0 || direction == 0) return;
        var index = list.FindIndex(d => d.Id == current);
        var next = index < 0
            ? (direction > 0 ? 0 : list.Count - 1)
            : ((index + Math.Sign(direction)) % list.Count + list.Count) % list.Count;
        if (list[next].Id != current) ApplyDefault(flow, list[next].Id);
    });

    /// <summary>One 5 % step, never above the device's cap; raising unmutes.</summary>
    public void StepVolume(int direction) => Post(() =>
    {
        if (_outputVolume is null || _boundOutput is null || direction == 0) return;
        var cap = AudioRules.CapFor(_config.Current.AudioVolumeCaps, _boundOutput);
        var current = _outputVolume.GetMasterVolumeLevelScalar();
        var value = AudioRules.StepVolume(current, direction, cap);
        _outputVolume.SetMasterVolumeLevelScalar((float)value, EventContext);
        if (direction > 0 && _outputVolume.GetMute() != 0) _outputVolume.SetMute(0, EventContext);
        ReadVolumes();
    });

    public void ToggleMute() => Post(() =>
    {
        if (_outputVolume is null) return;
        _outputVolume.SetMute(_outputVolume.GetMute() != 0 ? 0 : 1, EventContext);
        ReadVolumes();
    });

    public void ToggleMicMute() => Post(() =>
    {
        if (_inputVolume is null) return;
        _inputVolume.SetMute(_inputVolume.GetMute() != 0 ? 0 : 1, EventContext);
        ReadVolumes();
    });

    // --- worker thread --------------------------------------------------------

    private void Post(Action work)
    {
        try { _queue.Add(work); }
        catch (InvalidOperationException) { } // disposed
    }

    private void Run()
    {
        try
        {
            _enumerator = CoreAudio.Create<IMMDeviceEnumerator>(CoreAudio.CLSID_MMDeviceEnumerator);
            _client = new NotificationClient(this);
            _enumerator.RegisterEndpointNotificationCallback(_client);
            Refresh(starting: true);
        }
        catch (Exception e)
        {
            Log.Error("Starting audio (audio controls disabled)", e);
            Cleanup();
            return;
        }

        try
        {
            while (true)
            {
                var wait = _refreshDue is { } due
                    ? Math.Max(0, (int)Math.Ceiling((due - DateTime.UtcNow).TotalMilliseconds))
                    : Timeout.Infinite;
                if (_queue.TryTake(out var work, wait)) Execute(work);
                else if (_queue.IsCompleted) break;
                if (_refreshDue is { } dueNow && DateTime.UtcNow >= dueNow)
                {
                    _refreshDue = null;
                    Execute(() => Refresh(starting: false));
                }
            }
        }
        catch (Exception e)
        {
            Log.Error("Audio thread", e);
        }
        finally
        {
            Cleanup();
        }
    }

    private static void Execute(Action work)
    {
        try { work(); }
        catch (Exception e) { Log.Error("Audio", e); }
    }

    private void ScheduleRefresh() => Post(() => _refreshDue ??= DateTime.UtcNow + Settle);

    /// <summary>
    /// Re-reads devices and defaults. A direction whose set of available
    /// devices changed is merged into its list and, with auto-switch on,
    /// switched to the best available device. Default changes alone never
    /// switch. A new default output gets its volume rebound and capped.
    /// </summary>
    private void Refresh(bool starting)
    {
        var outputs = Enumerate(CoreAudio.EDataFlow.Render);
        var inputs = Enumerate(CoreAudio.EDataFlow.Capture);
        var defaultOutput = DefaultId(CoreAudio.EDataFlow.Render);
        var defaultInput = DefaultId(CoreAudio.EDataFlow.Capture);

        bool outputsChanged, inputsChanged;
        lock (_lock)
        {
            outputsChanged = !SameIds(_outputs, outputs);
            inputsChanged = !SameIds(_inputs, inputs);
        }

        if (starting || outputsChanged || inputsChanged || NamesChanged(outputs, inputs))
            UpdateLists(outputs, defaultOutput, inputs, defaultInput);

        var config = _config.Current;
        if (!starting)
        {
            if (outputsChanged && config.AudioAutoOutput)
                defaultOutput = AutoSwitch(AudioFlow.Output, config.AudioOutputOrder, outputs, defaultOutput);
            if (inputsChanged && config.AudioAutoInput)
                defaultInput = AutoSwitch(AudioFlow.Input, config.AudioInputOrder, inputs, defaultInput);
        }

        lock (_lock)
        {
            _outputs = Ranked(config.AudioOutputOrder, outputs);
            _inputs = Ranked(config.AudioInputOrder, inputs);
            _defaultOutput = defaultOutput;
            _defaultInput = defaultInput;
        }

        if (defaultOutput != _boundOutput) BindOutput(defaultOutput, applyCap: !starting);
        if (defaultInput != _boundInput) BindInput(defaultInput);
        ReadVolumes(raise: false);
        RaiseChanged();
    }

    /// <summary>Seeds empty lists (current default first) and merges the devices seen now; saves only on change.</summary>
    private void UpdateLists(List<AudioDeviceRef> outputs, string? defaultOutput, List<AudioDeviceRef> inputs, string? defaultInput)
    {
        var config = _config.Current;
        var newOutputs = Updated(config.AudioOutputOrder, outputs, defaultOutput);
        var newInputs = Updated(config.AudioInputOrder, inputs, defaultInput);
        if (newOutputs.SequenceEqual(config.AudioOutputOrder) && newInputs.SequenceEqual(config.AudioInputOrder)) return;
        _config.Update(c =>
        {
            c.AudioOutputOrder = Updated(c.AudioOutputOrder, outputs, defaultOutput);
            c.AudioInputOrder = Updated(c.AudioInputOrder, inputs, defaultInput);
        });
    }

    private static List<AudioDeviceRef> Updated(List<AudioDeviceRef> list, List<AudioDeviceRef> seen, string? currentDefault) =>
        list.Count == 0 ? AudioRules.Seed(seen, currentDefault) : AudioRules.Merge(list, seen);

    private bool NamesChanged(List<AudioDeviceRef> outputs, List<AudioDeviceRef> inputs)
    {
        lock (_lock)
            return outputs.Any(d => _outputs.Find(o => o.Id == d.Id) is { } o && o.Name != d.Name)
                || inputs.Any(d => _inputs.Find(i => i.Id == d.Id) is { } i && i.Name != d.Name);
    }

    private string? AutoSwitch(AudioFlow flow, List<AudioDeviceRef> ranked, List<AudioDeviceRef> available, string? current)
    {
        var target = AudioRules.Target(ranked, available.Select(d => d.Id).ToHashSet(), current);
        if (target is null) return current;
        Log.Info($"Audio auto-switch ({flow}): {available.Find(d => d.Id == target)?.Name ?? target}");
        return ApplyDefault(flow, target, refresh: false) ? target : current;
    }

    /// <summary>Sets the default for all roles. Returns false if Windows refused.</summary>
    private bool ApplyDefault(AudioFlow flow, string id, bool refresh = true)
    {
        try
        {
            _policy ??= CoreAudio.Create<IPolicyConfig>(CoreAudio.CLSID_PolicyConfigClient);
        }
        catch (Exception e)
        {
            Log.Error("Creating the audio policy client", e);
            return false;
        }
        var ok = true;
        foreach (var role in AllRoles)
        {
            var hr = _policy.SetDefaultEndpoint(id, role);
            if (hr < 0)
            {
                Log.Error($"Setting default audio {flow} ({role}) failed: 0x{hr:X8}");
                ok = false;
            }
        }
        if (refresh) ScheduleRefresh(); // Windows also sends default-changed events; this covers a missed one
        return ok;
    }

    /// <summary>Binds the default output volume; the cap applies only when the default changes, never at start.</summary>
    private void BindOutput(string? id, bool applyCap)
    {
        Unbind(ref _outputVolume, ref _outputCallback);
        _boundOutput = id;
        if (id is null) return;
        (_outputVolume, _outputCallback) = Bind(id);
        if (_outputVolume is null || !applyCap) return;
        try
        {
            var cap = AudioRules.CapFor(_config.Current.AudioVolumeCaps, id);
            if (_outputVolume.GetMasterVolumeLevelScalar() > cap + 1e-4)
                _outputVolume.SetMasterVolumeLevelScalar((float)cap, EventContext);
        }
        catch (Exception e)
        {
            Log.Error("Applying the volume cap", e);
        }
    }

    private void BindInput(string? id)
    {
        Unbind(ref _inputVolume, ref _inputCallback);
        _boundInput = id;
        if (id is not null) (_inputVolume, _inputCallback) = Bind(id);
    }

    private (IAudioEndpointVolume?, VolumeCallback?) Bind(string id)
    {
        IMMDevice? device = null;
        IAudioEndpointVolume? volume = null;
        try
        {
            device = _enumerator!.GetDevice(id);
            volume = CoreAudio.Wrap<IAudioEndpointVolume>(
                device.Activate(CoreAudio.IID_IAudioEndpointVolume, Native.CLSCTX_ALL, 0));
            var callback = new VolumeCallback(this);
            volume.RegisterControlChangeNotify(callback);
            return (volume, callback);
        }
        catch (Exception e)
        {
            Log.Error("Binding audio endpoint volume", e);
            CoreAudio.Release(volume);
            return (null, null);
        }
        finally
        {
            CoreAudio.Release(device);
        }
    }

    private static void Unbind(ref IAudioEndpointVolume? volume, ref VolumeCallback? callback)
    {
        if (volume is not null && callback is not null)
        {
            try { volume.UnregisterControlChangeNotify(callback); }
            catch (Exception e) { Log.Error("Unbinding audio endpoint volume", e); }
        }
        callback?.Detach();
        CoreAudio.Release(volume);
        volume = null;
        callback = null;
    }

    private void ReadVolumes(bool raise = true)
    {
        double volume = 0;
        bool muted = false, micMuted = false;
        try
        {
            if (_outputVolume is not null)
            {
                volume = _outputVolume.GetMasterVolumeLevelScalar();
                muted = _outputVolume.GetMute() != 0;
            }
            if (_inputVolume is not null) micMuted = _inputVolume.GetMute() != 0;
        }
        catch (Exception e)
        {
            Log.Error("Reading audio volume", e);
        }
        bool changed;
        lock (_lock)
        {
            changed = volume != _volume || muted != _muted || micMuted != _micMuted;
            (_volume, _muted, _micMuted) = (volume, muted, micMuted);
        }
        if (raise && changed) RaiseChanged();
    }

    private List<AudioDeviceRef> Enumerate(CoreAudio.EDataFlow flow)
    {
        var result = new List<AudioDeviceRef>();
        var collection = _enumerator!.EnumAudioEndpoints(flow, CoreAudio.DEVICE_STATE_ACTIVE);
        try
        {
            var count = collection.GetCount();
            for (uint i = 0; i < count; i++)
            {
                IMMDevice? device = null;
                try
                {
                    device = collection.Item(i);
                    var id = ReadId(device);
                    result.Add(new AudioDeviceRef(id, FriendlyName(device) ?? id));
                }
                catch (Exception e)
                {
                    Log.Error("Reading audio device", e);
                }
                finally
                {
                    CoreAudio.Release(device);
                }
            }
        }
        finally
        {
            CoreAudio.Release(collection);
        }
        return result;
    }

    private string? DefaultId(CoreAudio.EDataFlow flow)
    {
        IMMDevice? device = null;
        try
        {
            device = _enumerator!.GetDefaultAudioEndpoint(flow, CoreAudio.ERole.Multimedia);
            return ReadId(device);
        }
        catch (COMException e) when (e.HResult == CoreAudio.E_NOTFOUND)
        {
            return null; // no device of that kind
        }
        finally
        {
            CoreAudio.Release(device);
        }
    }

    private static string ReadId(IMMDevice device)
    {
        var pointer = device.GetId();
        try { return Marshal.PtrToStringUni(pointer) ?? ""; }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }

    private static string? FriendlyName(IMMDevice device)
    {
        IPropertyStore? store = null;
        var value = default(CoreAudio.PropVariant);
        try
        {
            store = device.OpenPropertyStore(CoreAudio.STGM_READ);
            store.GetValue(CoreAudio.PKEY_Device_FriendlyName, out value);
            return value.Type == CoreAudio.VT_LPWSTR ? Marshal.PtrToStringUni(value.Value1) : null;
        }
        catch (Exception e)
        {
            Log.Error("Reading audio device name", e);
            return null;
        }
        finally
        {
            Native.PropVariantClear(ref value);
            CoreAudio.Release(store);
        }
    }

    private static bool SameIds(List<AudioDeviceRef> a, List<AudioDeviceRef> b) =>
        a.Select(d => d.Id).ToHashSet().SetEquals(b.Select(d => d.Id));

    private static List<AudioDeviceRef> Ranked(List<AudioDeviceRef> order, List<AudioDeviceRef> available)
    {
        var rank = new Dictionary<string, int>();
        for (var i = 0; i < order.Count; i++) rank.TryAdd(order[i].Id, i);
        return [.. available.OrderBy(d => rank.TryGetValue(d.Id, out var r) ? r : int.MaxValue)];
    }

    /// <summary>The user reordered a list: re-sort the cached devices (no COM).</summary>
    private void OnConfigChanged(AppConfig config) => Post(() =>
    {
        bool changed;
        lock (_lock)
        {
            var outputs = Ranked(config.AudioOutputOrder, _outputs);
            var inputs = Ranked(config.AudioInputOrder, _inputs);
            changed = !outputs.SequenceEqual(_outputs) || !inputs.SequenceEqual(_inputs);
            (_outputs, _inputs) = (outputs, inputs);
        }
        if (changed) RaiseChanged();
    });

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception e) { Log.Error("Audio changed handler", e); }
    }

    private void Cleanup()
    {
        Unbind(ref _outputVolume, ref _outputCallback);
        Unbind(ref _inputVolume, ref _inputCallback);
        if (_enumerator is not null && _client is not null)
        {
            try { _enumerator.UnregisterEndpointNotificationCallback(_client); }
            catch (Exception e) { Log.Error("Unregistering audio notifications", e); }
        }
        _client?.Detach();
        CoreAudio.Release(_policy);
        CoreAudio.Release(_enumerator);
        _policy = null;
        _enumerator = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _config.Changed -= OnConfigChanged;
        _queue.CompleteAdding();
        _thread?.Join(TimeSpan.FromSeconds(1));
    }

    // --- callbacks from Windows (any thread): only queue work ---------------

    [GeneratedComClass]
    private sealed partial class NotificationClient(AudioService owner) : IMMNotificationClient
    {
        private volatile AudioService? _owner = owner;

        public void Detach() => _owner = null;

        public int OnDeviceStateChanged(nint deviceId, uint newState) => Schedule();
        public int OnDeviceAdded(nint deviceId) => Schedule();
        public int OnDeviceRemoved(nint deviceId) => Schedule();
        public int OnDefaultDeviceChanged(CoreAudio.EDataFlow flow, CoreAudio.ERole role, nint defaultDeviceId) => Schedule();

        public int OnPropertyValueChanged(nint deviceId, CoreAudio.PropertyKey key) =>
            key.FormatId == CoreAudio.PKEY_Device_FriendlyName.FormatId
            && key.PropertyId == CoreAudio.PKEY_Device_FriendlyName.PropertyId ? Schedule() : 0;

        private int Schedule()
        {
            try { _owner?.ScheduleRefresh(); }
            catch (Exception e) { Log.Error("Audio notification", e); }
            return 0;
        }
    }

    [GeneratedComClass]
    private sealed partial class VolumeCallback(AudioService owner) : IAudioEndpointVolumeCallback
    {
        private volatile AudioService? _owner = owner;

        public void Detach() => _owner = null;

        public int OnNotify(nint data)
        {
            try
            {
                // Data is only valid during the call; the worker re-reads both endpoints anyway.
                if (_owner is { } owner) owner.Post(() => owner.ReadVolumes());
            }
            catch (Exception e) { Log.Error("Audio volume notification", e); }
            return 0;
        }
    }
}

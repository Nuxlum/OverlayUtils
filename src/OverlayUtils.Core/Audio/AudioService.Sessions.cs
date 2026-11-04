using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using OverlayUtils.Core.Diagnostics;
using OverlayUtils.Core.Interop;
using OverlayUtils.Core.Platform;

namespace OverlayUtils.Core.Audio;

// Audio sessions of the default output and the per-app mix. Same rules as the
// rest of AudioService: COM only on the audio thread, Windows callbacks only
// queue work, errors logged. Sessions are tracked by their instance id; a
// rescan (session created/expired/disconnected, process exited) diffs the
// enumerator against what is tracked. The mix is written when a program gets
// a new session, when the default output changes and on ApplyMix; never at
// start, so changes made elsewhere stay until the next overlay change.
public sealed partial class AudioService
{
    private enum ApplyMode { None, NewPrograms, All }

    private sealed class TrackedSession(IAudioSessionControl2 control, SessionEvents events, uint processId, string? exe)
    {
        public IAudioSessionControl2 Control { get; } = control;
        public SessionEvents Events { get; } = events;
        public uint ProcessId { get; } = processId;
        /// <summary>Null for sessions that are never listed or mixed (system sounds, our own, unreadable).</summary>
        public string? Exe { get; } = exe;
    }

    /// <summary>A process with sessions: its exe and, when Windows allows it, a handle that reports its exit.</summary>
    private sealed record ProcessWatch(string? Exe, Process? Process);

    // Cached for any thread, guarded by _lock.
    private List<AudioApp> _apps = [];

    // Worker-thread only.
    private IAudioSessionManager2? _sessionManager;
    private SessionNotification? _sessionNotification;
    private readonly Dictionary<string, TrackedSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, ProcessWatch> _processes = [];
    private readonly Dictionary<string, string> _appNames = new(StringComparer.OrdinalIgnoreCase);
    private int _sessionScanQueued;

    /// <summary>Programs with a live session on the default output, one per exe (see <see cref="IAudioView.Apps"/>).</summary>
    public IReadOnlyList<AudioApp> Apps { get { lock (_lock) return _apps; } }

    /// <summary>Re-applies the stored mix of the default output to every live session.</summary>
    public void ApplyMix() => Post(() => ApplyLevels([.. _sessions.Values]));

    // --- worker thread --------------------------------------------------------

    /// <summary>First start (or a config with no channels): the four default channels.</summary>
    private void SeedChannels()
    {
        if (_config.Current.AudioChannels.Count > 0) return;
        try
        {
            _config.Update(c =>
            {
                if (c.AudioChannels.Count == 0) c.AudioChannels = [.. MixRules.DefaultChannels];
            });
        }
        catch (Exception e)
        {
            Log.Error("Saving the default audio channels", e);
        }
    }

    /// <summary>Stores a mix for a device seen for the first time: a copy of the previous device's, else defaults.</summary>
    private void EnsureMix(string deviceId, string? previousDeviceId)
    {
        var mixes = _config.Current.AudioMix;
        if (mixes.ContainsKey(deviceId)) return;
        // Current is a snapshot, so the instance MixFor returns is ours; its Apps are case-insensitive.
        var mix = MixRules.MixFor(mixes, deviceId, previousDeviceId);
        try
        {
            _config.Update(c =>
            {
                if (!c.AudioMix.ContainsKey(deviceId)) c.AudioMix[deviceId] = mix;
            });
        }
        catch (Exception e)
        {
            Log.Error("Saving the mix of a new audio device", e);
        }
    }

    private void BindSessions(string? deviceId, bool applyAll)
    {
        UnbindSessions();
        if (deviceId is null)
        {
            PublishApps();
            return;
        }
        IMMDevice? device = null;
        try
        {
            device = _enumerator!.GetDevice(deviceId);
            _sessionManager = CoreAudio.Wrap<IAudioSessionManager2>(
                device.Activate(CoreAudio.IID_IAudioSessionManager2, Native.CLSCTX_ALL, 0));
            // Windows only sends session notifications once the enumerator has been read: scan, then register,
            // then rescan to pick up a session created in between (applied as new).
            ScanSessions(applyAll ? ApplyMode.All : ApplyMode.None);
            _sessionNotification = new SessionNotification(this);
            _sessionManager.RegisterSessionNotification(_sessionNotification);
            ScheduleSessionScan();
        }
        catch (Exception e)
        {
            Log.Error("Binding audio sessions (per-app mix disabled for this device)", e);
            UnbindSessions();
            PublishApps();
        }
        finally
        {
            CoreAudio.Release(device);
        }
    }

    private void UnbindSessions()
    {
        if (_sessionManager is not null && _sessionNotification is not null)
        {
            try { _sessionManager.UnregisterSessionNotification(_sessionNotification); }
            catch (Exception e) { Log.Error("Unregistering audio session notifications", e); }
        }
        _sessionNotification?.Detach();
        _sessionNotification = null;
        foreach (var id in _sessions.Keys.ToList()) Drop(id);
        PruneProcesses();
        CoreAudio.Release(_sessionManager);
        _sessionManager = null;
    }

    /// <summary>Coalesces bursts of session events into one scan.</summary>
    private void ScheduleSessionScan()
    {
        if (Interlocked.Exchange(ref _sessionScanQueued, 1) == 1) return;
        Post(() =>
        {
            Interlocked.Exchange(ref _sessionScanQueued, 0);
            ScanSessions(ApplyMode.NewPrograms);
        });
    }

    private void ScanSessions(ApplyMode mode)
    {
        if (_sessionManager is null) return;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var added = new List<TrackedSession>();
        var enumerator = CoreAudio.Wrap<IAudioSessionEnumerator>(_sessionManager.GetSessionEnumerator());
        try
        {
            var count = enumerator.GetCount();
            for (var i = 0; i < count; i++)
            {
                IAudioSessionControl2? control = null;
                var kept = false;
                try
                {
                    control = CoreAudio.Wrap<IAudioSessionControl2>(enumerator.GetSession(i));
                    var id = TakeString(control.GetSessionInstanceIdentifier());
                    if (!seen.Add(id) || _sessions.ContainsKey(id)) continue;
                    var processId = control.GetProcessId();
                    var exe = control.IsSystemSoundsSession() == 0 || processId == 0 || processId == Environment.ProcessId
                        ? null
                        : Watch(processId).Exe;
                    var events = new SessionEvents(this);
                    control.RegisterAudioSessionNotification(events);
                    var tracked = new TrackedSession(control, events, processId, exe);
                    _sessions[id] = tracked;
                    kept = true;
                    added.Add(tracked);
                }
                catch (Exception e)
                {
                    Log.Error("Reading an audio session", e);
                }
                finally
                {
                    if (!kept) CoreAudio.Release(control);
                }
            }
        }
        finally
        {
            CoreAudio.Release(enumerator);
        }

        foreach (var id in _sessions.Keys.Where(id => !seen.Contains(id)).ToList()) Drop(id);
        PruneProcesses();
        RecordPrograms(added);

        if (mode == ApplyMode.All)
            ApplyLevels([.. _sessions.Values]);
        else if (mode == ApplyMode.NewPrograms && added.Count > 0)
        {
            // Every session of a program that got a new one, so all its sessions share one level.
            var programs = added.Where(s => s.Exe is not null).Select(s => s.Exe!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            ApplyLevels([.. _sessions.Values.Where(s => s.Exe is not null && programs.Contains(s.Exe))]);
        }
        PublishApps();
    }

    private void Drop(string id)
    {
        if (!_sessions.Remove(id, out var session)) return;
        try { session.Control.UnregisterAudioSessionNotification(session.Events); }
        catch (Exception e) { Log.Error("Unregistering audio session events", e); }
        session.Events.Detach();
        CoreAudio.Release(session.Control);
    }

    /// <summary>The exe of a process (cached per process id) and an exit watch, so ended programs leave the list.</summary>
    private ProcessWatch Watch(uint processId)
    {
        if (_processes.TryGetValue(processId, out var known)) return known;
        Process? process = null;
        try
        {
            process = Process.GetProcessById((int)processId);
            process.EnableRaisingEvents = true; // also keeps a handle, so the id is not reused while watched
            process.Exited += OnProcessExited;
        }
        catch (Exception)
        {
            // Gone already or not accessible (protected): no exit watch; liveness falls back to a query.
            process?.Dispose();
            process = null;
        }
        var watch = new ProcessWatch(ExePath(processId), process);
        _processes[processId] = watch;
        return watch;
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        try { ScheduleSessionScan(); }
        catch (Exception ex) { Log.Error("Audio process exit", ex); }
    }

    private void PruneProcesses()
    {
        var used = _sessions.Values.Select(s => s.ProcessId).ToHashSet();
        foreach (var (processId, watch) in _processes.Where(p => !used.Contains(p.Key)).ToList())
        {
            if (watch.Process is { } process)
            {
                process.Exited -= OnProcessExited;
                process.Dispose();
            }
            _processes.Remove(processId);
        }
    }

    private bool IsAlive(uint processId)
    {
        if (_processes.TryGetValue(processId, out var watch) && watch.Process is { } process)
        {
            try { return !process.HasExited; }
            catch (Exception) { return false; }
        }
        var handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == 0) return false;
        try { return Native.GetExitCodeProcess(handle, out var code) && code == Native.STILL_ACTIVE; }
        finally { Native.CloseHandle(handle); }
    }

    private static string? ExePath(uint processId)
    {
        var handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == 0) return null;
        try
        {
            Span<ushort> buffer = stackalloc ushort[1024];
            var size = (uint)buffer.Length;
            return Native.QueryFullProcessImageName(handle, 0, ref buffer[0], ref size) && size > 0
                ? new string(MemoryMarshal.Cast<ushort, char>(buffer[..(int)size]))
                : null;
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    /// <summary>
    /// Assigns programs seen for the first time a default channel (the stored choice, if any, always wins;
    /// the fullscreen flag comes from the foreground window now, when the session appears).
    /// </summary>
    private void RecordPrograms(List<TrackedSession> added)
    {
        var programs = _config.Current.AudioPrograms;
        var fresh = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in added)
        {
            if (session.Exe is not { } exe || fresh.ContainsKey(exe) || Contains(programs, exe)) continue;
            fresh[exe] = MixRules.ChannelOf(exe, programs, WindowFocus.IsFullscreenForeground(session.ProcessId));
        }
        if (fresh.Count == 0) return;
        try
        {
            _config.Update(c =>
            {
                // Rebuilt case-insensitively: never two keys for the same exe in different casing.
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (exe, channel) in c.AudioPrograms) map.TryAdd(exe, channel);
                foreach (var (exe, channel) in fresh) map.TryAdd(exe, channel);
                c.AudioPrograms = map;
            });
        }
        catch (Exception e)
        {
            Log.Error("Saving the audio programs", e);
        }
    }

    private static bool Contains(Dictionary<string, string> programs, string exe) =>
        programs.ContainsKey(exe) || programs.Keys.Any(k => string.Equals(k, exe, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Writes channel x app to each session via <see cref="MixRules.SessionLevel"/>: the volume product and the
    /// session mute flag (so the Windows mixer shows it muted, and unmuting there restores the level).
    /// </summary>
    private void ApplyLevels(List<TrackedSession> sessions)
    {
        if (_readOnly || _boundOutput is not { } device || sessions.Count == 0) return;
        var config = _config.Current;
        var mix = MixRules.MixFor(config.AudioMix, device, null); // a snapshot: never mutated
        foreach (var session in sessions)
        {
            if (session.Exe is not { } exe) continue;
            try
            {
                var channelId = MixRules.ChannelFor(exe, config.AudioPrograms, config.AudioChannels);
                var channel = mix.Channels.TryGetValue(channelId, out var level) ? level : new MixLevel(1.0, false);
                var app = MixRules.AppLevel(mix, exe);
                var (sessionVolume, muted) = MixRules.SessionLevel(channel, app);
                var volume = (ISimpleAudioVolume)session.Control;
                volume.SetMasterVolume((float)sessionVolume, EventContext);
                volume.SetMute(muted ? 1 : 0, EventContext);
            }
            catch (Exception e)
            {
                Log.Error("Setting an app volume", e);
            }
        }
    }

    /// <summary>Rebuilds the cached app list from live sessions; raises Changed when it differs.</summary>
    private void PublishApps()
    {
        var live = _sessions.Values.Where(s => s.Exe is not null && IsAlive(s.ProcessId) && !IsExpired(s));
        var apps = MixRules.GroupByProgram(live, s => s.Exe!).Select(g => new AudioApp(g.Exe, NameOf(g.Exe))).ToList();
        bool changed;
        lock (_lock)
        {
            changed = !apps.SequenceEqual(_apps);
            if (changed) _apps = apps;
        }
        if (changed) RaiseChanged();
    }

    private static bool IsExpired(TrackedSession session)
    {
        try { return session.Control.GetState() == CoreAudio.AudioSessionState.Expired; }
        catch (Exception) { return true; }
    }

    /// <summary>The exe's file description (e.g. "Discord"), else its file name without extension.</summary>
    private string NameOf(string exe)
    {
        if (_appNames.TryGetValue(exe, out var cached)) return cached;
        string? name = null;
        try { name = FileVersionInfo.GetVersionInfo(exe).FileDescription?.Trim(); }
        catch (Exception) { /* no version resource or unreadable: use the file name */ }
        if (string.IsNullOrEmpty(name)) name = Path.GetFileNameWithoutExtension(exe);
        _appNames[exe] = name;
        return name;
    }

    private static string TakeString(nint pointer)
    {
        try { return Marshal.PtrToStringUni(pointer) ?? ""; }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }

    // --- callbacks from Windows (any thread): only queue work ---------------

    [GeneratedComClass]
    private sealed partial class SessionNotification(AudioService owner) : IAudioSessionNotification
    {
        private volatile AudioService? _owner = owner;

        public void Detach() => _owner = null;

        public int OnSessionCreated(nint newSession)
        {
            // The pointer is only borrowed for the call; the worker finds the session by rescanning.
            try { _owner?.ScheduleSessionScan(); }
            catch (Exception e) { Log.Error("Audio session notification", e); }
            return 0;
        }
    }

    [GeneratedComClass]
    private sealed partial class SessionEvents(AudioService owner) : IAudioSessionEvents
    {
        private volatile AudioService? _owner = owner;

        public void Detach() => _owner = null;

        public int OnDisplayNameChanged(nint newDisplayName, nint eventContext) => 0;
        public int OnIconPathChanged(nint newIconPath, nint eventContext) => 0;
        public int OnSimpleVolumeChanged(float newVolume, int newMute, nint eventContext) => 0;
        public int OnChannelVolumeChanged(uint channelCount, nint newChannelVolumes, uint changedChannel, nint eventContext) => 0;
        public int OnGroupingParamChanged(nint newGroupingParam, nint eventContext) => 0;

        public int OnStateChanged(CoreAudio.AudioSessionState newState) =>
            newState == CoreAudio.AudioSessionState.Expired ? Rescan() : 0;

        public int OnSessionDisconnected(int disconnectReason) => Rescan();

        private int Rescan()
        {
            try { _owner?.ScheduleSessionScan(); }
            catch (Exception e) { Log.Error("Audio session event", e); }
            return 0;
        }
    }
}

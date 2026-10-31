using System.Collections.Concurrent;
using OverlayUtils.Core.Diagnostics;
using OverlayUtils.Core.Interop;

namespace OverlayUtils.Core.Input;

/// <summary>
/// Global hotkeys through RegisterHotKey, on a dedicated thread with its own
/// message loop (hotkeys belong to the thread that registered them).
///
/// RegisterHotKey is used instead of a keyboard hook on purpose: media keys
/// such as Browser Home also produce WM_APPCOMMAND, which hooks cannot block;
/// a registered hotkey makes Windows skip the key's default action entirely.
/// It also lets the receiving process take the foreground.
///
/// <see cref="Pressed"/> is raised on the hotkey thread: marshal to the UI
/// thread before touching UI.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly Thread _thread;
    private readonly ConcurrentQueue<Action> _work = new();
    private readonly ManualResetEventSlim _ready = new();
    private readonly HashSet<int> _registered = [];
    private uint _threadId;

    public HotkeyService()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "Hotkeys" };
        _thread.Start();
        _ready.Wait();
    }

    public event Action<int>? Pressed;

    /// <summary>Registers (or re-registers) a hotkey. Returns false if another program owns it.</summary>
    public Task<bool> RegisterAsync(int id, uint modifiers, uint virtualKey) =>
        Invoke(() =>
        {
            if (_registered.Remove(id)) Native.UnregisterHotKey(0, id);
            var ok = Native.RegisterHotKey(0, id, modifiers | Native.MOD_NOREPEAT, virtualKey);
            if (ok) _registered.Add(id);
            else Log.Error($"Hotkey {id} (vk 0x{virtualKey:X2}) is already used by another program");
            return ok;
        });

    public Task<bool> UnregisterAsync(int id) =>
        Invoke(() => _registered.Remove(id) && Native.UnregisterHotKey(0, id));

    public bool IsRegistered(int id)
    {
        lock (_registered) return _registered.Contains(id);
    }

    private Task<T> Invoke<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Enqueue(() =>
        {
            try
            {
                T result;
                lock (_registered) result = func();
                tcs.SetResult(result);
            }
            catch (Exception e) { tcs.SetException(e); }
        });
        Native.PostThreadMessage(_threadId, Native.WM_APP, 0, 0);
        return tcs.Task;
    }

    private void Loop()
    {
        _threadId = Native.GetCurrentThreadId();
        Native.PeekMessage(out _, 0, 0, 0, Native.PM_NOREMOVE); // creates the message queue
        _ready.Set();

        while (Native.GetMessage(out var msg, 0, 0, 0) > 0)
        {
            if (msg.message == Native.WM_HOTKEY)
            {
                try { Pressed?.Invoke((int)msg.wParam); }
                catch (Exception e) { Log.Error("Hotkey handler", e); }
            }
            else if (msg.message == Native.WM_APP)
            {
                while (_work.TryDequeue(out var action)) action();
            }
        }

        lock (_registered)
        {
            foreach (var id in _registered) Native.UnregisterHotKey(0, id);
            _registered.Clear();
        }
    }

    public void Dispose()
    {
        Native.PostThreadMessage(_threadId, Native.WM_QUIT, 0, 0);
        _thread.Join(TimeSpan.FromSeconds(1));
        _ready.Dispose();
    }
}

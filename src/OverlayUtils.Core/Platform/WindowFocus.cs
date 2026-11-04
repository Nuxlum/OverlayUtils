using System.Runtime.InteropServices;
using OverlayUtils.Core.Input;
using OverlayUtils.Core.Interop;

namespace OverlayUtils.Core.Platform;

/// <summary>Class names of the desktop shell: taskbar, desktop, tray overflow.</summary>
public static class ShellWindows
{
    private static readonly HashSet<string> Classes = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW",
        "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
    };

    public static bool IsShellClass(string className) => Classes.Contains(className);
}

/// <summary>Foreground window queries and the focus hand-over used by the overlay.</summary>
public static class WindowFocus
{
    public static nint Foreground() => Native.GetForegroundWindow();

    /// <summary>The foreground window, or 0 when it is nothing but the shell (desktop, taskbar).</summary>
    public static nint ForegroundApp()
    {
        var hwnd = Foreground();
        if (hwnd == 0) return 0;
        var root = Native.GetAncestor(hwnd, Native.GA_ROOT);
        if (root != 0) hwnd = root;
        return ShellWindows.IsShellClass(ClassName(hwnd)) ? 0 : hwnd;
    }

    /// <summary>
    /// Windows refuses SetForegroundWindow to a process that has not just
    /// received user input (a gamepad does not count). Attaching to the
    /// foreground thread's input queue gets around it; as a last resort an
    /// Alt tap does.
    /// </summary>
    public static void Force(nint hwnd)
    {
        var fg = Foreground();
        var fgThread = fg != 0 ? Native.GetWindowThreadProcessId(fg, out _) : 0;
        var ourThread = Native.GetCurrentThreadId();
        var attached = fgThread != 0 && fgThread != ourThread && Native.AttachThreadInput(ourThread, fgThread, true);
        try
        {
            Native.BringWindowToTop(hwnd);
            Native.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) Native.AttachThreadInput(ourThread, fgThread, false);
        }
        if (Foreground() != hwnd)
        {
            KeySender.Chord(Native.VK_MENU);
            Native.SetForegroundWindow(hwnd);
        }
    }

    public static bool IsAlive(nint hwnd) => hwnd != 0 && Native.IsWindow(hwnd);

    /// <summary>True when the window covers its whole monitor (exclusive or borderless fullscreen).</summary>
    public static bool IsFullscreen(nint hwnd)
    {
        if (hwnd == 0 || !Native.GetWindowRect(hwnd, out var rect)) return false;
        var info = new Native.MONITORINFO { cbSize = (uint)Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST), ref info)) return false;
        var monitor = info.rcMonitor;
        return rect.Left <= monitor.Left && rect.Top <= monitor.Top && rect.Right >= monitor.Right && rect.Bottom >= monitor.Bottom;
    }

    /// <summary>True when the foreground app window belongs to the process and is fullscreen.</summary>
    public static bool IsFullscreenForeground(uint processId)
    {
        var hwnd = ForegroundApp();
        return hwnd != 0 && ProcessOf(hwnd) == processId && IsFullscreen(hwnd);
    }

    public static uint ProcessOf(nint hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }

    public static string ClassName(nint hwnd)
    {
        Span<ushort> buffer = stackalloc ushort[256];
        var length = Native.GetClassName(hwnd, ref buffer[0], buffer.Length);
        return length > 0 ? new string(MemoryMarshal.Cast<ushort, char>(buffer[..length])) : "";
    }
}

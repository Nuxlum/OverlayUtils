using System.Runtime.InteropServices;
using OverlayUtils.Core.Interop;
using OverlayUtils.Core.Overlay;

namespace OverlayUtils.Core.Platform;

/// <summary>Work area and DPI scale of the monitor a window or the cursor is on.</summary>
public static class DisplayArea
{
    public static (RectI Work, double DpiScale) ForWindow(nint hwnd) =>
        Describe(Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST));

    public static (RectI Work, double DpiScale) ForCursor()
    {
        Native.GetCursorPos(out var point);
        return Describe(Native.MonitorFromPoint(point, Native.MONITOR_DEFAULTTONEAREST));
    }

    /// <summary>Full monitor rectangle (not the work area) and DPI scale of the monitor a window is on.</summary>
    public static (RectI Bounds, double DpiScale) MonitorForWindow(nint hwnd) =>
        Bounds(Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST));

    public static (RectI Bounds, double DpiScale) MonitorForCursor()
    {
        Native.GetCursorPos(out var point);
        return Bounds(Native.MonitorFromPoint(point, Native.MONITOR_DEFAULTTONEAREST));
    }

    private static (RectI Bounds, double DpiScale) Bounds(nint monitor)
    {
        var (_, scale) = Describe(monitor);
        var info = new Native.MONITORINFO { cbSize = (uint)Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(monitor, ref info);
        return (new RectI(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom), scale);
    }

    private static (RectI Work, double DpiScale) Describe(nint monitor)
    {
        var info = new Native.MONITORINFO { cbSize = (uint)Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(monitor, ref info);
        var work = new RectI(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom);
        var scale = Native.GetDpiForMonitor(monitor, Native.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0
            ? dpiX / 96.0
            : 1.0;
        return (work, scale);
    }
}

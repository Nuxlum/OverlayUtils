using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using OverlayUtils.Core.Diagnostics;
using OverlayUtils.Core.Interop;
using OverlayUtils.Core.Platform;

namespace OverlayUtils.App.Overlay;

/// <summary>
/// Window-level look of the full-screen overlay done by DWM: tool-window style, dark frame, square corners.
/// No DWM backdrop: the overlay draws its own frosted screenshot (see <see cref="FrostedBackdrop"/>). Every call
/// may fail on older Windows; the window then simply keeps the defaults.
/// </summary>
internal static class WindowEffects
{
    /// <summary>Out of the taskbar and Alt+Tab, dark frame, never rounded (it covers the monitor). Needs the handle.</summary>
    public static void ApplyChrome(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == 0) return;
        try
        {
            // Out of Alt+Tab even if WPF's hidden owner changes, as Python does.
            var style = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, (style | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW);
            // Failures are expected on Windows 10 (no corner preference) and ignored, as in Python.
            SetAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, 1);
            SetAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, Native.DWMWCP_DONOTROUND);
        }
        catch (Exception e)
        {
            Log.Error("Overlay chrome", e);
        }
    }

    /// <summary>The Windows accent in a variant readable on the dark panel, or Python's blue.</summary>
    public static Color Accent()
    {
        var c = AccentPalette.ReadForDarkBackground();
        return Color.FromRgb(c.R, c.G, c.B);
    }

    static bool SetAttribute(nint hwnd, int attribute, int value) =>
        Native.DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int)) == 0;
}

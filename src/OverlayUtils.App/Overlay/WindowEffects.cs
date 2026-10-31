using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using OverlayUtils.Core.Diagnostics;
using OverlayUtils.Core.Interop;
using OverlayUtils.Core.Platform;

namespace OverlayUtils.App.Overlay;

/// <summary>
/// Window-level look of the overlay done by DWM: tool-window style, rounded corners and border
/// (Python _round_window_corners), dark frame, and the Windows 11 acrylic backdrop. Every call may fail
/// on older Windows; the window then simply stays square and solid.
/// </summary>
internal static class WindowEffects
{
    const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    const int AcrylicBuild = 22621;          // first build with DWMWA_SYSTEMBACKDROP_TYPE
    const int BorderColor = 0x003E3E3E;      // Python OV_BORDER as COLORREF (0x00BBGGRR)

    /// <summary>Out of the taskbar and Alt+Tab, rounded corners with a thin border, dark frame. Needs the handle.</summary>
    public static void ApplyChrome(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == 0) return;
        try
        {
            // Out of Alt+Tab even if WPF's hidden owner changes, as Python does.
            var style = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, (style | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW);
            // Failures are expected on Windows 10 (no corners or border colour) and ignored, as in Python.
            SetAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, 1);
            SetAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, Native.DWMWCP_ROUND);
            SetAttribute(hwnd, Native.DWMWA_BORDER_COLOR, BorderColor);
        }
        catch (Exception e)
        {
            Log.Error("Overlay chrome", e);
        }
    }

    /// <summary>
    /// Turns the acrylic backdrop on (frame extended over the whole client area, window and composition
    /// background transparent). Returns false on Windows 10, with "Transparency effects" off, or when a DWM
    /// call fails, with the frame put back as it was; the caller then keeps (or restores) its solid
    /// background. Call it again after every show (DWM drops the acrylic of a hidden window) and after the
    /// transparency setting changed.
    /// </summary>
    public static bool TryApplyAcrylic(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == 0 || HwndSource.FromHwnd(hwnd)?.CompositionTarget is not { } target) return false;
        try
        {
            if (Environment.OSVersion.Version.Build >= AcrylicBuild && TransparencyEnabled())
            {
                var all = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                // Through "none" first: setting the same type again does not make DWM redraw it, and after
                // a hide and show DWM draws only the solid fallback colour until the type changes.
                SetAttribute(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, Native.DWMSBT_NONE);
                if (Native.DwmExtendFrameIntoClientArea(hwnd, ref all) == 0
                    && SetAttribute(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, Native.DWMSBT_TRANSIENTWINDOW))
                {
                    target.BackgroundColor = Colors.Transparent;
                    w.Background = Brushes.Transparent;
                    return true;
                }
            }
        }
        catch (Exception e)
        {
            Log.Error("Overlay acrylic", e);
        }

        // Solid: undo whatever an earlier call (or the failed one above) turned on.
        try
        {
            var none = new Native.MARGINS();
            Native.DwmExtendFrameIntoClientArea(hwnd, ref none);
            if (Environment.OSVersion.Version.Build >= AcrylicBuild)
                SetAttribute(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, Native.DWMSBT_NONE);
        }
        catch (Exception e)
        {
            Log.Error("Overlay acrylic off", e);
        }
        target.BackgroundColor = Colors.Black; // HwndTarget's default; the window background covers it
        return false;
    }

    /// <summary>The Windows accent (light variant, for a dark background), or Python's blue.</summary>
    public static Color Accent() =>
        AccentPalette.Read() is { } c ? Color.FromRgb(c.R, c.G, c.B) : OverlayViewModel.FallbackAccent;

    static bool TransparencyEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            // Missing value: Windows' default, on.
            return key?.GetValue("EnableTransparency") is not int value || value != 0;
        }
        catch (Exception e)
        {
            Log.Error("Overlay transparency setting", e);
            return false;
        }
    }

    static bool SetAttribute(nint hwnd, int attribute, int value) =>
        Native.DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int)) == 0;
}

using Microsoft.Win32;
using OverlayUtils.Core.Diagnostics;

namespace OverlayUtils.Core.Platform;

/// <summary>
/// "Start with Windows" through HKCU\...\Run (no admin rights needed).
///
/// The Python version used an elevated scheduled task because it always ran
/// as admin. Both modes now run as a normal user, so the Run key is enough.
/// The UIAccess exe is launched through ShellExec_RunDLL: Windows grants
/// UIAccess only to processes started via ShellExecute, never via a bare
/// CreateProcess.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(AppPaths.AppName) is string;
        }
    }

    /// <summary>The registered command, or null.</summary>
    public static string? Command
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(AppPaths.AppName) as string;
        }
    }

    public static bool Enable(string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            key.SetValue(AppPaths.AppName, CommandFor(exePath));
            return true;
        }
        catch (Exception e)
        {
            Log.Error("Enabling start with Windows", e);
            return false;
        }
    }

    public static bool Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(AppPaths.AppName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception e)
        {
            Log.Error("Disabling start with Windows", e);
            return false;
        }
    }

    /// <summary>If start-up is on, re-point it at <paramref name="exePath"/> (used when switching modes).</summary>
    public static void RetargetIfEnabled(string exePath)
    {
        if (IsEnabled) Enable(exePath);
    }

    internal static string CommandFor(string exePath)
    {
        if (Path.GetFileName(exePath).Equals(AppPaths.UIAccessExeName, StringComparison.OrdinalIgnoreCase))
        {
            var rundll = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "rundll32.exe");
            return $"\"{rundll}\" shell32.dll,ShellExec_RunDLL \"{exePath}\"";
        }
        return $"\"{exePath}\"";
    }
}

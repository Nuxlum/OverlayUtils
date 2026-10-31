using Microsoft.Win32;
using OverlayUtils.Core.Diagnostics;

namespace OverlayUtils.Core.Platform;

/// <summary>The user's Windows accent colour.</summary>
public static class AccentPalette
{
    private const string AccentKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";

    /// <summary>
    /// Entry 1 (bytes 4-6) of the 8 x RGBA palette; null when the blob is
    /// missing or too short.
    /// </summary>
    public static (byte R, byte G, byte B)? LightVariant(byte[]? palette) =>
        palette is { Length: >= 8 } ? (palette[4], palette[5], palette[6]) : null;

    public static (byte R, byte G, byte B)? Read()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AccentKey);
            return LightVariant(key?.GetValue("AccentPalette") as byte[]);
        }
        catch (Exception e)
        {
            Log.Error("AccentPalette.Read", e);
            return null;
        }
    }
}

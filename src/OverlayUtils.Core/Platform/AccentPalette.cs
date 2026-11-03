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

    /// <summary>Python OV_ACCENT, used when no palette entry is readable on the overlay background.</summary>
    public static readonly (byte R, byte G, byte B) Fallback = (0x4C, 0xC2, 0xFF);

    /// <summary>The overlay panel background (#202020) the accent must stand out on.</summary>
    private static readonly (byte R, byte G, byte B) DarkBackground = (0x20, 0x20, 0x20);

    /// <summary>
    /// The first of entry 1 (light variant) and entry 0 (lightest) with a WCAG contrast of at least
    /// 4.5:1 against the overlay background, else <see cref="Fallback"/>: a grey or dark accent would
    /// make the selection hard to see.
    /// </summary>
    public static (byte R, byte G, byte B) ForDarkBackground(byte[]? palette)
    {
        if (palette is { Length: >= 8 })
        {
            foreach (var i in new[] { 1, 0 })
            {
                var c = (palette[i * 4], palette[i * 4 + 1], palette[i * 4 + 2]);
                if (Contrast(c, DarkBackground) >= 4.5) return c;
            }
        }
        return Fallback;
    }

    /// <summary>WCAG 2 contrast ratio, from 1 to 21.</summary>
    public static double Contrast((byte R, byte G, byte B) a, (byte R, byte G, byte B) b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance((byte R, byte G, byte B) c) =>
        0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);

    private static double Linear(byte v)
    {
        var s = v / 255.0;
        return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    public static (byte R, byte G, byte B)? Read() => LightVariant(ReadPalette());

    /// <summary>The accent to draw on the dark overlay (see <see cref="ForDarkBackground"/>).</summary>
    public static (byte R, byte G, byte B) ReadForDarkBackground() => ForDarkBackground(ReadPalette());

    private static byte[]? ReadPalette()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AccentKey);
            return key?.GetValue("AccentPalette") as byte[];
        }
        catch (Exception e)
        {
            Log.Error("AccentPalette.Read", e);
            return null;
        }
    }
}

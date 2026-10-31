using OverlayUtils.Core.Config;

namespace OverlayUtils.Core.Overlay;

/// <summary>Integer rectangle in physical pixels (Right/Bottom exclusive, like RECT).</summary>
public readonly record struct RectI(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>Size and position of the overlay panel. Pure maths, no UI.</summary>
public static class OverlayLayout
{
    /// <summary>Panel size at 100% DPI and normal size, in device-independent pixels.</summary>
    public const int Width = 420;
    public const int Height = 720;

    /// <summary>Gap to the work-area edges at 100% DPI.</summary>
    public const int Margin = 36;

    /// <summary>Scale applied when the panel is "large" (TV distance).</summary>
    public const double LargeFactor = 1.35;

    /// <summary>
    /// Size multiplier for a setting: "large" and "normal" are fixed, anything
    /// else behaves as "auto" (large only on the TV profile).
    /// </summary>
    public static double SizeFactor(string sizeSetting, string currentProfile, string tvProfile)
    {
        if (sizeSetting == OverlaySizes.Large) return LargeFactor;
        if (sizeSetting == OverlaySizes.Normal) return 1.0;
        return tvProfile != "" && currentProfile == tvProfile ? LargeFactor : 1.0;
    }

    /// <summary>
    /// Panel rectangle in physical pixels, docked to the left or right edge of
    /// <paramref name="work"/>. The margin scales with DPI only; the height is
    /// clamped so the panel keeps the margin at the top and bottom.
    /// </summary>
    public static RectI Place(RectI work, double dpiScale, double sizeFactor, string side)
    {
        double scale = dpiScale * sizeFactor;
        int margin = Round(Margin * dpiScale);
        int w = Round(Width * scale);
        int h = Math.Min(Round(Height * scale), work.Height - 2 * margin);
        int x = side == OverlaySides.Right ? work.Right - margin - w : work.Left + margin;
        int y = work.Top + margin;
        return new RectI(x, y, x + w, y + h);
    }

    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
}

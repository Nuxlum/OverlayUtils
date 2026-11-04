using OverlayUtils.Core.Config;

namespace OverlayUtils.Core.Overlay;

/// <summary>Integer rectangle in physical pixels (Right/Bottom exclusive, like RECT).</summary>
public readonly record struct RectI(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>Frame measures of the full-monitor overlay, in DIPs.</summary>
public readonly record struct OverlayFrame(double RailWidth, double ColumnWidth, double Gutter, int CoverColumns);

/// <summary>Size factor and frame maths of the overlay. Pure maths, no UI.</summary>
public static class OverlayLayout
{
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

    /// <summary>Minimum width of a game cover at normal size, in DIPs.</summary>
    public const double CoverMinWidth = 150;

    /// <summary>Rail, content column, gutter and cover columns for a full-monitor overlay of the given width (DIPs).</summary>
    public static OverlayFrame Frame(double widthDip, double sizeFactor)
    {
        var rail = 0.07 * widthDip;
        var column = (sizeFactor > 1 ? 0.40 : 0.30) * widthDip;
        var gutter = 48 * sizeFactor;
        var covers = Math.Clamp((int)((widthDip - rail - 2 * gutter) / (CoverMinWidth * sizeFactor)), 4, 12);
        return new OverlayFrame(rail, column, gutter, covers);
    }
}

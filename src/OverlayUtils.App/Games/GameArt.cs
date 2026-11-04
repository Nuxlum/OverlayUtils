using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OverlayUtils.App.Games;

/// <summary>
/// Cover art and icons for the Games tab, decoded small (a tile is ~125 DIP wide) and frozen so a worker can load
/// them. Kept for the session by path; a file that cannot be read is remembered as missing.
/// </summary>
internal static class GameArt
{
    const int CoverWidth = 240; // pixels: sharp at the Large size on a 150 % screen
    const int IconSize = 96;

    static readonly ConcurrentDictionary<string, ImageSource?> Covers = new(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<string, ImageSource?> Icons = new(StringComparer.OrdinalIgnoreCase);

    public static bool TryCached(string? cover, string? icon, out ImageSource? coverImage, out ImageSource? iconImage)
    {
        coverImage = null;
        iconImage = null;
        if (cover is not null && !Covers.TryGetValue(cover, out coverImage)) return false;
        if (coverImage is not null || icon is null) return true;
        return Icons.TryGetValue(icon, out iconImage);
    }

    public static ImageSource? Cover(string path) => Covers.GetOrAdd(path, p => Decode(p, CoverWidth));

    /// <summary>An image file (an Xbox logo) or the icon of an exe.</summary>
    public static ImageSource? Icon(string path) => Icons.GetOrAdd(path, p =>
        p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? ExeIcon(p) : Decode(p, IconSize));

    static BitmapImage? Decode(string path, int width)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad; // the file is not kept open
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = width;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null; // a broken or unsupported image just shows the placeholder (decoders throw many types)
        }
    }

    static BitmapSource? ExeIcon(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractIcon(path, 0, IconSize);
            if (icon is null) return null;
            var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception)
        {
            return null; // no icon resource: the placeholder glyph is shown
        }
    }
}

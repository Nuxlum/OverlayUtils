using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OverlayUtils.App.Games;

/// <summary>
/// Cover art and icons for the Games tab, decoded small (a tile is ~125 DIP wide) and frozen so a worker can load
/// them. Covers are kept in a small most-recently-used cache (the grid only holds the art of the rows near the
/// view); a cover that fails to decode is tried again next time. Icons are tiny and kept for the session.
/// </summary>
internal static class GameArt
{
    const int CoverWidth = 200;   // pixels: ~125 DIP at 150 % with room for the Large size
    const int IconSize = 96;
    const int CoverCapacity = 120; // ~240 KB each: about 30 MB at most

    static readonly object CoverLock = new();
    static readonly Dictionary<string, LinkedListNode<(string Path, ImageSource Image)>> Covers = new(StringComparer.OrdinalIgnoreCase);
    static readonly LinkedList<(string Path, ImageSource Image)> CoverOrder = new(); // most recent first
    static readonly ConcurrentDictionary<string, ImageSource?> Icons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The art of a tile if it needs no decoding (cover cached, or no cover and the icon cached).</summary>
    public static bool TryCached(string? cover, string? icon, out ImageSource? coverImage, out ImageSource? iconImage)
    {
        coverImage = null;
        iconImage = null;
        if (cover is not null && (coverImage = CachedCover(cover)) is not null) return true;
        if (cover is not null) return false;
        return icon is null || Icons.TryGetValue(icon, out iconImage);
    }

    public static ImageSource? Cover(string path)
    {
        if (CachedCover(path) is { } cached) return cached;
        if (Decode(path, CoverWidth) is not { } image) return null;
        lock (CoverLock)
        {
            if (!Covers.ContainsKey(path))
            {
                Covers[path] = CoverOrder.AddFirst((path, image));
                while (CoverOrder.Count > CoverCapacity && CoverOrder.Last is { } oldest)
                {
                    CoverOrder.RemoveLast();
                    Covers.Remove(oldest.Value.Path);
                }
            }
        }
        return image;
    }

    /// <summary>An image file (an Xbox logo) or the icon of an exe.</summary>
    public static ImageSource? Icon(string path) => Icons.GetOrAdd(path, p =>
        p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? ExeIcon(p) : Decode(p, IconSize));

    static ImageSource? CachedCover(string path)
    {
        lock (CoverLock)
        {
            if (!Covers.TryGetValue(path, out var node)) return null;
            CoverOrder.Remove(node);
            CoverOrder.AddFirst(node);
            return node.Value.Image;
        }
    }

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

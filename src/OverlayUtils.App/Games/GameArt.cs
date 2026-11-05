using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OverlayUtils.Core.Diagnostics;

namespace OverlayUtils.App.Games;

/// <summary>
/// Cover art and icons for the Games tab, decoded small (a tile is ~125 DIP wide) and frozen so a worker can load
/// them. Covers are kept in a small most-recently-used cache (the grid only holds the art of the rows near the
/// view); a cover that fails to decode is tried again next time. Each cover keeps its ambient colour (the Games
/// backdrop tint), taken once at decode time. Icons are tiny and kept for the session.
/// </summary>
internal static class GameArt
{
    const int CoverWidth = 200;   // pixels: ~125 DIP at 150 % with room for the Large size
    const int IconSize = 96;
    const int CoverCapacity = 120; // ~240 KB each: about 30 MB at most

    static readonly object CoverLock = new();
    const int AmbientSamples = 8;  // the cover is averaged from an 8x8 copy
    const double AmbientMaxLuma = 120; // 0..255: dark enough that white text over the tint stays readable

    static readonly Dictionary<string, LinkedListNode<(string Path, CoverArt Art)>> Covers = new(StringComparer.OrdinalIgnoreCase);
    static readonly LinkedList<(string Path, CoverArt Art)> CoverOrder = new(); // most recent first
    static readonly ConcurrentDictionary<string, ImageSource?> Icons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The art of a tile if it needs no decoding (cover cached, or no cover and the icon cached).</summary>
    public static bool TryCached(string? cover, string? icon, out CoverArt? coverImage, out ImageSource? iconImage)
    {
        coverImage = null;
        iconImage = null;
        if (cover is not null && (coverImage = CachedCover(cover)) is not null) return true;
        if (cover is not null) return false;
        return icon is null || Icons.TryGetValue(icon, out iconImage);
    }

    /// <summary>The cover and its ambient colour, decoded now if not cached (any thread).</summary>
    public static CoverArt? Cover(string path)
    {
        if (CachedCover(path) is { } cached) return cached;
        if (Decode(path, CoverWidth) is not { } image) return null;
        var art = new CoverArt(image, AmbientOf(image));
        lock (CoverLock)
        {
            if (!Covers.ContainsKey(path))
            {
                Covers[path] = CoverOrder.AddFirst((path, art));
                while (CoverOrder.Count > CoverCapacity && CoverOrder.Last is { } oldest)
                {
                    CoverOrder.RemoveLast();
                    Covers.Remove(oldest.Value.Path);
                }
            }
        }
        return art;
    }

    /// <summary>An image file (an Xbox logo) or the icon of an exe.</summary>
    public static ImageSource? Icon(string path) => Icons.GetOrAdd(path, p =>
        p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? ExeIcon(p) : Decode(p, IconSize));

    static CoverArt? CachedCover(string path)
    {
        lock (CoverLock)
        {
            if (!Covers.TryGetValue(path, out var node)) return null;
            CoverOrder.Remove(node);
            CoverOrder.AddFirst(node);
            return node.Value.Art;
        }
    }

    /// <summary>
    /// Dominant colour of a cover: an 8x8 copy averaged with more weight on saturated pixels (so a black or white
    /// frame does not wash the colour out), then darkened to <see cref="AmbientMaxLuma"/>. Null if it cannot be read.
    /// </summary>
    static int _ambientErrorLogged;

    static Color? AmbientOf(BitmapSource image)
    {
        try
        {
            if (image.PixelWidth <= 0 || image.PixelHeight <= 0) return null;
            var small = new FormatConvertedBitmap(
                new TransformedBitmap(image, new ScaleTransform((double)AmbientSamples / image.PixelWidth,
                    (double)AmbientSamples / image.PixelHeight)),
                PixelFormats.Bgra32, null, 0);
            int w = small.PixelWidth, h = small.PixelHeight;
            var pixels = new byte[w * h * 4];
            small.CopyPixels(pixels, w * 4, 0);
            double r = 0, g = 0, b = 0, total = 0;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                int pb = pixels[i], pg = pixels[i + 1], pr = pixels[i + 2];
                var saturation = (Math.Max(pr, Math.Max(pg, pb)) - Math.Min(pr, Math.Min(pg, pb))) / 255.0;
                var weight = (0.1 + saturation * saturation * 4) * (pixels[i + 3] / 255.0);
                r += pr * weight;
                g += pg * weight;
                b += pb * weight;
                total += weight;
            }
            if (total <= 0) return null;
            r /= total;
            g /= total;
            b /= total;
            var luma = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            if (luma > AmbientMaxLuma)
            {
                var k = AmbientMaxLuma / luma;
                r *= k;
                g *= k;
                b *= k;
            }
            return Color.FromRgb((byte)Math.Round(r), (byte)Math.Round(g), (byte)Math.Round(b));
        }
        catch (Exception e)
        {
            // No tint for this cover: the backdrop stays plain. Logged once: a cause shared by every cover would
            // otherwise fill the log on each Games page.
            if (Interlocked.Exchange(ref _ambientErrorLogged, 1) == 0) Log.Error("Cover ambient colour", e);
            return null;
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

/// <summary>A decoded cover (frozen) and its ambient colour for the Games backdrop tint (null: none).</summary>
internal sealed record CoverArt(ImageSource Image, Color? Ambient);

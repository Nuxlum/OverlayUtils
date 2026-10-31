using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace OverlayUtils.App.Tray;

/// <summary>
/// Draws the tray icon: a rounded square in the colour of the loaded monitor
/// profile with its label ("TV", or an initial), "M" when there are no
/// profiles. Same look as the Python version.
/// </summary>
internal static partial class TrayIconRenderer
{
    public static readonly ((byte R, byte G, byte B) Color, string Label) DefaultStyle = ((0, 120, 215), "M");

    /// <summary>Returns a new Icon; dispose the previous one with <see cref="Destroy"/>.</summary>
    public static Icon Create(((byte R, byte G, byte B) Color, string Label)? style, int size)
    {
        var ((r, g, b), label) = style ?? DefaultStyle;
        var s = size / 64f;
        using var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.Clear(Color.Transparent);

            using var path = RoundedRect(new RectangleF(2 * s, 2 * s, size - 4 * s, size - 4 * s), 14 * s);
            using var fill = new SolidBrush(Color.FromArgb(r, g, b));
            graphics.FillPath(fill, path);

            var fontSize = (label.Length == 1 ? 34 : 26) * s;
            using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            graphics.DrawString(label, font, Brushes.White, new RectangleF(0, -1 * s, size, size), format);
        }
        var handle = bitmap.GetHicon();
        return Icon.FromHandle(handle);
    }

    /// <summary>Icon.FromHandle does not own the handle: free it explicitly.</summary>
    public static void Destroy(Icon? icon)
    {
        if (icon is null) return;
        DestroyIcon(icon.Handle);
        icon.Dispose();
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint handle);
}

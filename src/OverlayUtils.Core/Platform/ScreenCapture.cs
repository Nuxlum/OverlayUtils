using System.Runtime.InteropServices;
using OverlayUtils.Core.Interop;
using OverlayUtils.Core.Overlay;

namespace OverlayUtils.Core.Platform;

/// <summary>A screen grab: 32bpp BGRA, top-down. Lives in memory only.</summary>
public sealed record CapturedScreen(byte[] Bgra, int Width, int Height, int Stride);

public static class ScreenCapture
{
    /// <summary>Copies the pixels of <paramref name="bounds"/> (physical screen pixels). Null on any failure.</summary>
    public static CapturedScreen? Capture(RectI bounds)
    {
        int w = bounds.Width, h = bounds.Height;
        if (w <= 0 || h <= 0) return null;
        nint screen = 0, mem = 0, dib = 0, old = 0;
        try
        {
            screen = Native.GetDC(0);
            if (screen == 0) return null;
            mem = Native.CreateCompatibleDC(screen);
            if (mem == 0) return null;
            var bmi = new Native.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(), biWidth = w, biHeight = -h,
                biPlanes = 1, biBitCount = 32, biCompression = 0,
            };
            dib = Native.CreateDIBSection(screen, in bmi, 0, out var bits, 0, 0);
            if (dib == 0 || bits == 0) return null;
            old = Native.SelectObject(mem, dib);
            if (!Native.BitBlt(mem, 0, 0, w, h, screen, bounds.Left, bounds.Top, Native.SRCCOPY)) return null;
            var data = new byte[checked(w * h * 4)];
            Marshal.Copy(bits, data, 0, data.Length);
            return new CapturedScreen(data, w, h, w * 4);
        }
        catch (Exception e)
        {
            Diagnostics.Log.Error("ScreenCapture", e);
            return null;
        }
        finally
        {
            if (mem != 0 && old != 0) Native.SelectObject(mem, old);
            if (dib != 0) Native.DeleteObject(dib);
            if (mem != 0) Native.DeleteDC(mem);
            if (screen != 0) Native.ReleaseDC(0, screen);
        }
    }
}

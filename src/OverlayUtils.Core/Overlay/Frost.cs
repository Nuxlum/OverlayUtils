using OverlayUtils.Core.Config;

namespace OverlayUtils.Core.Overlay;

/// <summary>How the frosted backdrop is drawn; every value derives from the Appearance settings.</summary>
public sealed record FrostSettings(
    int BlurRadius, double Saturation, double Brightness, double FrostOpacity, double GrainOpacity,
    IReadOnlyList<byte> GradientAlphas)
{
    static readonly byte[] BaseAlphas = [0xE0, 0xB3, 0x59, 0x33];

    public static FrostSettings FromConfig(AppConfig c)
    {
        double dim = 0.5 + c.OverlayDim / 100.0;
        return new FrostSettings(
            BlurRadius: (int)Math.Round(c.OverlayBlur / 100.0 * 64),
            Saturation: 1.3,
            Brightness: 1.08,
            FrostOpacity: c.OverlayFrost / 100.0 * 0.20,
            GrainOpacity: 0.05,
            GradientAlphas: BaseAlphas.Select(a => (byte)Math.Clamp((int)Math.Round(a * dim), 0, 255)).ToArray());
    }
}

/// <summary>A processed backdrop: opaque Bgr32 pixels, top-down, stride Width * 4.</summary>
public sealed record FrostImage(byte[] Bgr32, int Width, int Height);

/// <summary>Turns a screen capture into the frosted backdrop: downscale, blur, saturate. Pure and thread-safe.</summary>
public static class Frost
{
    public static FrostImage? Process(byte[] bgra, int width, int height, int stride, FrostSettings s)
    {
        int qw = width / 4, qh = height / 4;
        if (qw <= 0 || qh <= 0 || stride < width * 4 || bgra.Length < (height - 1) * stride + width * 4) return null;
        int n = qw * qh;
        var ch = new[] { new float[n], new float[n], new float[n] }; // B, G, R

        for (int y = 0; y < qh; y++)
            for (int x = 0; x < qw; x++)
            {
                int b = 0, g = 0, r = 0;
                for (int dy = 0; dy < 4; dy++)
                {
                    int row = (y * 4 + dy) * stride + x * 16;
                    for (int dx = 0; dx < 4; dx++)
                    {
                        int i = row + dx * 4;
                        b += bgra[i]; g += bgra[i + 1]; r += bgra[i + 2];
                    }
                }
                int o = y * qw + x;
                ch[0][o] = b / 16f; ch[1][o] = g / 16f; ch[2][o] = r / 16f;
            }

        int rad = s.BlurRadius / 4;
        if (rad > 0)
        {
            var tmp = new float[n];
            foreach (var plane in ch)
                for (int pass = 0; pass < 3; pass++)
                {
                    Box(plane, tmp, qw, qh, rad, horizontal: true);
                    Box(tmp, plane, qw, qh, rad, horizontal: false);
                }
        }

        var output = new byte[n * 4];
        float sat = (float)s.Saturation, lift = (float)s.Brightness;
        for (int i = 0; i < n; i++)
        {
            float b = ch[0][i], g = ch[1][i], r = ch[2][i];
            float luma = 0.2126f * r + 0.7152f * g + 0.0722f * b;
            output[i * 4] = To8((luma + (b - luma) * sat) * lift);
            output[i * 4 + 1] = To8((luma + (g - luma) * sat) * lift);
            output[i * 4 + 2] = To8((luma + (r - luma) * sat) * lift);
            output[i * 4 + 3] = 255;
        }
        return new FrostImage(output, qw, qh);
    }

    /// <summary>
    /// True when every sampled pixel (every 8th, both ways) is near black: a capture of exclusive-fullscreen or
    /// protected content, which would only show as a grey frost. Also true for an empty or inconsistent buffer.
    /// </summary>
    public static bool LooksBlack(byte[] bgra, int width, int height, int stride, int threshold = 6)
    {
        if (width <= 0 || height <= 0 || stride < width * 4 || bgra.Length < (height - 1) * stride + width * 4) return true;
        for (int y = 0; y < height; y += 8)
            for (int x = 0; x < width; x += 8)
            {
                int i = y * stride + x * 4;
                if (bgra[i] > threshold || bgra[i + 1] > threshold || bgra[i + 2] > threshold) return false;
            }
        return true;
    }

    static byte To8(float v) => (byte)Math.Clamp((int)(v + 0.5f), 0, 255);

    /// <summary>One edge-clamped box-blur pass from <paramref name="a"/> into <paramref name="dst"/>, along rows or columns.</summary>
    static void Box(float[] a, float[] dst, int w, int h, int r, bool horizontal)
    {
        int len = horizontal ? w : h, lines = horizontal ? h : w;
        int step = horizontal ? 1 : w, lineStep = horizontal ? w : 1;
        float inv = 1f / (2 * r + 1);
        for (int line = 0; line < lines; line++)
        {
            int b = line * lineStep;
            float sum = a[b] * r;
            for (int j = 0; j <= r; j++) sum += a[b + Math.Min(j, len - 1) * step];
            for (int i = 0; i < len; i++)
            {
                dst[b + i * step] = sum * inv;
                sum += a[b + Math.Min(i + r + 1, len - 1) * step] - a[b + Math.Max(i - r, 0) * step];
            }
        }
    }
}

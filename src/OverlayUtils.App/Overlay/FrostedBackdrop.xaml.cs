using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using OverlayUtils.Core.Overlay;

namespace OverlayUtils.App.Overlay;

/// <summary>
/// Draws the frosted backdrop (layers in the XAML). It only shows what it is given: the controller captures the
/// monitor, processes it on a worker and hands the image over; <see cref="Apply"/> sets the layers that need no
/// re-processing (frost, grain, gradient). UI thread only.
/// </summary>
internal sealed partial class FrostedBackdrop : UserControl
{
    const int GrainSize = 128;
    static readonly Duration FadeIn = TimeSpan.FromMilliseconds(150);
    static readonly Color FallbackDim = Color.FromRgb(0x08, 0x0A, 0x10); // FrameBg of the theme
    static readonly double[] DimOffsets = [0, 0.38, 0.72, 1];
    static readonly Lazy<ImageBrush> GrainBrush = new(MakeGrain);

    public FrostedBackdrop()
    {
        InitializeComponent();
        Grain.Fill = GrainBrush.Value;
    }

    /// <summary>True when <paramref name="source"/> is the bare backdrop (not the frame or a card above it).</summary>
    public bool IsSurface(object? source) => ReferenceEquals(source, Surface);

    /// <summary>Frost, grain and gradient of the Appearance settings; instant, no re-processing.</summary>
    public void Apply(FrostSettings settings)
    {
        FrostLayer.Opacity = settings.FrostOpacity;
        Grain.Opacity = settings.GrainOpacity;
        var dim = (TryFindResource("FrameBg") as SolidColorBrush)?.Color ?? FallbackDim;
        var gradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (var i = 0; i < DimOffsets.Length && i < settings.GradientAlphas.Count; i++)
            gradient.GradientStops.Add(new GradientStop(Color.FromArgb(settings.GradientAlphas[i], dim.R, dim.G, dim.B), DimOffsets[i]));
        gradient.Freeze();
        Dim.Fill = gradient;
    }

    /// <summary>Shows a processed image (frozen); fades it in when <paramref name="fade"/>, else at once.</summary>
    public void ShowImage(BitmapSource image, bool fade)
    {
        Picture.Source = image;
        Shot.BeginAnimation(OpacityProperty, null);
        if (fade)
            Shot.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, FadeIn)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        else Shot.Opacity = 1;
    }

    /// <summary>Back to the plain background, the image released.</summary>
    public void Clear()
    {
        Shot.BeginAnimation(OpacityProperty, null);
        Shot.Opacity = 0;
        Picture.Source = null;
    }

    /// <summary>A frozen Bgr32 bitmap of a processed backdrop; any thread.</summary>
    public static BitmapSource ToBitmap(FrostImage image)
    {
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgr32, null, image.Bgr32, image.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>128 px tiled grey noise, the same every time (fixed seed).</summary>
    static ImageBrush MakeGrain()
    {
        var random = new Random(12345);
        var pixels = new byte[GrainSize * GrainSize * 4];
        for (var i = 0; i < GrainSize * GrainSize; i++)
        {
            var v = (byte)random.Next(256);
            pixels[i * 4] = v;
            pixels[i * 4 + 1] = v;
            pixels[i * 4 + 2] = v;
            pixels[i * 4 + 3] = 255;
        }
        var bitmap = BitmapSource.Create(GrainSize, GrainSize, 96, 96, PixelFormats.Bgra32, null, pixels, GrainSize * 4);
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap)
        {
            TileMode = TileMode.Tile,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, GrainSize, GrainSize),
            Stretch = Stretch.Fill,
        };
        brush.Freeze();
        return brush;
    }
}

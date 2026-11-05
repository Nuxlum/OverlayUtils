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
    // The first image of an open fades in as long as the window's own open fade of the backdrop.
    static readonly Duration FadeIn = TimeSpan.FromMilliseconds(180);
    static readonly Duration TintFade = TimeSpan.FromMilliseconds(150);
    static readonly IEasingFunction EaseOut = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    static readonly Color FallbackDim = Color.FromRgb(0x08, 0x0A, 0x10); // FrameBg of the theme
    static readonly double[] DimOffsets = [0, 0.38, 0.72, 1];
    const double DownDarkFrom = 0.55; // top to bottom (Games): fully dark from about the shelf down
    const double FallbackTintOpacity = 0.5;
    static readonly Lazy<ImageBrush> GrainBrush = new(MakeGrain);

    // The tint is one brush whose colour is changed in place, so a selection change allocates nothing.
    readonly GradientStop _tintCore = new(Colors.Transparent, 0);
    readonly GradientStop _tintEdge = new(Colors.Transparent, 1);
    Brush? _across;
    Brush? _down;
    bool _vertical;
    double? _tintOpacity;
    int _tintVersion; // bumped on every tint change: a fade-out that ends after a newer tint does not hide it

    public FrostedBackdrop()
    {
        InitializeComponent();
        Grain.Fill = GrainBrush.Value;
        Tint.Fill = new RadialGradientBrush(new GradientStopCollection { _tintCore, _tintEdge })
        {
            Center = new Point(0.78, 0.28), GradientOrigin = new Point(0.78, 0.28), RadiusX = 0.62, RadiusY = 0.8,
        };
    }

    /// <summary>True when <paramref name="source"/> is the bare backdrop (not the frame or a card above it).</summary>
    public bool IsSurface(object? source) => ReferenceEquals(source, Surface);

    /// <summary>Frost, grain and gradient of the Appearance settings; instant, no re-processing.</summary>
    public void Apply(FrostSettings settings)
    {
        FrostLayer.Opacity = settings.FrostOpacity;
        Grain.Opacity = settings.GrainOpacity;
        var dim = (TryFindResource("FrameBg") as SolidColorBrush)?.Color ?? FallbackDim;
        Color Dimmed(byte alpha) => Color.FromArgb(alpha, dim.R, dim.G, dim.B);
        var across = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        var alphas = settings.GradientAlphas;
        for (var i = 0; i < DimOffsets.Length && i < alphas.Count; i++)
            across.GradientStops.Add(new GradientStop(Dimmed(alphas[i]), DimOffsets[i]));
        across.Freeze();
        // Games: lighter at the top where the hero sits over the game, dark under the shelf; same Darkness setting.
        var down = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        if (alphas.Count >= 3)
        {
            down.GradientStops.Add(new GradientStop(Dimmed((byte)((alphas[1] + alphas[2]) / 2)), 0));
            down.GradientStops.Add(new GradientStop(Dimmed(alphas[0]), DownDarkFrom));
            down.GradientStops.Add(new GradientStop(Dimmed(alphas[0]), 1));
        }
        down.Freeze();
        _across = across;
        _down = down;
        Dim.Fill = _vertical ? _down : _across;
    }

    /// <summary>Top-to-bottom gradient (the Games page) instead of the left-to-right one.</summary>
    public void SetVertical(bool vertical)
    {
        _vertical = vertical;
        if (_across is not null) Dim.Fill = vertical ? _down : _across;
    }

    /// <summary>
    /// Washes the backdrop with <paramref name="color"/> from the top right (the selected cover's colour on Games);
    /// null removes it. With <paramref name="fade"/> the colour cross-fades in 150 ms from the one shown (a tint
    /// appearing fades in from clear, one removed fades out, then the layer collapses); else it switches at once.
    /// The stops always hold the final colour, so a change during a fade simply starts from where it is.
    /// </summary>
    public void ShowTint(Color? color, bool fade)
    {
        var version = ++_tintVersion;
        var shown = Tint.Visibility == Visibility.Visible;
        if (color is not { } c)
        {
            if (!fade || !shown)
            {
                SetStop(_tintCore, _tintCore.Color, null);
                SetStop(_tintEdge, _tintEdge.Color, null);
                Tint.Visibility = Visibility.Collapsed;
                return;
            }
            var core = _tintCore.Color;
            var edge = _tintEdge.Color;
            SetStop(_tintEdge, Color.FromArgb(0, edge.R, edge.G, edge.B), TintFade);
            SetStop(_tintCore, Color.FromArgb(0, core.R, core.G, core.B), TintFade, () =>
            {
                if (version == _tintVersion) Tint.Visibility = Visibility.Collapsed;
            });
            return;
        }
        _tintOpacity ??= TryFindResource("GamesTintOpacity") as double?;
        var alpha = (byte)Math.Round(Math.Clamp(_tintOpacity ?? FallbackTintOpacity, 0, 1) * 255);
        if (fade && !shown)
        {
            // From clear in the same colour, so it does not pass through grey.
            SetStop(_tintCore, Color.FromArgb(0, c.R, c.G, c.B), null);
            SetStop(_tintEdge, Color.FromArgb(0, c.R, c.G, c.B), null);
        }
        SetStop(_tintCore, Color.FromArgb(alpha, c.R, c.G, c.B), fade ? TintFade : null);
        SetStop(_tintEdge, Color.FromArgb(0, c.R, c.G, c.B), fade ? TintFade : null);
        Tint.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Sets the stop to <paramref name="to"/> (its base value) and, with a duration, animates there from the colour
    /// shown now; <paramref name="done"/> runs when that animation ends.
    /// </summary>
    static void SetStop(GradientStop stop, Color to, Duration? duration, Action? done = null)
    {
        var from = stop.Color; // the animated colour, if a fade is running
        stop.BeginAnimation(GradientStop.ColorProperty, null);
        stop.Color = to;
        if (duration is not { } d || from == to)
        {
            done?.Invoke();
            return;
        }
        var clock = new ColorAnimation(from, to, d) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop }.CreateClock();
        if (done is not null) clock.Completed += (_, _) => done();
        stop.ApplyAnimationClock(GradientStop.ColorProperty, clock);
    }

    /// <summary>Shows a processed image (frozen); fades it in when <paramref name="fade"/>, else at once.</summary>
    public void ShowImage(BitmapSource image, bool fade)
    {
        Picture.Source = image;
        Shot.BeginAnimation(OpacityProperty, null);
        if (fade)
            Shot.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, FadeIn) { EasingFunction = EaseOut });
        else Shot.Opacity = 1;
    }

    /// <summary>Back to the plain background, the image released.</summary>
    public void Clear()
    {
        Shot.BeginAnimation(OpacityProperty, null);
        Shot.Opacity = 0;
        Picture.Source = null;
    }

    static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
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

using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using OverlayUtils.Core.Diagnostics;
using OverlayUtils.Core.Interop;
using OverlayUtils.Core.Localization;
using OverlayUtils.Core.Overlay;
using OverlayUtils.Core.Platform;

namespace OverlayUtils.App.Overlay;

/// <summary>
/// The volume / microphone indicator of the pad shortcuts (overlay closed). Never takes the focus, lets clicks
/// through, sits above the taskbar area of the foreground window's monitor and hides itself after
/// <see cref="Duration"/>. UI thread only.
/// </summary>
internal sealed partial class VolumeIndicator : Window
{
    static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(1500);
    const double BottomGap = 72; // DIPs above the bottom of the work area, clear of the Windows volume flyout's spot

    readonly DispatcherTimer _hideTimer;
    nint _hwnd;

    public VolumeIndicator()
    {
        InitializeComponent();
        _hideTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = Duration };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            try { Hide(); }
            catch (Exception e) { Log.Error("Volume indicator hide", e); }
        };
        SourceInitialized += (_, _) => ApplyStyles();
    }

    /// <summary>True while it is on screen, so audio changes can update it.</summary>
    public bool IsShowing => IsVisible;

    /// <summary>Master volume: level icon, bar (grey when muted) and percent, or "Muted".</summary>
    public void SetVolume(double level, bool muted)
    {
        level = Math.Clamp(level, 0, 1);
        IconText.Text = Glyphs.VolumeIcon(level, muted);
        Bar.Visibility = Visibility.Visible;
        Caption.Visibility = Visibility.Collapsed;
        FillColumn.Width = new GridLength(level, GridUnitType.Star);
        RestColumn.Width = new GridLength(1 - level, GridUnitType.Star);
        Fill.Background = muted ? (Brush)FindResource("Subtle") : new SolidColorBrush(WindowEffects.Accent());
        Percent.Text = muted ? Loc.T("Overlay.Audio.Muted") : $"{Math.Round(level * 100)}%";
        Percent.Visibility = Visibility.Visible;
    }

    /// <summary>Microphone mute: mic icon and a caption.</summary>
    public void SetMic(bool muted)
    {
        IconText.Text = muted ? Glyphs.MicOff : Glyphs.Mic;
        Bar.Visibility = Visibility.Collapsed;
        Caption.Visibility = Visibility.Visible;
        Caption.Text = Loc.T(muted ? "Overlay.Audio.MicMuted" : "Overlay.Audio.MicOn");
        Percent.Visibility = Visibility.Collapsed;
    }

    /// <summary>Shows (or keeps showing) it on the foreground window's monitor and restarts the hide timer.</summary>
    public void Flash()
    {
        try
        {
            var foreground = WindowFocus.Foreground();
            var (work, scale) = foreground != 0 ? DisplayArea.ForWindow(foreground) : DisplayArea.ForCursor();
            if (!IsVisible)
            {
                Place(work, scale); // while hidden, so a DPI change happens before the first frame
                Show();
            }
            Place(work, scale);
            _hideTimer.Stop();
            _hideTimer.Start();
        }
        catch (Exception e)
        {
            Log.Error("Volume indicator", e);
        }
    }

    void Place(RectI work, double scale)
    {
        if (_hwnd == 0) _hwnd = new WindowInteropHelper(this).EnsureHandle();
        var w = (int)Math.Round(Width * scale);
        var h = (int)Math.Round(Height * scale);
        var x = work.Left + (work.Width - w) / 2;
        var y = work.Bottom - h - (int)Math.Round(BottomGap * scale);
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
    }

    /// <summary>Tool window (no taskbar / Alt+Tab), never activated, click-through (WPF already made it layered).</summary>
    void ApplyStyles()
    {
        try
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var style = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE,
                (style | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT) & ~Native.WS_EX_APPWINDOW);
        }
        catch (Exception e)
        {
            Log.Error("Volume indicator styles", e);
        }
    }

    public void Dismiss()
    {
        _hideTimer.Stop();
        try { Close(); }
        catch (Exception e) { Log.Error("Volume indicator close", e); }
    }
}

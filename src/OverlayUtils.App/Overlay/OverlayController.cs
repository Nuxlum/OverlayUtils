using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using OverlayUtils.Core.Config;
using OverlayUtils.Core.Diagnostics;
using OverlayUtils.Core.Interop;
using OverlayUtils.Core.Localization;
using OverlayUtils.Core.Monitors;
using OverlayUtils.Core.Overlay;
using OverlayUtils.Core.Platform;
using Application = System.Windows.Application;

namespace OverlayUtils.App.Overlay;

/// <summary>
/// Shows and hides the overlay (Python _Overlay.show / hide / _watch_focus) and runs what the navigator returns.
/// The window is created once and only shown and hidden. Lives on the UI thread; <see cref="Toggle"/> and
/// <see cref="HasPreviousWindow"/> may be called from any thread.
/// </summary>
internal sealed class OverlayController : IDisposable
{
    static readonly TimeSpan ActionDelay = TimeSpan.FromMilliseconds(150);   // Python OVERLAY_ACTION_DELAY
    static readonly TimeSpan FocusCheck = TimeSpan.FromMilliseconds(150);    // Python OVERLAY_FOCUS_CHECK_MS
    static readonly TimeSpan ClockTick = TimeSpan.FromSeconds(30);           // Python OVERLAY_TICK_MS
    static readonly TimeSpan PendingCheck = TimeSpan.FromMilliseconds(250);

    readonly Application _app;
    readonly ConfigStore _config;
    readonly MonitorProfiles _monitors;
    readonly OverlayContent _content;
    readonly OverlayNavigator _navigator;
    readonly OverlayViewModel _vm = new();
    readonly OverlayWindow _window = new();
    readonly nint _hwnd;
    readonly uint _ownPid = (uint)Environment.ProcessId;
    readonly DispatcherTimer _focusTimer;
    readonly DispatcherTimer _clockTimer;
    readonly DispatcherTimer _pendingTimer;

    volatile bool _open;
    nint _prev;            // foreground app when the overlay opened; kept after hiding for the action that follows
    bool _hadFocus;
    RectI _placement;
    bool _disposed;

    public OverlayController(Application app, ConfigStore config, MonitorProfiles monitors, OverlayContent content)
    {
        _app = app;
        _config = config;
        _monitors = monitors;
        _content = content;
        _navigator = new OverlayNavigator(content.Tabs, content.Settings, content.Info);

        _window.Bind(_vm);
        _window.CommandIssued += OnCommand;
        _window.RowClicked += OnRowClicked;
        _window.Closing += OnClosing;

        _hwnd = new WindowInteropHelper(_window).EnsureHandle();
        // Out of Alt+Tab even if WPF's hidden owner changes, as Python does.
        var style = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, (style | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

        _focusTimer = Timer(FocusCheck, WatchFocus);
        _clockTimer = Timer(ClockTick, () => Render(refreshSelection: _navigator.Page != OverlayPage.Info));
        _pendingTimer = Timer(PendingCheck, CheckPending);

        _config.Changed += OnConfigChanged;
        Loc.LanguageChanged += OnLanguageChanged;
    }

    public bool IsOpen => _open;

    /// <summary>False when the overlay was opened over the desktop or taskbar (or that window has gone).</summary>
    public bool HasPreviousWindow => WindowFocus.IsAlive(_prev);

    /// <summary>The overlay's window handle, e.g. to own a dialog opened from it.</summary>
    public nint Handle => _hwnd;

    public void Toggle() => OnUi(() =>
    {
        if (_open) Hide();
        else Show();
    });

    /// <summary>Gives the foreground back to the overlay after one of our dialogs closed (Python does the same).</summary>
    public void Refocus()
    {
        if (_open) WindowFocus.Force(_hwnd);
    }

    void Show()
    {
        if (_disposed) return;
        try
        {
            var cfg = _config.Current;
            _prev = WindowFocus.ForegroundApp();
            var (work, dpiScale) = _prev != 0 ? DisplayArea.ForWindow(_prev) : DisplayArea.ForCursor();

            _content.ResetTransient();
            _navigator.Reset(cfg.HiddenTabs);
            var sizeFactor = OverlayLayout.SizeFactor(cfg.OverlaySize, _monitors.Current, _monitors.ForRole(MonitorRole.Tv));
            _vm.SizeFactor = sizeFactor;
            if (AccentPalette.Read() is { } accent) _vm.AccentBrush = Frozen(Color.FromRgb(accent.R, accent.G, accent.B));
            Render(refreshSelection: false);

            // Placed while still hidden, so a DPI change on the target monitor happens before the first frame.
            _placement = OverlayLayout.Place(work, dpiScale, sizeFactor, cfg.OverlaySide);
            _window.Width = _placement.Width / dpiScale;
            _window.Height = _placement.Height / dpiScale;
            Place();

            _window.Show();
            Place(); // Show must not have moved it
            _open = true;
            WindowFocus.Force(_hwnd);
            _window.Activate();
            _window.Focus();

            _hadFocus = false;
            _focusTimer.Start();
            _clockTimer.Start();
        }
        catch (Exception e)
        {
            Log.Error("Overlay show", e);
        }
    }

    public void Hide(bool restoreFocus = true)
    {
        if (!_app.Dispatcher.CheckAccess())
        {
            _app.Dispatcher.BeginInvoke(() => Hide(restoreFocus));
            return;
        }
        if (!_open) return;
        try
        {
            // Focus first, then hide: hidden first, Windows activates another window and refuses the hand-back.
            if (restoreFocus && WindowFocus.IsAlive(_prev)) WindowFocus.Force(_prev);
            _open = false;
            _focusTimer.Stop();
            _clockTimer.Stop();
            _pendingTimer.Stop(); // a pending confirm is dropped by Reset on the next open
            _window.Hide();
        }
        catch (Exception e)
        {
            Log.Error("Overlay hide", e);
        }
    }

    /// <summary>Applies <see cref="_placement"/> in physical pixels, topmost, without activating.</summary>
    void Place()
    {
        var r = _placement;
        if (r.Width <= 0 || r.Height <= 0) return;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, r.Left, r.Top, r.Width, r.Height,
            Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
    }

    nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        // WPF resizes to its suggested rectangle on a DPI change; put the panel back on the Place result after.
        if (msg == Native.WM_DPICHANGED)
            _app.Dispatcher.BeginInvoke(DispatcherPriority.Send, Place);
        return 0;
    }

    void WatchFocus()
    {
        // Python _watch_focus: close when another window takes the foreground, but only after we had it once.
        var fg = WindowFocus.Foreground();
        if (fg != 0 && WindowFocus.ProcessOf(fg) == _ownPid) _hadFocus = true;
        else if (_hadFocus) Hide(restoreFocus: false); // focus stays with the window that was clicked
    }

    void CheckPending()
    {
        if (_navigator.ExpirePending()) Render(refreshSelection: false);
        if (!HasPending()) _pendingTimer.Stop();
    }

    bool HasPending()
    {
        var items = _navigator.Items;
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Kind != ItemKind.Segmented) continue;
            var count = items[i].Options().Count;
            for (var j = 0; j < count; j++)
                if (_navigator.IsPending(i, j)) return true;
        }
        return false;
    }

    // --- commands -------------------------------------------------------------

    void OnCommand(OverlayCommand command)
    {
        if (_open) Apply(_navigator.Handle(command));
    }

    void OnRowClicked(int index, int? option)
    {
        if (_open) Apply(_navigator.Click(index, option));
    }

    void Apply(NavResult result)
    {
        switch (result.Effect)
        {
            case NavEffect.Redraw:
                Render(refreshSelection: false);
                break;
            case NavEffect.Hide:
                Hide();
                break;
            case NavEffect.RunHere:
                if (result.Action is { } here)
                {
                    try { here(); }
                    catch (Exception e) { Log.Error("Overlay action", e); }
                }
                if (_open) Render(refreshSelection: true);
                break;
            case NavEffect.HideAndRun:
                Hide();
                if (result.Action is { } later)
                    _ = Task.Run(async () =>
                    {
                        // After focus is back in the previous window (Python _run_overlay_action).
                        await Task.Delay(ActionDelay);
                        try { later(); }
                        catch (Exception e) { Log.Error("Overlay action", e); }
                    });
                break;
        }
        if (_open && !_pendingTimer.IsEnabled && HasPending()) _pendingTimer.Start();
    }

    void Render(bool refreshSelection)
    {
        try
        {
            if (refreshSelection) _navigator.Refresh();
            var profile = _monitors.List().Count > 0 ? _monitors.Current : "";
            (byte, byte, byte)? color = profile.Length > 0 ? _monitors.Style(profile).Color : null;
            _vm.Refresh(_navigator, DateTime.Now.ToString("HH:mm"), color, profile);
        }
        catch (Exception e)
        {
            Log.Error("Overlay render", e);
        }
    }

    void OnConfigChanged(AppConfig config) => OnUi(() =>
    {
        if (_open) Render(refreshSelection: true);
    });

    void OnLanguageChanged() => OnUi(() =>
    {
        if (_open) Render(refreshSelection: true);
    });

    void OnClosing(object? sender, CancelEventArgs e)
    {
        // Created once for the whole session: never destroyed while the app runs (Alt+F4 is already swallowed).
        if (_disposed) return;
        e.Cancel = true;
        Hide();
    }

    // --- helpers --------------------------------------------------------------

    DispatcherTimer Timer(TimeSpan interval, Action tick)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, _app.Dispatcher) { Interval = interval };
        timer.Tick += (_, _) =>
        {
            try { tick(); }
            catch (Exception e) { Log.Error("Overlay timer", e); }
        };
        return timer;
    }

    void OnUi(Action action)
    {
        if (_app.Dispatcher.CheckAccess()) action();
        else _app.Dispatcher.BeginInvoke(action);
    }

    static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _config.Changed -= OnConfigChanged;
        Loc.LanguageChanged -= OnLanguageChanged;
        _focusTimer.Stop();
        _clockTimer.Stop();
        _pendingTimer.Stop();
        try { _window.Close(); }
        catch (Exception e) { Log.Error("Overlay close", e); }
    }
}

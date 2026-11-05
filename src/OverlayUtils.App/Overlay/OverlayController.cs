using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using OverlayUtils.App.Audio;
using OverlayUtils.Core;
using OverlayUtils.Core.Config;
using OverlayUtils.Core.Diagnostics;
using OverlayUtils.Core.Games;
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
/// <see cref="PreviousWindowIsForeground"/> may be called from any thread.
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
    int _renderQueued;
    bool _padConnected;
    int? _battery;
    nint _prev;            // foreground app when the overlay opened; kept after hiding for the action that follows
    bool _hadFocus;
    RectI _placement;
    bool _disposed;

    // The game that was in front when the overlay opened (Home eyebrow). Found off the UI thread; the version drops a
    // result that arrives after the overlay closed or opened again.
    int _gameLookup;
    GameEntry? _currentGame;
    DateTime _currentStartUtc;

    // Frosted backdrop: the monitor captured at open, kept while open so the Appearance sliders can re-process it.
    CapturedScreen? _capture;
    int _frostVersion;      // bumped on every new processing and on hide; a stale result is dropped
    int _frostBlur = -1;    // blur radius of the image shown (or being made), -1 when none
    bool _fadePending;      // no image shown yet since the open

    // The page drawn last (a different one is a page change, which the window animates) and the direction of the
    // command being handled (-1 LT / a rail icon above, +1 RT / below, 0 anything else).
    (OverlayPage Page, string Tab, string Sub) _drawnPage;
    int _pageDirection;

    public OverlayController(Application app, ConfigStore config, MonitorProfiles monitors, OverlayContent content)
    {
        _app = app;
        _config = config;
        _monitors = monitors;
        _content = content;
        _navigator = new OverlayNavigator(content.Tabs, content.Settings, content.Info, media: content.Media);

        _vm.Sources = new StageSources(
            content.OutputCapPercent,
            () =>
            {
                var current = monitors.Current;
                return monitors.List().Select(p => new MonitorVm(p, p == current)).ToList();
            },
            () => (content.Media as MediaSession)?.Art)
        {
            GameFilters = content.GamesFilters,
            GameFilter = () => content.GamesFilter,
        };
        _window.Bind(_vm);
        _window.CommandIssued += OnCommand;
        _window.RowClicked += OnRowClicked;
        _window.RowHovered += index => { if (_open && _navigator.Hover(index)) Render(); };
        _window.RailClicked += index =>
        {
            if (_open) ApplyPaged(Math.Sign(index - CurrentTabIndex()), _navigator.ClickTab(index));
        };
        _window.SettingsClicked += () => OnCommand(OverlayCommand.Settings);
        _window.BackdropClicked += () => Hide();
        _window.StepClicked += (index, direction) => { if (_open) Apply(_navigator.ClickStep(index, direction)); };
        _window.LevelClicked += (index, fraction) => { if (_open) Apply(_navigator.ClickLevel(index, fraction)); };
        _window.MediaClicked += (index, button) => { if (_open) Apply(_navigator.ClickMedia(index, button)); };
        _window.TileHovered += id => { if (_open && _navigator.HoverTile(_navigator.IndexOfTile(id))) Render(); };
        _window.TileClicked += id =>
        {
            if (!_open) return;
            var index = _navigator.IndexOfTile(id);
            if (index >= 0) Apply(_navigator.ClickTile(index)); // gone since it was drawn: nothing starts
        };
        _window.FilterClicked += OnFilterClicked;
        _window.PageLeft += () => { if (_open) Render(); };
        _window.Closing += OnClosing;

        _hwnd = new WindowInteropHelper(_window).EnsureHandle(); // the window applies its chrome and backdrop here
        UpdateAccent();
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

        _focusTimer = Timer(FocusCheck, WatchFocus);
        _clockTimer = Timer(ClockTick, Render);
        _pendingTimer = Timer(PendingCheck, CheckPending);

        _config.Changed += OnConfigChanged;
        Loc.LanguageChanged += OnLanguageChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public bool IsOpen => _open;

    /// <summary>Pad battery percent (UI thread), shown in the status corner when SDL reports one.</summary>
    public int? BatteryPercent { get; private set; }

    /// <summary>Raised on the UI thread when the overlay opens (true) or hides (false).</summary>
    public event Action<bool>? OpenChanged;

    /// <summary>Runs a command as if the window had issued it (pad input). Any thread.</summary>
    public void Command(OverlayCommand command) => OnUi(() => OnCommand(command));

    /// <summary>
    /// Something the rows show changed (audio devices, volume, media session). Any thread; coalesced, so a burst of
    /// events renders once.
    /// </summary>
    public void Invalidate()
    {
        if (!_open || Interlocked.Exchange(ref _renderQueued, 1) == 1) return;
        _app.Dispatcher.BeginInvoke(() =>
        {
            Volatile.Write(ref _renderQueued, 0);
            if (_open) Render();
        });
    }

    /// <summary>Pad connected (footer shows pad buttons) and battery level for the header. Any thread.</summary>
    public void SetPadState(bool padConnected, int? batteryLevel, int? batteryPercent) => OnUi(() =>
    {
        _padConnected = padConnected;
        _battery = batteryLevel;
        BatteryPercent = batteryPercent;
        if (_open) Render();
    });

    /// <summary>
    /// True when the window that was in front when the overlay opened still exists and is the foreground app now;
    /// false when the overlay was opened over the desktop or taskbar, that window has gone, or another took over.
    /// </summary>
    public bool PreviousWindowIsForeground
    {
        get
        {
            var prev = _prev;
            return prev != 0 && WindowFocus.ForegroundApp() == prev && WindowFocus.IsAlive(prev);
        }
    }

    /// <summary>The overlay's window handle, e.g. to own a dialog opened from it.</summary>
    public nint Handle => _hwnd;

    public void Toggle() => OnUi(() =>
    {
        if (_open) Hide();
        else Show();
    });

    /// <summary>
    /// Toggle decided by an earlier state: closes when the overlay was open at <paramref name="wasOpen"/> time (even
    /// if the focus watcher hid it since), opens otherwise. Used by the tray icon, whose press already steals focus.
    /// </summary>
    public void Toggle(bool wasOpen) => OnUi(() =>
    {
        if (wasOpen) Hide();
        else if (!_open) Show();
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
            // Reads the motion setting; true when the close fade of the last open still ran (the window never hid).
            var resumed = _window.BeginOpen();
            var foreground = WindowFocus.ForegroundApp();
            if (foreground != _hwnd) _prev = foreground; // ours only when the hand-back of that close failed
            FindCurrentGame(_prev);
            var (bounds, dpiScale) = _prev != 0 ? DisplayArea.MonitorForWindow(_prev) : DisplayArea.MonitorForCursor();

            // While our window is still hidden, so the screenshot is of the game (about 25 ms, memory only). A resumed
            // open keeps the backdrop on screen: a capture now would be of the overlay itself.
            if (resumed) KeepBackdrop(cfg);
            else CaptureBackdrop(cfg, bounds);

            _content.ResetTransient();
            RunCancel(_navigator.Reset(cfg.HiddenTabs));
            _drawnPage = PageKey(); // the first render is not a page change
            _pageDirection = 0;
            UpdateAccent();

            // Placed while still hidden, so a DPI change on the target monitor happens before the first frame.
            Layout(cfg, bounds, dpiScale);
            Render();

            _window.PrepareOpening(resumed);
            _window.ResetPointer();
            _window.Show();
            Place(); // Show must not have moved it
            _open = true;
            RaiseOpenChanged(true);
            WindowFocus.Force(_hwnd);
            _window.Activate();
            _window.Focus();
            // Dark at once; the frosted image fades in when the worker has it.
            ProcessBackdrop(FrostSettings.FromConfig(cfg));

            _hadFocus = false;
            _focusTimer.Start();
            _clockTimer.Start();
        }
        catch (Exception e)
        {
            Log.Error("Overlay show", e);
            // A failed open must not keep the screenshot (or take a late image) until the next one.
            _capture = null;
            _frostVersion++;
        }
    }

    /// <summary>
    /// Looks up, in the background, which library game owns <paramref name="window"/> (process image path match) and
    /// when its process started; the Home eyebrow shows it with the play time. No match or any failure: app name.
    /// </summary>
    void FindCurrentGame(nint window)
    {
        var version = ++_gameLookup;
        _currentGame = null;
        if (window == 0) return;
        var games = _content.KnownGames; // read here, on the UI thread
        _ = Task.Run(() =>
        {
            try
            {
                if (ForegroundProcess.Describe(window) is not { } process) return;
                if (CurrentGame.Match(process.ExePath, games) is not { } game) return;
                OnUi(() =>
                {
                    if (version != _gameLookup || !_open) return; // closed or reopened meanwhile
                    _currentGame = game;
                    _currentStartUtc = process.StartUtc;
                    Render();
                });
            }
            catch (Exception e)
            {
                Log.Error("Overlay current game", e);
            }
        });
    }

    /// <summary>Home: "title · play time" of the game that was in front; elsewhere, and without a match, the app name.</summary>
    string EyebrowText()
    {
        if (_currentGame is { } game && _navigator.Page == OverlayPage.Tabs && _navigator.CurrentTab.Id == OverlayContent.HomeTabId)
            return Loc.T("Overlay.Eyebrow.Game", game.Title, CurrentGame.PlayTime(DateTime.UtcNow - _currentStartUtc));
        return AppPaths.AppName;
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
            _gameLookup++; // a lookup still running is stale now
            RaiseOpenChanged(false);
            _focusTimer.Stop();
            _clockTimer.Stop();
            _pendingTimer.Stop(); // a pending confirm is dropped by Reset on the next open
            // A picked-up list entry must not stay moved but unsaved: put it back now.
            if (_navigator.Holding) RunCancel(_navigator.Reset(_config.Current.HiddenTabs));
            // The window takes no input from now on and the game has the focus already; the frame fades out
            // (120 ms) and then the window hides, or it hides at once without motion.
            _window.BeginClose();
        }
        catch (Exception e)
        {
            Log.Error("Overlay hide", e);
            try { _window.HideNow(); }
            catch (Exception ex) { Log.Error("Overlay hide", ex); }
        }
        finally
        {
            DropBackdrop();
        }
    }

    /// <summary>
    /// Size factor, frame (rail, column, cover columns) and placement for the current settings on the given monitor
    /// (full bounds, physical pixels), applied to the window.
    /// </summary>
    void Layout(AppConfig cfg, RectI bounds, double dpiScale)
    {
        var sizeFactor = OverlayLayout.SizeFactor(cfg.OverlaySize, _monitors.Current, _monitors.ForRole(MonitorRole.Tv));
        var frame = OverlayLayout.Frame(bounds.Width / dpiScale, sizeFactor);
        _vm.SizeFactor = sizeFactor;
        _vm.Frame = frame;
        _content.GameColumns = frame.CoverColumns; // the navigator's grid moves follow the columns drawn
        _placement = bounds; // the whole monitor, taskbar included
        _window.Width = _placement.Width / dpiScale;
        _window.Height = _placement.Height / dpiScale;
        Place();
    }

    /// <summary>
    /// Re-applies size and frame while open (Settings rows, or a monitor profile switch that changed the size factor
    /// or the display layout), on the monitor the window is on now.
    /// </summary>
    void Relayout(AppConfig cfg)
    {
        try
        {
            var (bounds, dpiScale) = DisplayArea.MonitorForWindow(_hwnd);
            Layout(cfg, bounds, dpiScale);
        }
        catch (Exception e)
        {
            Log.Error("Overlay relayout", e);
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
        // WPF resizes to its suggested rectangle on a DPI change; lay the frame out again for the new DPI (its width
        // in DIPs changed) and put the window back on the whole monitor.
        if (msg == Native.WM_DPICHANGED)
            _app.Dispatcher.BeginInvoke(DispatcherPriority.Send, () => Relayout(_config.Current));
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
        if (_navigator.ExpirePending()) Render();
        if (!HasPending()) _pendingTimer.Stop();
    }

    bool HasPending() => _navigator.HasPending;

    // --- commands -------------------------------------------------------------

    void RaiseOpenChanged(bool open)
    {
        try { OpenChanged?.Invoke(open); }
        catch (Exception e) { Log.Error("Overlay open changed", e); }
    }

    void OnCommand(OverlayCommand command)
    {
        if (!_open) return;
        var direction = command switch
        {
            OverlayCommand.PrevTab => -1,
            OverlayCommand.NextTab => 1,
            _ => 0,
        };
        ApplyPaged(direction, _navigator.Handle(command));
    }

    /// <summary>
    /// <see cref="Apply"/> for a command that may change the page: a page change it causes slides that way (LT / RT,
    /// rail clicks). The navigator has handled it already; only the drawing reads the direction.
    /// </summary>
    void ApplyPaged(int direction, NavResult result)
    {
        _pageDirection = direction;
        try { Apply(result); }
        finally { _pageDirection = 0; }
    }

    /// <summary>Index of the current tab among the visible ones (where a rail click moves from).</summary>
    int CurrentTabIndex()
    {
        var tabs = _navigator.VisibleTabs;
        var current = _navigator.CurrentTab.Id;
        for (var i = 0; i < tabs.Count; i++)
            if (tabs[i].Id == current) return i;
        return 0;
    }

    (OverlayPage Page, string Tab, string Sub) PageKey() =>
        (_navigator.Page, _navigator.CurrentTab.Id, _navigator.Page == OverlayPage.Sub ? _navigator.SubTitle : "");

    void OnRowClicked(int index, int? option)
    {
        if (_open) Apply(_navigator.Click(index, option));
    }

    /// <summary>
    /// A source pill of the Games page: that filter at once (not a run of Y cycles), under the same keep-the-game rule
    /// as Y (<see cref="OverlayNavigator.ApplyFilter"/>), and not while a list entry is held.
    /// </summary>
    void OnFilterClicked(GameSource? source)
    {
        if (_open) Apply(_navigator.ApplyFilter(() => _content.SetGamesFilter(source)));
    }

    void Apply(NavResult result)
    {
        switch (result.Effect)
        {
            case NavEffect.Redraw:
                Render();
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
                if (_open) Render();
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

    void Render()
    {
        try
        {
            // Always: a row that became hidden must not stay selected until the next clock tick.
            RunCancel(_navigator.Refresh());
            var page = PageKey();
            if (page != _drawnPage)
            {
                _drawnPage = page;
                if (_window.AnimatePage(_pageDirection))
                {
                    // The old page slides out first (90 ms); the rail already shows the new one. The page is drawn
                    // on PageLeft, or by the next render if a command comes first.
                    _vm.RefreshRail(_navigator);
                    return;
                }
            }
            _vm.Eyebrow = EyebrowText();
            _vm.Refresh(_navigator, DateTime.Now.ToString("HH:mm"), _content.OutputDevice, _padConnected, _battery,
                BatteryPercent);
            _window.PageDrawn(); // a page that was sliding out: the new one comes in
        }
        catch (Exception e)
        {
            Log.Error("Overlay render", e);
        }
    }

    /// <summary>Runs the Cancel of a picked-up entry the navigator dropped (Reset / Refresh).</summary>
    static void RunCancel(Action? cancel)
    {
        if (cancel is null) return;
        try { cancel(); }
        catch (Exception e) { Log.Error("Overlay reorder cancel", e); }
    }

    void OnConfigChanged(AppConfig config) => OnUi(() =>
    {
        if (!_open) return;
        Relayout(config); // overlay size or monitor profile may have changed
        UpdateBackdrop(config); // the Appearance sliders preview live
        RunCancel(_navigator.SetHiddenTabs(config.HiddenTabs)); // a tab hidden or shown in Settings follows at once
        Render();
    });

    void OnLanguageChanged() => OnUi(() =>
    {
        if (_open) Render();
    });

    void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // An accent change arrives as General (ImmersiveColorSet).
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color)) return;
        OnUi(() =>
        {
            try
            {
                UpdateAccent();
            }
            catch (Exception ex)
            {
                Log.Error("Overlay preference change", ex);
            }
        });
    }

    // --- frosted backdrop ------------------------------------------------------

    /// <summary>
    /// Takes the screenshot for this open (UI thread, before the window shows) and resets the backdrop to the plain
    /// background with the current frost, grain and gradient. A failed capture leaves the plain background.
    /// </summary>
    void CaptureBackdrop(AppConfig cfg, RectI bounds)
    {
        _frostVersion++;
        _frostBlur = -1;
        _capture = null;
        _fadePending = true;
        try
        {
            _window.Backdrop.Clear();
            _window.Backdrop.Apply(FrostSettings.FromConfig(cfg));
            _capture = ScreenCapture.Capture(bounds);
        }
        catch (Exception e)
        {
            Log.Error("Overlay backdrop capture", e);
        }
    }

    /// <summary>
    /// Re-open during the close fade: the backdrop on screen stays (no capture of our own window); only the settings
    /// are applied again. Without a capture the Appearance sliders do not re-blur it during this open.
    /// </summary>
    void KeepBackdrop(AppConfig cfg)
    {
        _frostVersion++;
        _frostBlur = -1;
        _capture = null;
        _fadePending = false;
        try { _window.Backdrop.Apply(FrostSettings.FromConfig(cfg)); }
        catch (Exception e) { Log.Error("Overlay backdrop", e); }
    }

    /// <summary>
    /// Blurs the kept capture on a worker and shows it when done, if the overlay is still open and nothing newer was
    /// asked meanwhile. A black capture (exclusive fullscreen, protected content) is not shown.
    /// </summary>
    void ProcessBackdrop(FrostSettings settings)
    {
        if (_capture is not { } capture) return;
        var version = ++_frostVersion;
        _frostBlur = settings.BlurRadius;
        _ = Task.Run(() =>
        {
            try
            {
                if (Frost.LooksBlack(capture.Bgra, capture.Width, capture.Height, capture.Stride)) return;
                if (Frost.Process(capture.Bgra, capture.Width, capture.Height, capture.Stride, settings) is not { } image) return;
                var bitmap = FrostedBackdrop.ToBitmap(image);
                _app.Dispatcher.BeginInvoke(() =>
                {
                    if (!_open || version != _frostVersion) return;
                    // Only the first image of an open fades in; a re-processed one replaces it at once.
                    var fade = _fadePending && _window.MotionEnabled;
                    _fadePending = false;
                    try { _window.Backdrop.ShowImage(bitmap, fade); }
                    catch (Exception e) { Log.Error("Overlay backdrop show", e); }
                });
            }
            catch (Exception e)
            {
                Log.Error("Overlay backdrop", e);
            }
        });
    }

    /// <summary>Settings changed while open: frost, grain and darkness at once; a new blur re-processes the capture.</summary>
    void UpdateBackdrop(AppConfig config)
    {
        try
        {
            var settings = FrostSettings.FromConfig(config);
            _window.Backdrop.Apply(settings);
            if (_capture is not null && settings.BlurRadius != _frostBlur) ProcessBackdrop(settings);
        }
        catch (Exception e)
        {
            Log.Error("Overlay backdrop update", e);
        }
    }

    /// <summary>
    /// On hide: the capture is released and a result still on its way is dropped. The image stays on screen while the
    /// close fade runs; the window clears it when it hides.
    /// </summary>
    void DropBackdrop()
    {
        _frostVersion++;
        _frostBlur = -1;
        _capture = null;
    }

    void UpdateAccent()
    {
        var accent = WindowEffects.Accent();
        if ((_vm.AccentBrush as SolidColorBrush)?.Color != accent) _vm.AccentBrush = Frozen(accent);
    }

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
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _focusTimer.Stop();
        _clockTimer.Stop();
        _pendingTimer.Stop();
        try { _window.Close(); }
        catch (Exception e) { Log.Error("Overlay close", e); }
    }
}

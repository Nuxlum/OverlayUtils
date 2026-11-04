using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using OverlayUtils.App.Audio;
using OverlayUtils.App.Games;
using OverlayUtils.App.Overlay;
using OverlayUtils.Core;
using OverlayUtils.Core.Audio;
using OverlayUtils.Core.Config;
using OverlayUtils.Core.Diagnostics;
using OverlayUtils.Core.Input;
using OverlayUtils.Core.Interop;
using OverlayUtils.Core.Legacy;
using OverlayUtils.Core.Localization;
using OverlayUtils.Core.Monitors;
using OverlayUtils.Core.Overlay;
using OverlayUtils.Core.Platform;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace OverlayUtils.App.Tray;

/// <summary>
/// Owns the tray icon and its menu, and wires stage-1 features together:
/// monitor profiles, Home key remapping, global hotkeys, language, start-up,
/// full mode, audio devices and the media session, and the overlay (Shift+F1 or a left
/// click on the icon), whose host it is. Lives on the WPF UI thread; hotkey, pad and
/// audio callbacks are marshalled here.
/// </summary>
internal sealed class TrayController : IOverlayHost, IDisposable
{
    private const int HotkeyHome = 1;
    private const int HotkeyOverlay = 2;
    private const int HotkeyDesk = 3;
    private const int HotkeyTv = 4;

    private readonly Application _app;
    private readonly ConfigStore _config;
    private readonly LauncherKind _kind;
    private readonly MonitorSwitcherLocator _locator;
    private readonly MonitorProfiles _monitors;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();
    private HotkeyService? _hotkeys;
    private OverlayController? _overlay;
    private PadService? _pads;
    private AudioService? _audio;
    private MediaSession? _media;
    private GameService? _games;
    private VolumeIndicator? _indicator; // UI thread; created on first use
    private bool _indicatorShowsMic;     // UI thread
    private System.Drawing.Icon? _currentIcon;
    private bool _busy; // a mode switch is in progress
    private bool? _overlayOpenAtPress; // left button pressed on the tray icon: was the overlay open then
    private NameDialog? _nameDialog;   // UI thread; the open name prompt, if any
    private bool _choosingProgram;     // UI thread; the program picker is open

    public TrayController(Application app, ConfigStore config, LauncherKind kind)
    {
        _app = app;
        _config = config;
        _kind = kind;
        _locator = new MonitorSwitcherLocator(config);
        _monitors = new MonitorProfiles(config, _locator);

        _icon = new NotifyIcon { ContextMenuStrip = _menu };
        _menu.Opening += (_, _) => BuildMenu();
        // Left click opens or closes the overlay; right click keeps the menu. Whether it was open is read on the
        // press: the press makes the taskbar foreground, the focus watcher hides the overlay before the release,
        // and a toggle on the release would open it again.
        _icon.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) _overlayOpenAtPress = _overlay?.IsOpen ?? false;
        };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            var wasOpen = _overlayOpenAtPress ?? _overlay?.IsOpen ?? false;
            _overlayOpenAtPress = null;
            _overlay?.Toggle(wasOpen);
        };

        _config.Changed += OnConfigChanged;
        Loc.LanguageChanged += () => OnUi(RefreshIcon);
    }

    /// <summary>True when this process really is the full-mode exe with UIAccess granted.</summary>
    public bool InFullMode => _kind == LauncherKind.UIAccess && ProcessInfo.HasUIAccess;

    public void Start(bool migrated, bool finishDisableFullMode)
    {
        _locator.StartSearch();
        StartAudio();
        // Read once in the background now, so the first open of the Games tab is not empty; again on every open.
        _games = new GameService(_config, message => Notify(message, force: true));
        _games.Changed += () => _overlay?.Invalidate();
        _games.Refresh();
        try
        {
            var content = new OverlayContent(_config, _monitors, _locator, this, new AudioServiceView(_audio!), _media,
                games: _games);
            _overlay = new OverlayController(_app, _config, _monitors, content);
            _overlay.OpenChanged += OnOverlayOpenChanged;
        }
        catch (Exception e)
        {
            Log.Error("Overlay start", e); // the tray keeps working without it
        }
        _hotkeys = new HotkeyService();
        _hotkeys.Pressed += OnHotkey;
        _ = RegisterHotkeysAsync();
        StartPads();

        RefreshIcon();
        _icon.Visible = true;

        if (migrated) Notify(Loc.T("Notify.Migrated"));
        if (LegacyInstall.IsRunning()) Notify(Loc.T("Notify.LegacyRunning"));
        if (finishDisableFullMode) _ = FinishDisableFullModeAsync();
    }

    // --- hotkeys ------------------------------------------------------------

    private async Task RegisterHotkeysAsync()
    {
        if (_hotkeys is null) return;
        if (!await _hotkeys.RegisterAsync(HotkeyOverlay, Native.MOD_SHIFT, Native.VK_F1))
            Notify(Loc.T("Notify.HotkeyBusy", "Shift+F1"));
        if (!await _hotkeys.RegisterAsync(HotkeyDesk, Native.MOD_CONTROL | Native.MOD_ALT, Native.VK_DOWN))
            Notify(Loc.T("Notify.HotkeyBusy", "Ctrl+Alt+↓"));
        if (!await _hotkeys.RegisterAsync(HotkeyTv, Native.MOD_CONTROL | Native.MOD_ALT, Native.VK_UP))
            Notify(Loc.T("Notify.HotkeyBusy", "Ctrl+Alt+↑"));
        _lastHomeKeyMode = _config.Current.HomeKeyMode;
        await ApplyHomeKeyAsync(_config.Current.HomeKeyMode, notifyIfBusy: false);
    }

    private async Task ApplyHomeKeyAsync(string mode, bool notifyIfBusy)
    {
        if (_hotkeys is null) return;
        if (mode == HomeKeyModes.None)
        {
            await _hotkeys.UnregisterAsync(HotkeyHome);
            return;
        }
        if (!_hotkeys.IsRegistered(HotkeyHome)
            && !await _hotkeys.RegisterAsync(HotkeyHome, 0, Native.VK_BROWSER_HOME)
            && notifyIfBusy)
            Notify(Loc.T("Notify.HomeKeyBusy"));
    }

    /// <summary>Runs on the hotkey thread.</summary>
    private void OnHotkey(int id)
    {
        switch (id)
        {
            case HotkeyHome:
                var mode = _config.Current.HomeKeyMode;
                if (mode == HomeKeyModes.Windows) KeySender.WindowsKey();
                else if (mode == HomeKeyModes.AltTab) KeySender.AltTab();
                break;
            case HotkeyOverlay:
                _overlay?.Toggle();
                break;
            case HotkeyDesk:
                SwitchMonitor(() => _monitors.SwitchTo(MonitorRole.Desk));
                break;
            case HotkeyTv:
                SwitchMonitor(() => _monitors.SwitchTo(MonitorRole.Tv));
                break;
        }
    }

    private void SwitchMonitor(Func<SwitchResult> action)
    {
        var result = action();
        var name = _monitors.Current;
        OnUi(() =>
        {
            RefreshIcon();
            Notify(result switch
            {
                SwitchResult.Switched => Loc.T("Notify.MonitorSwitched", name),
                SwitchResult.NoProfile => Loc.T("Notify.NoProfile"),
                SwitchResult.SwitcherMissing => Loc.T("Notify.SwitcherMissing"),
                _ => Loc.T("Notify.SwitcherFailed"),
            });
        });
    }

    // --- audio ----------------------------------------------------------------

    /// <summary>
    /// Audio devices and the media session run for the whole session, overlay open or not. Both only queue work
    /// and raise <c>Changed</c> on their own threads; failures are logged and the rest keeps working.
    /// </summary>
    private void StartAudio()
    {
        _audio = new AudioService(_config);
        _audio.Changed += OnAudioChanged;
        try { _audio.Start(); }
        catch (Exception e) { Log.Error("Audio start", e); }
        try
        {
            _media = new MediaSession();
            _media.Changed += () => _overlay?.Invalidate();
        }
        catch (Exception e)
        {
            Log.Error("Media session start", e);
        }
    }

    /// <summary>Runs on the audio thread.</summary>
    private void OnAudioChanged()
    {
        _overlay?.Invalidate();
        OnUi(() =>
        {
            if (_indicator?.IsShowing != true || _audio is null) return;
            if (_indicatorShowsMic) _indicator.SetMic(_audio.MicMuted);
            else _indicator.SetVolume(_audio.Volume, _audio.Muted);
        });
    }

    /// <summary>Back + Left / Right with the overlay closed. Pad thread.</summary>
    private void StepVolumeFromPad(int direction)
    {
        if (_audio?.Default(AudioFlow.Output) is not { } output) return;
        // Shown right away with the value the service will set (same rule); OnAudioChanged corrects it if needed.
        var cap = AudioRules.CapFor(_config.Current.AudioVolumeCaps, output.Id);
        var level = AudioRules.StepVolume(_audio.Volume, direction, cap);
        var muted = _audio.Muted && direction < 0;
        _audio.StepVolume(direction);
        OnUi(() => ShowIndicator(mic: false, level, muted));
    }

    /// <summary>Back + Y with the overlay closed. Pad thread.</summary>
    private void ToggleMicFromPad()
    {
        if (_audio?.Default(AudioFlow.Input) is null) return;
        var muted = !_audio.MicMuted;
        _audio.ToggleMicMute();
        OnUi(() => ShowIndicator(mic: true, 0, muted));
    }

    private void ShowIndicator(bool mic, double level, bool muted)
    {
        try
        {
            _indicator ??= new VolumeIndicator();
            _indicatorShowsMic = mic;
            if (mic) _indicator.SetMic(muted);
            else _indicator.SetVolume(level, muted);
            _indicator.Flash();
        }
        catch (Exception e)
        {
            Log.Error("Volume indicator", e);
        }
    }

    // --- pads -----------------------------------------------------------------

    /// <summary>Starts gamepad input; without SDL3.dll it logs once and the rest keeps working.</summary>
    private void StartPads()
    {
        try
        {
            _pads = new PadService(new PadInterpreter()) { CombosEnabled = _config.Current.ControllerEnabled };
            _pads.ActionRaised += OnPadAction;
            _pads.StateChanged += () => _overlay?.SetPadState(_pads.PadCount > 0, _pads.BatteryLevel, _pads.BatteryPercent);
            if (_overlay is not null) _overlay.OpenChanged += open => { if (_pads is not null) _pads.OverlayOpen = open; };
            _pads.Start();
        }
        catch (Exception e)
        {
            Log.Error("Pad start", e);
        }
    }

    /// <summary>Runs on the pad thread.</summary>
    private void OnPadAction(PadAction action)
    {
        switch (action.Kind)
        {
            case PadActionKind.ToggleOverlay:
                _overlay?.Toggle();
                break;
            case PadActionKind.MonitorTv:
                SwitchMonitor(() => _monitors.SwitchTo(MonitorRole.Tv));
                break;
            case PadActionKind.MonitorDesk:
                SwitchMonitor(() => _monitors.SwitchTo(MonitorRole.Desk));
                break;
            case PadActionKind.Command:
                var command = action.Command;
                OnUi(() =>
                {
                    // An open name prompt takes the pad: A accepts, B cancels, the rest is swallowed.
                    if (_nameDialog is not { } dialog)
                    {
                        _overlay?.Command(command);
                        return;
                    }
                    try
                    {
                        switch (DialogPad.Route(command))
                        {
                            case DialogPadAction.Accept: dialog.Accept(); break;
                            case DialogPadAction.Cancel: dialog.Dismiss(); break;
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Error("Name dialog pad", e);
                    }
                });
                break;
            case PadActionKind.VolumeDown or PadActionKind.VolumeUp or PadActionKind.MicMute:
                try
                {
                    if (action.Kind == PadActionKind.MicMute) ToggleMicFromPad();
                    else StepVolumeFromPad(action.Kind == PadActionKind.VolumeUp ? 1 : -1);
                }
                catch (Exception e)
                {
                    Log.Error("Pad audio shortcut", e);
                }
                break;
        }
    }

    private string _lastHomeKeyMode = "";

    private void OnConfigChanged(AppConfig config)
    {
        if (_pads is not null) _pads.CombosEnabled = config.ControllerEnabled;
        Loc.SetLanguage(config.Language);
        if (config.HomeKeyMode != _lastHomeKeyMode)
        {
            _lastHomeKeyMode = config.HomeKeyMode;
            _ = ApplyHomeKeyAsync(config.HomeKeyMode, notifyIfBusy: true);
        }
        OnUi(RefreshIcon);
    }

    // --- icon / notifications -----------------------------------------------

    private void RefreshIcon()
    {
        var profiles = _monitors.List();
        var current = profiles.Count > 0 ? _monitors.Current : "";
        var style = string.IsNullOrEmpty(current) ? (((byte, byte, byte), string)?)null : _monitors.Style(current);

        var previous = _currentIcon;
        _currentIcon = TrayIconRenderer.Create(style, SystemInformation.SmallIconSize.Width * 2);
        _icon.Icon = _currentIcon;
        TrayIconRenderer.Destroy(previous);

        var lines = new List<string> { AppPaths.AppName };
        if (!string.IsNullOrEmpty(current)) lines.Add(Loc.T("Tooltip.Monitor", current));
        lines.Add(Loc.T(InFullMode ? "Tooltip.Mode.Full" : "Tooltip.Mode.Standard"));
        var text = string.Join("\n", lines);
        _icon.Text = text.Length > 127 ? text[..127] : text; // Windows limit
    }

    /// <param name="force">Ignore the notifications setting (failures the user must see).</param>
    private void Notify(string message, bool force = false)
    {
        if (!force && !_config.Current.NotificationsEnabled) return;
        OnUi(() => _icon.ShowBalloonTip(3000, AppPaths.AppName, message, ToolTipIcon.None));
    }

    private void OnUi(Action action)
    {
        if (_app.Dispatcher.CheckAccess()) action();
        else _app.Dispatcher.BeginInvoke(action);
    }

    // --- overlay host ------------------------------------------------------------
    // Actions the overlay runs after hiding arrive on a worker thread; the others on the UI thread.

    public bool PreviousWindowIsForeground => _overlay?.PreviousWindowIsForeground ?? false;

    public void SwitchMonitor(string profile) => SwitchMonitor(() => _monitors.Switch(profile));

    public void ChooseMonitorSwitcher() => OnUi(() =>
    {
        // Owned by the overlay, or the topmost panel would cover the dialog.
        ChooseSwitcher(_overlay is { IsOpen: true } overlay ? new WindowOwner(overlay.Handle) : null);
        _overlay?.Refocus();
    });

    /// <summary>The overlay flow: no dialogs, failures become a notification.</summary>
    public void SwitchFullMode() => OnUi(async () =>
    {
        if (_busy) return;
        try
        {
            await RunSwitch(InFullMode ? ModeSwitcher.BeginDisableFullMode : ModeSwitcher.EnableFullMode,
                exitOnSuccess: true, notifyFailure: true);
        }
        catch (Exception e)
        {
            Log.Error("Overlay full mode", e);
        }
    });

    void IOverlayHost.Exit() => OnUi(Exit);

    /// <summary>
    /// Add / rename a channel, save a preset. Opened after the navigator's action has returned (BeginInvoke), modeless
    /// and owned by the overlay; <paramref name="done"/> runs on the UI thread, only for OK with a non-empty name.
    /// A new request replaces a prompt still open (cancelling it), so the prompt shown always matches its callback;
    /// the overlay hiding cancels it. While it is open, pad A / B accept / cancel it (<see cref="OnPadAction"/>).
    /// </summary>
    public void AskName(string title, string suggested, Action<string> done) => _app.Dispatcher.BeginInvoke(() =>
    {
        try
        {
            var previous = _nameDialog; // replaced: cancelled without handing the focus back to the overlay
            _nameDialog = null;
            previous?.Dismiss();
            var owner = _overlay is { IsOpen: true } overlay ? overlay.Handle : 0;
            var dialog = new NameDialog(title, suggested, owner, WindowEffects.Accent(), _pads?.PadCount > 0);
            dialog.Accepted += name =>
            {
                try { done(name); }
                catch (Exception e) { Log.Error("Name dialog action", e); }
            };
            dialog.Closed += (_, _) =>
            {
                if (_nameDialog != dialog) return; // replaced by a newer prompt, which keeps the focus
                _nameDialog = null;
                _overlay?.Refocus();
            };
            _nameDialog = dialog;
            dialog.Show();
        }
        catch (Exception e)
        {
            _nameDialog = null;
            Log.Error("Name dialog", e);
        }
    });

    /// <summary>
    /// "Add program…": an .exe file picker owned by the overlay (or the topmost panel would cover it), opened after the
    /// navigator's action has returned. <paramref name="done"/> gets the full path on the UI thread.
    /// </summary>
    public void ChooseProgram(Action<string> done) => _app.Dispatcher.BeginInvoke(() =>
    {
        if (_choosingProgram) return;
        _choosingProgram = true;
        try
        {
            string? path = null;
            using (var dialog = new OpenFileDialog
                   {
                       Title = Loc.T("Settings.Audio.AddProgram"),
                       Filter = Loc.T("Dialog.ChooseProgram.Filter"),
                       CheckFileExists = true,
                   })
            {
                var owner = _overlay is { IsOpen: true } overlay ? new WindowOwner(overlay.Handle) : null;
                if (dialog.ShowDialog(owner) == DialogResult.OK) path = dialog.FileName;
            }
            _overlay?.Refocus();
            if (!string.IsNullOrEmpty(path)) done(path);
        }
        catch (Exception e)
        {
            Log.Error("Choose program", e);
        }
        finally
        {
            _choosingProgram = false;
        }
    });

    /// <summary>
    /// The overlay opened: the game library is read again unless it was read in the last 30 s (new installs show up).
    /// It hid: an open name prompt is cancelled (it would float alone over the game).
    /// </summary>
    private void OnOverlayOpenChanged(bool open)
    {
        if (open) _games?.RefreshIfStale();
        else _nameDialog?.Dismiss();
    }

    private sealed class WindowOwner(nint handle) : IWin32Window
    {
        public nint Handle => handle;
    }

    // --- menu -----------------------------------------------------------------

    private void BuildMenu()
    {
        _menu.Items.Clear();
        var cfg = _config.Current;

        // Also reachable where Shift+F1 is not (e.g. Remote Desktop keeps some key combinations).
        if (_overlay is not null)
            _menu.Items.Add(Item(Loc.T("Overlay.Info.Key.Toggle"), () => _overlay.Toggle()));
        _menu.Items.Add(BuildMonitorMenu());

        var home = new ToolStripMenuItem(Loc.T("Menu.HomeKey"));
        foreach (var (mode, key) in new[]
                 {
                     (HomeKeyModes.Windows, "Menu.HomeKey.Windows"),
                     (HomeKeyModes.AltTab, "Menu.HomeKey.AltTab"),
                     (HomeKeyModes.None, "Menu.HomeKey.None"),
                 })
            home.DropDownItems.Add(Radio(Loc.T(key), cfg.HomeKeyMode == mode,
                () => _config.Update(c => c.HomeKeyMode = mode)));
        _menu.Items.Add(home);

        var language = new ToolStripMenuItem(Loc.T("Menu.Language"));
        language.DropDownItems.Add(Radio(Loc.T("Menu.Language.Auto", Loc.T("Language." + Loc.SystemCode)),
            cfg.Language is not ("en" or "it"), () => _config.Update(c => c.Language = Loc.Auto)));
        foreach (var code in Loc.Supported)
            language.DropDownItems.Add(Radio(Loc.T("Language." + code), cfg.Language == code,
                () => _config.Update(c => c.Language = code)));
        _menu.Items.Add(language);

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Check(Loc.T("Menu.Notifications"), cfg.NotificationsEnabled,
            () => _config.Update(c => c.NotificationsEnabled = !c.NotificationsEnabled)));
        _menu.Items.Add(Check(Loc.T("Menu.Startup"), StartupRegistration.IsEnabled, ToggleStartup));
        _menu.Items.Add(BuildFullModeMenu());
        if (LegacyInstall.IsPresent())
            _menu.Items.Add(Item(Loc.T("Menu.Legacy"), RemoveLegacy));

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Item(Loc.T("Menu.OpenSettingsFolder"), () => OpenFolder(AppPaths.ConfigDir)));
        _menu.Items.Add(Item(Loc.T("Menu.Exit"), Exit));

        MenuTheme.Apply(_menu);
    }

    private ToolStripMenuItem BuildMonitorMenu()
    {
        var menu = new ToolStripMenuItem(Loc.T("Menu.Monitor"));
        _monitors.InvalidateCache();
        var profiles = _monitors.List();
        var desk = _monitors.ForRole(MonitorRole.Desk);
        var tv = _monitors.ForRole(MonitorRole.Tv);
        var current = _monitors.Current;

        if (profiles.Count == 0)
        {
            menu.DropDownItems.Add(Disabled(Loc.T("Menu.Monitor.None")));
        }
        foreach (var name in profiles)
        {
            var label = name + (name == desk ? "\tCtrl+Alt+↓" : name == tv ? "\tCtrl+Alt+↑" : "");
            var captured = name;
            menu.DropDownItems.Add(Radio(label, name == current,
                () => Task.Run(() => SwitchMonitor(() => _monitors.Switch(captured)))));
        }

        if (profiles.Count > 0)
        {
            menu.DropDownItems.Add(new ToolStripSeparator());
            menu.DropDownItems.Add(RoleMenu(MonitorRole.Desk, "Menu.Monitor.DeskRole", desk, profiles));
            menu.DropDownItems.Add(RoleMenu(MonitorRole.Tv, "Menu.Monitor.TvRole", tv, profiles));
        }

        menu.DropDownItems.Add(new ToolStripSeparator());
        var path = _locator.Path;
        menu.DropDownItems.Add(Disabled(path switch
        {
            null => Loc.T("Menu.Monitor.SwitcherSearching"),
            "" => Loc.T("Menu.Monitor.SwitcherMissing"),
            _ => Loc.T("Menu.Monitor.SwitcherFound", path),
        }));
        menu.DropDownItems.Add(Item(Loc.T("Menu.Monitor.ChooseSwitcher"), () => ChooseSwitcher()));
        if (path == "")
            menu.DropDownItems.Add(Item(Loc.T("Menu.Monitor.Download"), () => OpenUrl(MonitorSwitcherLocator.Website)));
        menu.DropDownItems.Add(Item(Loc.T("Menu.Monitor.OpenProfiles"), () => OpenFolder(_monitors.ProfilesDir)));
        return menu;
    }

    private ToolStripMenuItem RoleMenu(MonitorRole role, string key, string assigned, IReadOnlyList<string> profiles)
    {
        var menu = new ToolStripMenuItem(Loc.T(key));
        foreach (var name in profiles)
        {
            var captured = name;
            menu.DropDownItems.Add(Radio(name, name == assigned, () => _monitors.SetRole(role, captured)));
        }
        menu.DropDownItems.Add(Radio(Loc.T("Menu.Monitor.RoleNone"), assigned == "", () => _monitors.SetRole(role, null)));
        return menu;
    }

    private ToolStripMenuItem BuildFullModeMenu()
    {
        var menu = new ToolStripMenuItem(Loc.T("Menu.FullMode"));
        menu.DropDownItems.Add(Disabled(Loc.T(InFullMode ? "Menu.FullMode.StatusOn" : "Menu.FullMode.StatusOff")));
        menu.DropDownItems.Add(InFullMode
            ? Item(Loc.T("Menu.FullMode.Disable"), DisableFullMode, enabled: !_busy)
            : Item(Loc.T("Menu.FullMode.Enable"), EnableFullMode, enabled: !_busy));
        return menu;
    }

    private static ToolStripMenuItem Item(string text, Action onClick, bool enabled = true)
    {
        var item = new ToolStripMenuItem(text) { Enabled = enabled };
        item.Click += (_, _) => Guard(onClick);
        return item;
    }

    private static ToolStripMenuItem Check(string text, bool on, Action onClick)
    {
        var item = Item(text, onClick);
        item.Checked = on;
        return item;
    }

    private static ToolStripMenuItem Radio(string text, bool on, Action onClick) => Check(text, on, onClick);

    private static ToolStripMenuItem Disabled(string text) => new(text) { Enabled = false };

    private static void Guard(Action action)
    {
        try { action(); }
        catch (Exception e) { Log.Error("Menu action", e); }
    }

    // --- actions ----------------------------------------------------------------

    public void ToggleStartup()
    {
        if (StartupRegistration.IsEnabled)
        {
            Notify(StartupRegistration.Disable() ? Loc.T("Notify.StartupOff") : Loc.T("Notify.StartupFailed"));
            return;
        }
        var exe = Path.Combine(AppPaths.AppDir, InFullMode ? AppPaths.UIAccessExeName : AppPaths.StandardExeName);
        Notify(StartupRegistration.Enable(exe) ? Loc.T("Notify.StartupOn") : Loc.T("Notify.StartupFailed"));
    }

    private void ChooseSwitcher(IWin32Window? owner = null)
    {
        using var dialog = new OpenFileDialog
        {
            Title = Loc.T("Menu.Monitor.ChooseSwitcher"),
            Filter = Loc.T("Dialog.ChooseSwitcher.Filter"),
            FileName = MonitorSwitcherLocator.ExeName,
        };
        if (dialog.ShowDialog(owner) == DialogResult.OK) _locator.Set(dialog.FileName);
    }

    private async void EnableFullMode()
    {
        if (!Confirm(Loc.T("Dialog.FullMode.EnableBody"))) return;
        await RunSwitch(ModeSwitcher.EnableFullMode, exitOnSuccess: true);
    }

    private async void DisableFullMode()
    {
        if (!Confirm(Loc.T("Dialog.FullMode.DisableBody"))) return;
        await RunSwitch(ModeSwitcher.BeginDisableFullMode, exitOnSuccess: true);
    }

    private async Task FinishDisableFullModeAsync()
    {
        var result = await Task.Run(ModeSwitcher.FinishDisableFullMode);
        if (result.Outcome == ModeSwitcher.Outcome.Ok) Notify(Loc.T("Notify.StandardMode"));
        else Show(Loc.T("Dialog.FullMode.DisableIncomplete"), MessageBoxImage.Warning);
    }

    private async void RemoveLegacy()
    {
        if (!Confirm(Loc.T("Dialog.Legacy.Body"))) return;
        var result = await Task.Run(ModeSwitcher.RemoveLegacy);
        if (result.Outcome == ModeSwitcher.Outcome.Ok) Show(Loc.T("Dialog.Legacy.Done"), MessageBoxImage.Information);
        else ShowFailure(result);
    }

    private async Task RunSwitch(Func<ModeSwitcher.Result> step, bool exitOnSuccess, bool notifyFailure = false)
    {
        _busy = true;
        try
        {
            var result = await Task.Run(step);
            if (result.Outcome == ModeSwitcher.Outcome.Ok)
            {
                if (exitOnSuccess) Exit();
                return;
            }
            if (notifyFailure) Notify(FailureMessage(result), force: true);
            else ShowFailure(result);
        }
        finally
        {
            _busy = false;
        }
    }

    private static void ShowFailure(ModeSwitcher.Result result) => Show(FailureMessage(result), MessageBoxImage.Warning);

    private static string FailureMessage(ModeSwitcher.Result result) =>
        result.Outcome switch
        {
            ModeSwitcher.Outcome.Cancelled => Loc.T("Dialog.FullMode.Cancelled"),
            ModeSwitcher.Outcome.LaunchFailed => Loc.T("Dialog.FullMode.LaunchFailed", result.Detail ?? ""),
            _ => result.Helper switch
            {
                HelperResult.NotSigned => Loc.T("Dialog.FullMode.NotSigned"),
                HelperResult.UntrustedSigner => Loc.T("Dialog.FullMode.Untrusted"),
                HelperResult.CopyFailed => Loc.T("Dialog.FullMode.CopyFailed"),
                _ => Loc.T("Dialog.FullMode.Failed"),
            },
        };

    private static bool Confirm(string message) =>
        MessageBox.Show(message, AppPaths.AppName, MessageBoxButton.OKCancel, MessageBoxImage.Question)
        == MessageBoxResult.OK;

    private static void Show(string message, MessageBoxImage image) =>
        MessageBox.Show(message, AppPaths.AppName, MessageBoxButton.OK, image);

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
    }

    private static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();

    private void Exit()
    {
        _overlay?.Dispose(); // closes the overlay window for real (it refuses to close otherwise)
        _icon.Visible = false;
        _app.Shutdown();
    }

    public void Dispose()
    {
        _pads?.Dispose();
        _audio?.Dispose();
        _media?.Dispose();
        _indicator?.Dismiss();
        _nameDialog?.Dismiss();
        _hotkeys?.Dispose();
        _overlay?.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        TrayIconRenderer.Destroy(_currentIcon);
    }
}

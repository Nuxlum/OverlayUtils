using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;
using OverlayUtils.Core;
using OverlayUtils.Core.Config;
using OverlayUtils.Core.Diagnostics;
using OverlayUtils.Core.Input;
using OverlayUtils.Core.Interop;
using OverlayUtils.Core.Legacy;
using OverlayUtils.Core.Localization;
using OverlayUtils.Core.Monitors;
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
/// full mode. Lives on the WPF UI thread; hotkey callbacks are marshalled here.
/// </summary>
internal sealed class TrayController : IDisposable
{
    private const int HotkeyHome = 1;
    // 2 is reserved for the overlay toggle (Shift+F1), ported in stage 2.
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
    private System.Drawing.Icon? _currentIcon;
    private bool _busy; // a mode switch is in progress

    public TrayController(Application app, ConfigStore config, LauncherKind kind)
    {
        _app = app;
        _config = config;
        _kind = kind;
        _locator = new MonitorSwitcherLocator(config);
        _monitors = new MonitorProfiles(config, _locator);

        _icon = new NotifyIcon { ContextMenuStrip = _menu };
        _menu.Opening += (_, _) => BuildMenu();
        _icon.MouseUp += (_, e) =>
        {
            // Until the overlay exists (stage 2) a left click opens the menu too.
            // NotifyIcon only exposes this privately; it positions the menu correctly.
            if (e.Button == MouseButtons.Left)
                typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.Invoke(_icon, null);
        };

        _config.Changed += OnConfigChanged;
        Loc.LanguageChanged += () => OnUi(RefreshIcon);
    }

    /// <summary>True when this process really is the full-mode exe with UIAccess granted.</summary>
    private bool InFullMode => _kind == LauncherKind.UIAccess && ProcessInfo.HasUIAccess;

    public void Start(bool migrated, bool finishDisableFullMode)
    {
        _locator.StartSearch();
        _hotkeys = new HotkeyService();
        _hotkeys.Pressed += OnHotkey;
        _ = RegisterHotkeysAsync();

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
        if (!await _hotkeys.RegisterAsync(HotkeyDesk, Native.MOD_CONTROL | Native.MOD_ALT, Native.VK_DOWN))
            Notify(Loc.T("Notify.HotkeyBusy", "Ctrl+Alt+↓"));
        if (!await _hotkeys.RegisterAsync(HotkeyTv, Native.MOD_CONTROL | Native.MOD_ALT, Native.VK_UP))
            Notify(Loc.T("Notify.HotkeyBusy", "Ctrl+Alt+↑"));
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

    private void OnConfigChanged(AppConfig config)
    {
        Loc.SetLanguage(config.Language);
        _ = ApplyHomeKeyAsync(config.HomeKeyMode, notifyIfBusy: true);
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

    private void Notify(string message)
    {
        if (!_config.Current.NotificationsEnabled) return;
        OnUi(() => _icon.ShowBalloonTip(3000, AppPaths.AppName, message, ToolTipIcon.None));
    }

    private void OnUi(Action action)
    {
        if (_app.Dispatcher.CheckAccess()) action();
        else _app.Dispatcher.BeginInvoke(action);
    }

    // --- menu -----------------------------------------------------------------

    private void BuildMenu()
    {
        _menu.Items.Clear();
        var cfg = _config.Current;

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
        menu.DropDownItems.Add(Item(Loc.T("Menu.Monitor.ChooseSwitcher"), ChooseSwitcher));
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

    private void ToggleStartup()
    {
        if (StartupRegistration.IsEnabled)
        {
            Notify(StartupRegistration.Disable() ? Loc.T("Notify.StartupOff") : Loc.T("Notify.StartupFailed"));
            return;
        }
        var exe = Path.Combine(AppPaths.AppDir, InFullMode ? AppPaths.UIAccessExeName : AppPaths.StandardExeName);
        Notify(StartupRegistration.Enable(exe) ? Loc.T("Notify.StartupOn") : Loc.T("Notify.StartupFailed"));
    }

    private void ChooseSwitcher()
    {
        using var dialog = new OpenFileDialog
        {
            Title = Loc.T("Menu.Monitor.ChooseSwitcher"),
            Filter = Loc.T("Dialog.ChooseSwitcher.Filter"),
            FileName = MonitorSwitcherLocator.ExeName,
        };
        if (dialog.ShowDialog() == DialogResult.OK) _locator.Set(dialog.FileName);
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

    private async Task RunSwitch(Func<ModeSwitcher.Result> step, bool exitOnSuccess)
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
            ShowFailure(result);
        }
        finally
        {
            _busy = false;
        }
    }

    private static void ShowFailure(ModeSwitcher.Result result)
    {
        var message = result.Outcome switch
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
        Show(message, MessageBoxImage.Warning);
    }

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
        _icon.Visible = false;
        _app.Shutdown();
    }

    public void Dispose()
    {
        _hotkeys?.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        TrayIconRenderer.Destroy(_currentIcon);
    }
}

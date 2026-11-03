using OverlayUtils.Core.Config;
using OverlayUtils.Core.Input;
using OverlayUtils.Core.Localization;
using OverlayUtils.Core.Monitors;
using OverlayUtils.Core.Platform;

namespace OverlayUtils.Core.Overlay;

/// <summary>
/// The rows of the overlay: Controls tab, Settings page and Info page (Python OVERLAY_ITEMS, SETTINGS_ITEMS and
/// _info_panels). Only features that exist in the .NET app are listed. Lists are rebuilt on every call, so row
/// kinds, icons and labels follow the current profiles, config and language; the only state kept here is the
/// monitor pick and the open Info panel, both cleared by <see cref="ResetTransient"/>.
/// </summary>
public sealed class OverlayContent
{
    public const string ControlsTabId = "Controlli"; // Python tab name, kept as the stored id
    public const int MonitorMaxPills = 3;

    const string ShortcutsPanel = "shortcuts";
    const string MonitorPanel = "monitor";
    const string SystemPanel = "system";

    readonly ConfigStore _config;
    readonly MonitorProfiles _monitors;
    readonly MonitorSwitcherLocator _locator;
    readonly IOverlayHost _host;
    readonly Action _sendAltF4;

    string? _monitorPick;
    string _openPanel = ShortcutsPanel;

    public OverlayContent(ConfigStore config, MonitorProfiles monitors, MonitorSwitcherLocator locator,
        IOverlayHost host, Action? sendAltF4 = null)
    {
        _config = config;
        _monitors = monitors;
        _locator = locator;
        _host = host;
        _sendAltF4 = sendAltF4 ?? KeySender.AltF4;
        Tabs = [new OverlayTab(ControlsTabId, () => Loc.T("Overlay.Tab.Controls"), Controls)];
    }

    public IReadOnlyList<OverlayTab> Tabs { get; }

    /// <summary>Called when the overlay opens: monitor pick cleared, Info shows Shortcuts.</summary>
    public void ResetTransient()
    {
        _monitorPick = null;
        _openPanel = ShortcutsPanel;
    }

    // --- Controls -------------------------------------------------------------

    public IReadOnlyList<OverlayItem> Controls() =>
    [
        MonitorRow(),
        new OverlayItem
        {
            Kind = ItemKind.Segmented, Icon = Glyphs.Game,
            Section = () => Loc.T("Overlay.Section.Steam"),
            Label = () => Loc.T("Overlay.BigPicture.Label"),
            Description = () => Loc.T("Overlay.BigPicture.Desc"),
            Options = () =>
            [
                new OverlayOption(() => Loc.T("Overlay.BigPicture.Open"), () => ShellOpen.Open("steam://open/bigpicture")),
                new OverlayOption(() => Loc.T("Overlay.BigPicture.Exit"), () => ShellOpen.Open("steam://close/bigpicture")),
            ],
        },
        new OverlayItem
        {
            Kind = ItemKind.Action, Icon = Glyphs.Desktop,
            Section = () => Loc.T("Overlay.Section.Windows"),
            Label = () => Loc.T("Overlay.ShowDesktop.Label"),
            Description = () => Loc.T("Overlay.ShowDesktop.Desc"),
            Action = KeySender.ShowDesktop,
        },
        new OverlayItem
        {
            Kind = ItemKind.Action, Icon = Glyphs.Close,
            Label = () => Loc.T("Overlay.CloseWindow.Label"),
            Description = () => Loc.T("Overlay.CloseWindow.Desc"),
            Hint = () => "Alt+F4",
            Action = () =>
            {
                // Otherwise it would close the taskbar, the desktop or whatever took the foreground meanwhile.
                if (_host.PreviousWindowIsForeground) _sendAltF4();
            },
        },
        new OverlayItem
        {
            Kind = ItemKind.Segmented, Icon = Glyphs.Power, Confirm = true,
            Section = () => Loc.T("Overlay.Section.System"),
            Label = () => Loc.T("Overlay.Power.Label"),
            Description = () => Loc.T("Overlay.Power.Desc"),
            Options = () =>
            [
                new OverlayOption(() => Loc.T("Overlay.Power.Sleep"), PowerActions.Sleep),
                new OverlayOption(() => Loc.T("Overlay.Power.Restart"), PowerActions.Restart),
                new OverlayOption(() => Loc.T("Overlay.Power.ShutDown"), PowerActions.ShutDown),
            ],
        },
    ];

    OverlayItem MonitorRow()
    {
        var profiles = _monitors.List();
        Func<bool> visible = () => _monitors.List().Count > 0;
        Func<string> label = () => Loc.T("Overlay.Monitor.Label");
        Func<string> description = () => Loc.T("Overlay.Monitor.Desc");
        Func<string> section = () => Loc.T("Overlay.Section.Screen");

        if (profiles.Count <= MonitorMaxPills)
        {
            return new OverlayItem
            {
                Kind = ItemKind.Segmented, Icon = Glyphs.Monitor, Section = section, Label = label,
                Description = description, Visible = visible,
                Options = () => _monitors.List().Select(p => new OverlayOption(
                    () => p, () => _host.SwitchMonitor(p), Active: () => _monitors.Current == p)).ToList(),
            };
        }

        return new OverlayItem
        {
            Kind = ItemKind.Choice, Icon = Glyphs.Monitor, Section = section, Label = label,
            Description = description, Visible = visible,
            Value = () =>
            {
                var name = MonitorPick();
                return name == _monitors.Current ? Loc.T("Overlay.Monitor.Active", name) : name;
            },
            Step = direction =>
            {
                var list = _monitors.List();
                if (list.Count == 0) return;
                var index = list.ToList().IndexOf(MonitorPick());
                _monitorPick = list[(((index + direction) % list.Count) + list.Count) % list.Count];
            },
            Apply = () =>
            {
                var name = MonitorPick();
                if (name.Length > 0) _host.SwitchMonitor(name);
            },
        };
    }

    /// <summary>The picked profile, else the current one, else the first.</summary>
    string MonitorPick()
    {
        var list = _monitors.List();
        if (_monitorPick is { } pick && list.Contains(pick)) return pick;
        var current = _monitors.Current;
        if (list.Contains(current)) return current;
        return list.Count > 0 ? list[0] : "";
    }

    // --- Settings -------------------------------------------------------------

    public IReadOnlyList<OverlayItem> Settings()
    {
        Func<bool> hasProfiles = () => _monitors.List().Count > 0;
        var cfg = _config.Current;
        var fullMode = _host.InFullMode;

        return
        [
            new OverlayItem
            {
                Kind = ItemKind.Toggle, Icon = Glyphs.List,
                Section = () => Loc.T("Overlay.Section.Tabs"),
                Label = () => Loc.T("Overlay.Tab.Controls"),
                Description = () => Loc.T("Settings.Tab.Desc", Loc.T("Overlay.Tab.Controls")),
                IsOn = () => true,
                Toggle = () => { }, // the only tab: it cannot be hidden
            },
            RoleRow(MonitorRole.Desk, "Settings.Desk.Label", "Settings.Desk.Desc", hasProfiles, Glyphs.Pc, "Overlay.Section.Monitor"),
            RoleRow(MonitorRole.Tv, "Settings.Tv.Label", "Settings.Tv.Desc", hasProfiles, Glyphs.Monitor, null),
            new OverlayItem
            {
                Kind = ItemKind.Segmented, Icon = Glyphs.Folder, Visible = hasProfiles,
                Label = () => Loc.T("Settings.MonitorSwitcher.Label", SwitcherStatus()),
                Description = () => Loc.T("Settings.MonitorSwitcher.Desc"),
                Options = () =>
                [
                    new OverlayOption(() => Loc.T("Settings.MonitorSwitcher.Choose"), _host.ChooseMonitorSwitcher, Stay: true),
                ],
            },
            new OverlayItem
            {
                Kind = ItemKind.Toggle, Icon = cfg.NotificationsEnabled ? Glyphs.Bell : Glyphs.BellOff,
                Section = () => Loc.T("Overlay.Section.General"),
                Label = () => Loc.T("Menu.Notifications"),
                Description = () => Loc.T("Settings.Notifications.Desc"),
                IsOn = () => _config.Current.NotificationsEnabled,
                Toggle = () => _config.Update(c => c.NotificationsEnabled = !c.NotificationsEnabled),
            },
            new OverlayItem
            {
                Kind = ItemKind.Toggle, Icon = Glyphs.Power,
                Label = () => Loc.T("Menu.Startup"),
                Description = () => Loc.T("Settings.Startup.Desc"),
                IsOn = () => StartupRegistration.IsEnabled,
                Toggle = _host.ToggleStartup,
            },
            new OverlayItem
            {
                Kind = ItemKind.Toggle, Icon = Glyphs.Game,
                Label = () => Loc.T("Settings.PadCombos"),
                Description = () => Loc.T("Settings.PadCombos.Desc"),
                IsOn = () => _config.Current.ControllerEnabled,
                Toggle = () => _config.Update(c => c.ControllerEnabled = !c.ControllerEnabled),
            },
            new OverlayItem
            {
                Kind = ItemKind.Segmented, Icon = Glyphs.Home,
                Label = () => Loc.T("Menu.HomeKey"),
                Description = () => Loc.T("Settings.HomeKey.Desc"),
                Options = () =>
                [
                    HomeKeyOption("Settings.HomeKey.Windows", HomeKeyModes.Windows),
                    HomeKeyOption("Menu.HomeKey.AltTab", HomeKeyModes.AltTab),
                    HomeKeyOption("Settings.HomeKey.None", HomeKeyModes.None),
                ],
            },
            new OverlayItem
            {
                Kind = ItemKind.Choice, Icon = Glyphs.Settings,
                Label = () => Loc.T("Menu.Language"),
                Description = () => Loc.T("Settings.Language.Desc"),
                Value = LanguageValue,
                Step = StepLanguage,
            },
            new OverlayItem
            {
                Kind = ItemKind.Segmented, Icon = Glyphs.Monitor,
                Section = () => Loc.T("Overlay.Section.Overlay"),
                Label = () => Loc.T("Settings.Size.Label"),
                Description = () => Loc.T("Settings.Size.Desc"),
                Options = () =>
                [
                    ConfigOption("Settings.Size.Normal", c => c.OverlaySize, (c, v) => c.OverlaySize = v, OverlaySizes.Normal),
                    ConfigOption("Settings.Size.Large", c => c.OverlaySize, (c, v) => c.OverlaySize = v, OverlaySizes.Large),
                    ConfigOption("Settings.Size.Auto", c => c.OverlaySize, (c, v) => c.OverlaySize = v, OverlaySizes.Auto),
                ],
            },
            new OverlayItem
            {
                Kind = ItemKind.Segmented, Icon = Glyphs.Desktop,
                Label = () => Loc.T("Settings.Position.Label"),
                Description = () => Loc.T("Settings.Position.Desc"),
                Options = () =>
                [
                    ConfigOption("Settings.Position.Left", c => c.OverlaySide, (c, v) => c.OverlaySide = v, OverlaySides.Left),
                    ConfigOption("Settings.Position.Right", c => c.OverlaySide, (c, v) => c.OverlaySide = v, OverlaySides.Right),
                ],
            },
            new OverlayItem
            {
                Kind = ItemKind.Segmented, Icon = Glyphs.Game, Confirm = true,
                Label = () => Loc.T("Settings.FullMode.Label", Loc.T(fullMode ? "Settings.FullMode.On" : "Settings.FullMode.Off")),
                Description = () => Loc.T("Settings.FullMode.Desc"),
                Options = () =>
                [
                    new OverlayOption(() => Loc.T(fullMode ? "Settings.FullMode.Disable" : "Settings.FullMode.Enable"),
                        _host.SwitchFullMode),
                ],
            },
            new OverlayItem
            {
                Kind = ItemKind.Action, Icon = Glyphs.Info, OpensInfo = true,
                Section = () => Loc.T("Overlay.Section.Other"),
                Label = () => Loc.T("Settings.Info.Label"),
                Description = () => Loc.T("Settings.Info.Desc"),
            },
            new OverlayItem
            {
                Kind = ItemKind.Segmented, Icon = Glyphs.Close, Confirm = true,
                Label = () => Loc.T("Settings.Exit.Label"),
                Description = () => Loc.T("Settings.Exit.Desc"),
                Options = () => [new OverlayOption(() => Loc.T("Menu.Exit"), _host.Exit)],
            },
        ];
    }

    OverlayItem RoleRow(MonitorRole role, string labelKey, string descKey, Func<bool> visible, string icon, string? sectionKey) => new()
    {
        Kind = ItemKind.Choice, Icon = icon, Visible = visible,
        Section = sectionKey is null ? null : () => Loc.T(sectionKey),
        Label = () => Loc.T(labelKey),
        Description = () => Loc.T(descKey),
        Value = () => _monitors.ForRole(role) is { Length: > 0 } name ? name : Loc.T("Menu.Monitor.RoleNone"),
        Step = direction =>
        {
            var choices = _monitors.List().Append("").ToList();
            var index = choices.IndexOf(_monitors.ForRole(role));
            if (index < 0) index = choices.Count - 1;
            _monitors.SetRole(role, choices[(((index + direction) % choices.Count) + choices.Count) % choices.Count]);
        },
    };

    string SwitcherStatus() => _locator.Path switch
    {
        null => Loc.T("Settings.MonitorSwitcher.Searching"),
        "" => Loc.T("Settings.MonitorSwitcher.Missing"),
        _ => Loc.T("Settings.MonitorSwitcher.Found"),
    };

    OverlayOption HomeKeyOption(string labelKey, string mode) => new(
        () => Loc.T(labelKey), () => _config.Update(c => c.HomeKeyMode = mode), Stay: true,
        Active: () => _config.Current.HomeKeyMode == mode);

    OverlayOption ConfigOption(string labelKey, Func<AppConfig, string> get, Action<AppConfig, string> set, string value) => new(
        () => Loc.T(labelKey), () => _config.Update(c => set(c, value)), Stay: true,
        Active: () => get(_config.Current) == value);

    string LanguageValue()
    {
        var language = _config.Current.Language;
        return language is "en" or "it"
            ? Loc.T("Language." + language)
            : Loc.T("Menu.Language.Auto", Loc.T("Language." + Loc.SystemCode));
    }

    void StepLanguage(int direction)
    {
        string[] order = [Loc.Auto, .. Loc.Supported];
        var index = Math.Max(Array.IndexOf(order, _config.Current.Language), 0);
        var next = order[(((index + direction) % order.Length) + order.Length) % order.Length];
        _config.Update(c => c.Language = next);
    }

    // --- Info -----------------------------------------------------------------

    public IReadOnlyList<OverlayItem> Info()
    {
        var items = new List<OverlayItem>();

        void Panel(string id, string icon, string titleKey, params OverlayItem[] rows)
        {
            items.Add(new OverlayItem
            {
                Kind = ItemKind.Expander, Icon = icon,
                Label = () => Loc.T(titleKey),
                IsOn = () => _openPanel == id,
                Toggle = () => _openPanel = _openPanel == id ? "" : id,
            });
            items.AddRange(rows);
        }

        OverlayItem Row(string panel, Func<string> label, Func<string> value, Action? action = null, Func<string>? hint = null) => new()
        {
            Kind = ItemKind.Info, Label = label, Value = value, Action = action, Hint = hint,
            Visible = () => _openPanel == panel,
        };

        OverlayItem Key(string labelKey, string keys) =>
            Row(ShortcutsPanel, () => Loc.T(labelKey), () => keys);

        var desk = _monitors.ForRole(MonitorRole.Desk);
        var tv = _monitors.ForRole(MonitorRole.Tv);

        Panel(ShortcutsPanel, Glyphs.Keyboard, "Overlay.Info.Shortcuts",
            Key("Overlay.Info.Key.Toggle", "Shift+F1"),
            Row(ShortcutsPanel, () => Loc.T("Overlay.Info.Key.Monitor", desk.Length > 0 ? desk : Loc.T("Settings.Desk.Label")),
                () => "Ctrl+Alt+↓"),
            Row(ShortcutsPanel, () => Loc.T("Overlay.Info.Key.Monitor", tv.Length > 0 ? tv : Loc.T("Settings.Tv.Label")),
                () => "Ctrl+Alt+↑"),
            Key("Overlay.Info.Key.Arrows", "↑ ↓ ← →"),
            Key("Overlay.Footer.Confirm", "Enter"),
            Key("Overlay.Info.Key.Back", "Esc"),
            Key("Overlay.Info.Key.Tabs", "Tab / PgUp / PgDn"),
            Key("Overlay.Footer.Settings", "F2"),
            Key("Overlay.Info.Pad.Toggle", "Back + RB"),
            Key("Overlay.Info.Pad.Monitor", "Back + ↑ / ↓"),
            Key("Overlay.Info.Pad.Confirm", "A / B"),
            Key("Overlay.Info.Key.Tabs", "LT / RT"),
            Key("Overlay.Info.Pad.SettingsHold", "Back"));

        var profiles = _monitors.List();
        Panel(MonitorPanel, Glyphs.Monitor, "Overlay.Info.Monitor",
            Row(MonitorPanel, () => Loc.T("Overlay.Info.Switcher"),
                () => _locator.Path switch
                {
                    null => Loc.T("Settings.MonitorSwitcher.Searching"),
                    "" => Loc.T("Settings.MonitorSwitcher.Missing"),
                    var path => path,
                },
                () => OpenSwitcher(),
                () => Loc.T(string.IsNullOrEmpty(_locator.Path) ? "Overlay.Info.Download" : "Overlay.Footer.Open")),
            Row(MonitorPanel, () => Loc.T("Overlay.Info.Profiles"),
                () => profiles.Count > 0 ? string.Join(", ", profiles) : Loc.T("Overlay.Info.NoProfiles")),
            Row(MonitorPanel, () => Loc.T("Overlay.Info.Folder"), () => _monitors.ProfilesDir,
                () => ShellOpen.Open(_monitors.ProfilesDir), () => Loc.T("Overlay.Footer.Open")),
            Row(MonitorPanel, () => Loc.T("Overlay.Info.Roles"),
                () => $"{(desk.Length > 0 ? desk : "—")} · {(tv.Length > 0 ? tv : "—")}"));

        Panel(SystemPanel, Glyphs.Pc, "Overlay.Info.System",
            Row(SystemPanel, () => Loc.T("Overlay.Info.Version", typeof(OverlayContent).Assembly.GetName().Version?.ToString(3) ?? "?"),
                () => AppPaths.AppDir, () => ShellOpen.Open(AppPaths.AppDir), () => Loc.T("Overlay.Footer.Open")),
            Row(SystemPanel, () => Loc.T("Overlay.Info.Mode"),
                () => Loc.T(_host.InFullMode ? "Tooltip.Mode.Full" : "Tooltip.Mode.Standard")),
            Row(SystemPanel, () => Loc.T("Overlay.Info.Config"), () => AppPaths.ConfigDir,
                () => ShellOpen.Open(AppPaths.ConfigDir), () => Loc.T("Overlay.Footer.Open")));

        return items;
    }

    void OpenSwitcher()
    {
        var path = _locator.Path;
        var folder = string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(path);
        ShellOpen.Open(folder ?? MonitorSwitcherLocator.Website);
    }
}

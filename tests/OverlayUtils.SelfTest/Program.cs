using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using OverlayUtils.Core;
using OverlayUtils.Core.Audio;
using OverlayUtils.Core.Config;
using OverlayUtils.Core.Input;
using OverlayUtils.Core.Legacy;
using OverlayUtils.Core.Localization;
using OverlayUtils.Core.Monitors;
using OverlayUtils.Core.Overlay;
using OverlayUtils.Core.Platform;

var temp = Directory.CreateTempSubdirectory("overlayutils-selftest-").FullName;
AppPaths.OverrideConfigDir(Path.Combine(temp, "config"));
var failures = 0;
var passed = 0;

void Check(bool condition, string what, [CallerLineNumber] int line = 0)
{
    if (condition) { passed++; return; }
    failures++;
    Console.WriteLine($"FAIL (line {line}): {what}");
}

string RepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OverlayUtils.sln"))) dir = dir.Parent;
    return dir?.FullName ?? throw new InvalidOperationException("OverlayUtils.sln not found above the test binary");
}

// --- Localization -----------------------------------------------------------
{
    var en = Loc.AllStrings(CultureInfo.InvariantCulture);
    var it = Loc.AllStrings(CultureInfo.GetCultureInfo("it"));
    Check(en.Count > 0, "English strings load");
    foreach (var key in en.Keys.Except(it.Keys)) Check(false, $"key missing in Strings.it.resx: {key}");
    foreach (var key in it.Keys.Except(en.Keys)) Check(false, $"key only in Strings.it.resx: {key}");
    foreach (var (key, value) in en)
    {
        var holesEn = Regex.Matches(value, @"\{\d+\}").Select(m => m.Value).Distinct().Order();
        var holesIt = Regex.Matches(it.GetValueOrDefault(key, ""), @"\{\d+\}").Select(m => m.Value).Distinct().Order();
        Check(holesEn.SequenceEqual(holesIt), $"placeholders differ between languages: {key}");
    }

    // Every literal key used in the source must exist (catches typos at test time, not in the UI).
    var keyPattern = new Regex("\"((?:Menu|Notify|Dialog|Tooltip|Language|Overlay|Settings)\\.[A-Za-z0-9.]+)\"");
    foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories))
        foreach (Match m in keyPattern.Matches(File.ReadAllText(file)))
            Check(en.ContainsKey(m.Groups[1].Value), $"{Path.GetFileName(file)} uses unknown key {m.Groups[1].Value}");
    foreach (var code in Loc.Supported) Check(en.ContainsKey("Language." + code), $"Language.{code} exists");

    Loc.SetLanguage("it");
    Check(Loc.T("Menu.Exit") == "Esci", "Italian lookup");
    Check(Loc.T("Tooltip.Monitor", "TV") == "Monitor: TV", "formatting");
    Loc.SetLanguage("en");
    Check(Loc.T("Menu.Exit") == "Exit", "English lookup");
    Check(Loc.T("No.Such.Key") == "[No.Such.Key]", "missing key is visible, not a crash");
    Check(Loc.Resolve("it").Name == "it" && Loc.Resolve("en").Name == "en", "explicit language");
    Check(Loc.Resolve("auto").Name is "en" or "it", "auto resolves to a supported language");
}

// --- Config -----------------------------------------------------------------
{
    var path = Path.Combine(temp, "cfg", "config.json");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    // A config written by the Python AppUtili, including keys not ported yet.
    File.WriteAllText(path, """
        {
          "home_key_mode": "alttab",
          "controller_enabled": false,
          "monitor_desk": "Main",
          "hidden_tabs": ["Giochi"],
          "spotify_client_id": "0123456789abcdef0123456789abcdef",
          "discord_favorites": ["1", "2"]
        }
        """);
    var store = new ConfigStore(path);
    var cfg = store.Current;
    Check(cfg.HomeKeyMode == "alttab" && !cfg.ControllerEnabled && cfg.MonitorDesk == "Main", "Python config keys load");
    Check(cfg.Language == "auto" && cfg.NotificationsEnabled, "missing keys get defaults");
    Check(cfg.HiddenTabs.SequenceEqual(["Giochi"]), "lists load");

    var changed = 0;
    store.Changed += _ => changed++;
    store.Update(c => c.Language = "it");
    Check(changed == 1, "Changed raised once");
    var json = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
    Check(json.GetProperty("language").GetString() == "it", "update saved");
    Check(json.GetProperty("spotify_client_id").GetString() == "0123456789abcdef0123456789abcdef",
        "unknown keys survive a save (Spotify/Discord settings not ported yet)");
    Check(json.GetProperty("discord_favorites").GetArrayLength() == 2, "unknown arrays survive a save");

    File.WriteAllText(path, "{ not json");
    Check(new ConfigStore(path).Current.HomeKeyMode == HomeKeyModes.None, "corrupt file falls back to defaults");
    Check(File.Exists(path + ".bad"), "corrupt file is backed up");
}

// --- Migration --------------------------------------------------------------
{
    var legacy = Path.Combine(temp, "AppUtili");
    var target = Path.Combine(temp, "OverlayUtilsCfg");
    Directory.CreateDirectory(Path.Combine(legacy, "spotify_cache"));
    File.WriteAllText(Path.Combine(legacy, "config.json"), "{\"home_key_mode\":\"windows\"}");
    File.WriteAllText(Path.Combine(legacy, "spotify_token.json"), "{}");
    File.WriteAllText(Path.Combine(legacy, "spotify_cache", "a.jpg"), "x");
    File.WriteAllText(Path.Combine(legacy, "errori.log"), "old");

    Check(ConfigMigration.MigrateIfNeeded(legacy, target), "migrates the first time");
    Check(File.Exists(Path.Combine(target, "config.json")), "config copied");
    Check(File.Exists(Path.Combine(target, "spotify_token.json")), "tokens copied");
    Check(File.Exists(Path.Combine(target, "spotify_cache", "a.jpg")), "caches copied");
    Check(!File.Exists(Path.Combine(target, "errori.log")), "old log skipped");
    Check(File.Exists(Path.Combine(legacy, "config.json")), "old folder left in place");
    Check(!ConfigMigration.MigrateIfNeeded(legacy, target), "runs only once");
    Check(!ConfigMigration.MigrateIfNeeded(Path.Combine(temp, "missing"), Path.Combine(temp, "x")), "no legacy, no-op");
}

// --- Monitor profiles -------------------------------------------------------
{
    var profiles = Path.Combine(temp, "Profiles");
    Directory.CreateDirectory(profiles);
    foreach (var name in new[] { "Gaming", "Main", "TV" })
        File.WriteAllText(Path.Combine(profiles, name + ".xml"), "<x/>");
    var store = new ConfigStore(Path.Combine(temp, "mon", "config.json"));
    var monitors = new MonitorProfiles(store, new MonitorSwitcherLocator(store), profiles);

    Check(monitors.List().SequenceEqual(["Gaming", "Main", "TV"]), "profiles listed alphabetically");
    Check(monitors.ForRole(MonitorRole.Desk) == "Main", "desk guessed from name");
    Check(monitors.ForRole(MonitorRole.Tv) == "TV", "tv guessed from name");
    Check(monitors.Style("TV") == (MonitorProfiles.Colors[1], "TV"), "TV is purple with full label");
    Check(monitors.Style("Main") == (MonitorProfiles.Colors[0], "M"), "desk is blue with initial");
    Check(monitors.Style("Gaming").Color == MonitorProfiles.Colors[2], "others rotate");

    monitors.SetRole(MonitorRole.Tv, null);
    Check(monitors.ForRole(MonitorRole.Tv) == "", "role can be cleared");
    monitors.SetRole(MonitorRole.Desk, "Gaming");
    Check(monitors.ForRole(MonitorRole.Desk) == "Gaming", "explicit role wins");

    store.Update(c => c.MonitorProfile = "main"); // very old config value
    Check(monitors.Current == "Gaming", "legacy 'main' maps to the desk role");
}

// --- File search / paths ----------------------------------------------------
{
    var root = Path.Combine(temp, "search");
    Directory.CreateDirectory(Path.Combine(root, "a", "b"));
    Directory.CreateDirectory(Path.Combine(root, "x", "y", "z"));
    File.WriteAllText(Path.Combine(root, "a", "b", "MonitorSwitcher.exe"), "");
    File.WriteAllText(Path.Combine(root, "x", "y", "z", "Deep.exe"), "");
    Check(MonitorSwitcherLocator.SearchFolders([root], "MonitorSwitcher.exe", 2) is not null, "found 2 levels down");
    Check(MonitorSwitcherLocator.SearchFolders([root], "Deep.exe", 2) is null, "not found 3 levels down");

    var src = Path.Combine(temp, "copy-src");
    Directory.CreateDirectory(Path.Combine(src, "sub"));
    File.WriteAllText(Path.Combine(src, "sub", "f.txt"), "1");
    var dst = Path.Combine(temp, "copy-dst");
    Directory.CreateDirectory(dst);
    File.WriteAllText(Path.Combine(dst, "stale.txt"), "old");
    Check(FullModeInstaller.CopyInto(src, dst), "copy succeeds");
    Check(File.Exists(Path.Combine(dst, "sub", "f.txt")) && !File.Exists(Path.Combine(dst, "stale.txt")),
        "copy replaces the whole folder");
    Check(!Directory.Exists(dst + ".new"), "staging folder cleaned up");
    Check(FullModeInstaller.IsInside(Path.Combine(dst, "sub"), dst), "IsInside: child");
    Check(!FullModeInstaller.IsInside(dst + "2", dst), "IsInside: sibling with common prefix");

    Check(StartupRegistration.CommandFor("/apps/OverlayUtils.exe") == "\"/apps/OverlayUtils.exe\"",
        "standard exe starts directly");
    Check(StartupRegistration.CommandFor("/apps/OverlayUtils.UIAccess.exe").Contains("ShellExec_RunDLL"),
        "UIAccess exe starts through ShellExecute");
}

// --- Overlay layout ---------------------------------------------------------
{
    Check(new AppConfig().OverlaySize == OverlaySizes.Auto && new AppConfig().OverlaySide == OverlaySides.Left,
        "overlay defaults");
    var json = JsonSerializer.Serialize(new AppConfig(), ConfigStore.JsonOptions);
    Check(json.Contains("\"overlay_size\"") && json.Contains("\"overlay_side\""), "overlay keys are snake_case");

    Check(OverlayLayout.SizeFactor("normal", "TV", "TV") == 1.0, "normal stays normal on TV");
    Check(OverlayLayout.SizeFactor("large", "Desk", "TV") == 1.35, "large is large everywhere");
    Check(OverlayLayout.SizeFactor("auto", "TV", "TV") == 1.35, "auto is large on the TV profile");
    Check(OverlayLayout.SizeFactor("auto", "Desk", "TV") == 1.0, "auto is normal elsewhere");
    Check(OverlayLayout.SizeFactor("auto", "", "") == 1.0, "auto without a TV profile is normal");
    Check(OverlayLayout.SizeFactor("bogus", "TV", "TV") == 1.35, "unknown setting behaves as auto");

    var hd = new RectI(0, 0, 1920, 1040);
    Check(OverlayLayout.Place(hd, 1.0, 1.0, "left") == new RectI(36, 36, 456, 756), "left placement");
    Check(OverlayLayout.Place(hd, 1.0, 1.0, "right") == new RectI(1464, 36, 1884, 756), "right placement");
    Check(OverlayLayout.Place(new RectI(0, 0, 3840, 2100), 1.5, 1.35, "right") == new RectI(2935, 54, 3786, 1512),
        "dpi and large factor multiply, rounded away from zero");
    Check(OverlayLayout.Place(new RectI(0, 0, 1920, 1032), 1.0, 1.35, "left").Height == 960,
        "height clamped to the work area minus both margins");
    Check(OverlayLayout.Place(new RectI(-1920, 0, 0, 1040), 1.0, 1.0, "left").Left == -1884,
        "monitors left of the primary have negative coordinates");
}

// --- Overlay navigator ------------------------------------------------------
{
    var clock = new TestClock { Now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) };
    var log = new List<string>();
    var hiddenRow = true;
    var desktopHidden = false;
    OverlayItem[] controls =
    [
        new() { Kind = ItemKind.Action, Label = () => "Hidden", Visible = () => !hiddenRow, Action = () => log.Add("hidden") },
        new() { Kind = ItemKind.Action, Label = () => "Desktop", Description = () => "desktop-desc",
                Visible = () => !desktopHidden, Action = () => log.Add("desktop") },
        new() { Kind = ItemKind.Segmented, Label = () => "Power", Description = () => "power-desc", Confirm = true,
                Options = () =>
                [
                    new OverlayOption(() => "Sleep", () => log.Add("Sleep")),
                    new OverlayOption(() => "Restart", () => log.Add("Restart")),
                    new OverlayOption(() => "Off", () => log.Add("Off")),
                ] },
        new() { Kind = ItemKind.Choice, Label = () => "Pick", Description = () => "pick-desc", Step = d => log.Add("step" + d) },
    ];
    OverlayItem[] games = [new() { Kind = ItemKind.Action, Label = () => "Game", Action = () => log.Add("game") }];
    OverlayItem[] settings =
    [
        new() { Kind = ItemKind.Toggle, Label = () => "T", Toggle = () => log.Add("toggle") },
        new() { Kind = ItemKind.Expander, Label = () => "E", Toggle = () => log.Add("expand") },
    ];
    OverlayItem[] info =
    [
        new() { Kind = ItemKind.Info, Label = () => "NoAction" },
        new() { Kind = ItemKind.Info, Label = () => "WithAction", Action = () => log.Add("info") },
    ];
    var nav = new OverlayNavigator(
        [new OverlayTab("Controlli", () => "Controls", () => controls), new OverlayTab("Giochi", () => "Games", () => games)],
        () => settings, () => info, clock);

    void Run(NavResult r) => r.Action?.Invoke();

    nav.Reset([]);
    Check(nav.Page == OverlayPage.Tabs && nav.Index == 1 && nav.Description() == "desktop-desc",
        "reset selects the first selectable row");
    nav.Reset(["Controlli", "Giochi"]);
    Check(nav.VisibleTabs.Count == 1 && nav.VisibleTabs[0].Id == "Controlli", "Controlli is never hidden");
    nav.Reset([]);
    Check(nav.VisibleTabs.Count == 2, "no hidden tabs keeps both");

    Check(nav.Handle(OverlayCommand.Down).Effect == NavEffect.Redraw && nav.Index == 2, "Down selects Power");
    nav.Handle(OverlayCommand.Down);
    Check(nav.Index == 3, "Down selects Pick");
    nav.Handle(OverlayCommand.Down);
    Check(nav.Index == 1, "Down wraps skipping the hidden row");
    nav.Handle(OverlayCommand.Up);
    Check(nav.Index == 3, "Up wraps backwards");
    nav.Handle(OverlayCommand.Up);

    Check(nav.Index == 2 && nav.Segment == 0, "on Power at the first option");
    nav.Handle(OverlayCommand.Right);
    nav.Handle(OverlayCommand.Right);
    Check(nav.Segment == 2, "Right twice moves to option 2");
    nav.Handle(OverlayCommand.Right);
    Check(nav.Segment == 2, "segment cursor is clamped");

    var r1 = nav.Handle(OverlayCommand.A);
    Check(r1.Effect == NavEffect.Redraw && nav.IsPending(2, 2) && log.Count == 0, "first A arms the confirm");
    nav.Handle(OverlayCommand.Left);
    Check(!nav.IsPending(2, 2) && nav.Segment == 1, "moving the cursor clears the confirm");
    nav.Handle(OverlayCommand.Right);
    nav.Handle(OverlayCommand.A);
    clock.Now += TimeSpan.FromSeconds(2.9);
    var r2 = nav.Handle(OverlayCommand.A);
    Check(r2.Effect == NavEffect.HideAndRun, "second A within 3 s runs and hides");
    Run(r2);
    Check(log.SequenceEqual(["Off"]), "the confirmed option ran");
    log.Clear();

    nav.Handle(OverlayCommand.A);
    clock.Now += TimeSpan.FromSeconds(3.1);
    var r3 = nav.Handle(OverlayCommand.A);
    Check(r3.Effect == NavEffect.Redraw && nav.IsPending(2, 2) && log.Count == 0, "late second A re-arms instead of running");

    nav.Handle(OverlayCommand.Down);
    Check(!nav.IsPending(2, 2), "moving rows clears the confirm");
    nav.Handle(OverlayCommand.Up);
    nav.Handle(OverlayCommand.A);
    Check(!nav.ExpirePending(), "ExpirePending is false before the deadline");
    clock.Now += TimeSpan.FromSeconds(3.1);
    Check(nav.ExpirePending() && !nav.IsPending(2, 2) && !nav.ExpirePending(), "ExpirePending clears once after the deadline");

    // The segment cursor starts on the active option.
    var activeNav = new OverlayNavigator(
        [new OverlayTab("Controlli", () => "C", () =>
        [
            new OverlayItem
            {
                Kind = ItemKind.Segmented, Label = () => "S",
                Options = () =>
                [
                    new OverlayOption(() => "a", () => { }),
                    new OverlayOption(() => "b", () => { }, Active: () => true),
                ],
            },
        ])], () => [], () => [], clock);
    activeNav.Reset([]);
    Check(activeNav.Segment == 1, "segment cursor starts on the active option");

    nav.Handle(OverlayCommand.Down);
    Check(nav.Index == 3, "on Pick");
    var rl = nav.Handle(OverlayCommand.Left);
    Check(rl.Effect == NavEffect.RunHere && log.Count == 0, "Left on a choice is deferred");
    Run(rl);
    Check(log.SequenceEqual(["step-1"]), "Left steps -1");
    log.Clear();
    Run(nav.Handle(OverlayCommand.A));
    Check(log.SequenceEqual(["step1"]), "A on a choice without Apply steps +1");
    log.Clear();

    var rs = nav.Handle(OverlayCommand.Settings);
    Check(rs.Effect == NavEffect.Redraw && nav.Page == OverlayPage.Settings && nav.Index == 0, "Settings opens the settings page");
    Check(nav.Handle(OverlayCommand.WheelDown).Effect == NavEffect.Redraw && nav.Index == 1, "WheelDown moves on Settings");
    Check(nav.Handle(OverlayCommand.NextTab).Effect == NavEffect.None, "NextTab does nothing on Settings");
    Check(nav.Handle(OverlayCommand.Right).Effect == NavEffect.RunHere && nav.Handle(OverlayCommand.Left).Effect == NavEffect.None,
        "closed expander: Right opens, Left does nothing");
    nav.Handle(OverlayCommand.B);
    Check(nav.Page == OverlayPage.Tabs && nav.Index == 3, "B returns to the same tab row");
    Check(nav.Handle(OverlayCommand.WheelDown).Effect == NavEffect.None, "WheelDown does nothing on Tabs");
    Check(nav.Handle(OverlayCommand.B).Effect == NavEffect.Hide, "B on Tabs hides");

    nav.Reset([]);
    Check(nav.Handle(OverlayCommand.NextTab).Effect == NavEffect.Redraw && nav.CurrentTab.Id == "Giochi", "NextTab goes to Giochi");
    nav.Handle(OverlayCommand.NextTab);
    Check(nav.CurrentTab.Id == "Controlli" && nav.Index == 1, "NextTab wraps to Controlli");
    nav.Handle(OverlayCommand.PrevTab);
    Check(nav.CurrentTab.Id == "Giochi", "PrevTab wraps backwards");
    nav.Reset([]);

    nav.Handle(OverlayCommand.Settings);
    Run(nav.Handle(OverlayCommand.A));
    Check(log.SequenceEqual(["toggle"]), "A on a toggle runs it");
    log.Clear();
    nav.Handle(OverlayCommand.Settings);
    Check(nav.Page == OverlayPage.Tabs, "Settings command returns to Tabs");

    var infoNav = new OverlayNavigator(
        [new OverlayTab("Controlli", () => "C", () =>
        [
            new OverlayItem { Kind = ItemKind.Action, Label = () => "About", OpensInfo = true, Action = () => log.Add("never") },
        ])], () => [], () => info, clock);
    infoNav.Reset([]);
    var ri = infoNav.Handle(OverlayCommand.A);
    Check(ri.Effect == NavEffect.Redraw && infoNav.Page == OverlayPage.Info && infoNav.Index == 1,
        "OpensInfo opens the Info page on the selectable row");
    infoNav.Handle(OverlayCommand.Down);
    Check(infoNav.Index == 1, "the info row without an action is never selected");
    Check(infoNav.Handle(OverlayCommand.A).Effect == NavEffect.HideAndRun, "A on an info row with an action runs it");
    infoNav.Handle(OverlayCommand.B);
    Check(infoNav.Page == OverlayPage.Settings, "B on Info goes to Settings");

    // A visibility change moves the selection.
    nav.Reset([]);
    Check(nav.Index == 1, "back on Desktop");
    desktopHidden = true;
    nav.Refresh();
    Check(nav.Index == 2 && nav.Description() == "power-desc", "hidden selection moves to the nearest selectable row");
    desktopHidden = false;
    hiddenRow = false;
    nav.Reset([]);
    Check(nav.Index == 0, "a visible first row is selected on reset");
    hiddenRow = true;
    nav.Refresh();
    Check(nav.Index == 1, "Refresh moves below when nothing is selectable above");
    nav.Handle(OverlayCommand.Down);
    nav.Handle(OverlayCommand.Down);
    Check(nav.Index == 3, "on Pick again");

    // Mouse.
    var rc = nav.Click(2, 1);
    Check(nav.Index == 2 && nav.Segment == 1 && rc.Effect == NavEffect.Redraw && nav.IsPending(2, 1),
        "Click selects the row and segment, then acts as A");

    log.Clear();
    var rc2 = nav.Click(2, 1);
    Check(rc2.Effect == NavEffect.HideAndRun, "second Click on the same segment fires the confirm");
    Run(rc2);
    Check(log.SequenceEqual(["Restart"]), "the clicked option ran");
    log.Clear();
    nav.Click(2, 1);
    var rc3 = nav.Click(2, 2);
    Check(rc3.Effect == NavEffect.Redraw && nav.IsPending(2, 2) && !nav.IsPending(2, 1) && log.Count == 0,
        "Click on another segment re-arms instead of running");

    // Footer.
    var footer = nav.Footer();
    Check(footer.Any(f => f.Keys == "Esc" && f.Label == Loc.T("Overlay.Footer.Close")), "footer: Esc closes on Tabs");
    Check(footer.Any(f => f.Keys == "← →" && f.Label == Loc.T("Overlay.Footer.Change")), "footer: arrows change on a segmented row");
    Check(footer.Any(f => f.Keys == "F2" && f.Label == Loc.T("Overlay.Footer.Settings")), "footer: F2 opens settings");
    Check(footer[0].Keys == "Enter" && footer[0].Label == Loc.T("Overlay.Footer.Confirm"), "footer: Enter confirms");
    nav.Handle(OverlayCommand.Settings);
    Check(nav.Footer().Any(f => f.Keys == "Esc" && f.Label == Loc.T("Overlay.Footer.Back")), "footer: Esc goes back on Settings");
}

// --- Overlay platform -------------------------------------------------------
{
    foreach (var cls in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW" })
        Check(ShellWindows.IsShellClass(cls), $"{cls} is a shell window");
    Check(!ShellWindows.IsShellClass("UnityWndClass") && !ShellWindows.IsShellClass(""), "games are not shell windows");

    byte[] palette = [0x99, 0xEB, 0xFF, 0, 0x4C, 0xC2, 0xFF, 0, 0, 0x78, 0xD4, 0];
    Check(AccentPalette.LightVariant(palette) == ((byte)0x4C, (byte)0xC2, (byte)0xFF), "light accent is entry 1");
    Check(AccentPalette.LightVariant([1, 2, 3]) is null && AccentPalette.LightVariant(null) is null, "short palette ignored");

    var blueAccent = ((byte)0x4C, (byte)0xC2, (byte)0xFF);
    byte[] blue = [0x99, 0xEB, 0xFF, 0, 0x60, 0xCD, 0xFF, 0, 0, 0x78, 0xD4, 0];
    Check(AccentPalette.ForDarkBackground(blue) == ((byte)0x60, (byte)0xCD, (byte)0xFF), "readable light accent (entry 1) is used");
    byte[] grey = [0xC4, 0xC4, 0xC4, 0, 0x59, 0x59, 0x59, 0, 0x4C, 0x4C, 0x4C, 0];
    Check(AccentPalette.ForDarkBackground(grey) == ((byte)0xC4, (byte)0xC4, (byte)0xC4), "dim entry 1 falls back to entry 0");
    byte[] dark = [0x6B, 0x6B, 0x6B, 0, 0x59, 0x59, 0x59, 0, 0x4C, 0x4C, 0x4C, 0];
    Check(AccentPalette.ForDarkBackground(dark) == AccentPalette.Fallback, "unreadable palette uses #4CC2FF");
    Check(AccentPalette.Fallback == blueAccent, "accent fallback is #4CC2FF");
    Check(AccentPalette.ForDarkBackground(null) == AccentPalette.Fallback
        && AccentPalette.ForDarkBackground([1, 2, 3]) == AccentPalette.Fallback, "missing palette uses #4CC2FF");
    Check(Math.Abs(AccentPalette.Contrast((255, 255, 255), (0, 0, 0)) - 21) < 1e-9, "WCAG contrast white/black is 21:1");
}

// --- Overlay content --------------------------------------------------------
{
    var host = new FakeHost();
    var altF4 = 0;
    var profilesDir = Path.Combine(temp, "ovc-profiles");
    Directory.CreateDirectory(profilesDir);
    void SetProfiles(params string[] names)
    {
        foreach (var f in Directory.GetFiles(profilesDir)) File.Delete(f);
        foreach (var n in names) File.WriteAllText(Path.Combine(profilesDir, n + ".xml"), "<x/>");
    }
    var store = new ConfigStore(Path.Combine(temp, "ovc", "config.json"));
    var monitors = new MonitorProfiles(store, new MonitorSwitcherLocator(store), profilesDir);
    var content = new OverlayContent(store, monitors, new MonitorSwitcherLocator(store), host, () => altF4++);
    OverlayNavigator NewNav()
    {
        var n = new OverlayNavigator(content.Tabs, content.Settings, content.Info);
        content.ResetTransient();
        n.Reset([]);
        return n;
    }
    OverlayItem Row(IReadOnlyList<OverlayItem> items, string label) => items.First(i => i.Label() == label);
    void RunAction(NavResult r) => r.Action?.Invoke();
    OverlayItem FullModeRow() => content.Settings().First(i =>
        i.Kind == ItemKind.Segmented && i.Options().Count == 1
        && i.Options()[0].Label() is var l && (l == Loc.T("Settings.FullMode.Enable") || l == Loc.T("Settings.FullMode.Disable")));

    // No profiles: Monitor hidden, Big Picture first, description follows the selection.
    monitors.InvalidateCache();
    var nav = NewNav();
    var monitorRow = content.Controls()[0];
    Check(monitorRow.Label() == Loc.T("Overlay.Monitor.Label") && !monitorRow.Visible(), "Monitor row hidden without profiles");
    Check(nav.Selected?.Label() == Loc.T("Overlay.BigPicture.Label"), "Big Picture is selected when Monitor is hidden");
    Check(nav.Description() == Loc.T("Overlay.BigPicture.Desc"), "description is Big Picture's, not Monitor's");
    Check(content.Tabs.Count == 1 && content.Tabs[0].Id == OverlayContent.ControlsTabId, "only the Controls tab exists");

    // Three profiles: segmented, cursor on the current profile.
    SetProfiles("Gaming", "Main", "TV");
    monitors.InvalidateCache();
    store.Update(c => c.MonitorProfile = "Main");
    nav = NewNav();
    var mon3 = content.Controls()[0];
    Check(mon3.Kind == ItemKind.Segmented && mon3.Options().Count == 3 && mon3.Visible(), "3 profiles: segmented with 3 options");
    Check(nav.Selected?.Label() == Loc.T("Overlay.Monitor.Label") && nav.Segment == 1, "cursor starts on the current profile");
    RunAction(nav.Handle(OverlayCommand.A));
    Check(host.Calls.SequenceEqual(["SwitchMonitor:Main"]), "segment action switches the monitor");
    host.Calls.Clear();

    // Four profiles: choice with a pick that starts on the current one and wraps.
    SetProfiles("A", "B", "C", "D");
    monitors.InvalidateCache();
    store.Update(c => c.MonitorProfile = "A");
    nav = NewNav();
    var mon4 = content.Controls()[0];
    Check(mon4.Kind == ItemKind.Choice, "4 profiles: choice");
    Check(mon4.Value() == Loc.T("Overlay.Monitor.Active", "A"), "pick shows the current profile as active");
    RunAction(nav.Handle(OverlayCommand.Right));
    var applied = nav.Handle(OverlayCommand.A);
    Check(applied.Effect == NavEffect.HideAndRun, "A on the Monitor choice hides and runs");
    RunAction(applied);
    Check(host.Calls.SequenceEqual(["SwitchMonitor:B"]), "pick moved to the second profile and was applied");
    host.Calls.Clear();
    content.ResetTransient();
    Check(content.Controls()[0].Value() == Loc.T("Overlay.Monitor.Active", "A"), "ResetTransient clears the pick");
    store.Update(c => c.MonitorProfile = "");
    Check(content.Controls()[0].Value() == "A", "pick starts on the first profile when none is current");
    content.Controls()[0].Step!(-1);
    Check(content.Controls()[0].Value() == "D", "pick wraps backwards");
    content.ResetTransient();

    // Close window.
    var close = Row(content.Controls(), Loc.T("Overlay.CloseWindow.Label"));
    Check(close.Hint?.Invoke() == "Alt+F4", "Close window shows its keycap");
    nav = NewNav();
    while (nav.Selected?.Label() != close.Label()) nav.Handle(OverlayCommand.Down);
    var closeRun = nav.Handle(OverlayCommand.A);
    Check(closeRun.Effect == NavEffect.HideAndRun, "Close window hides then runs");
    host.PreviousWindowIsForeground = false;
    RunAction(closeRun);
    Check(altF4 == 0, "no Alt+F4 when the previous window is not in the foreground");
    host.PreviousWindowIsForeground = true;
    RunAction(closeRun);
    Check(altF4 == 1, "Alt+F4 when the previous window is back in the foreground");

    // Power.
    var power = Row(content.Controls(), Loc.T("Overlay.Power.Label"));
    Check(power.Kind == ItemKind.Segmented && power.Confirm && power.Options().Count == 3, "Power: 3 options with confirm");

    // Settings.
    var settings = content.Settings();
    Check(!settings.Any(i => i.Label().Contains("Spotify") || i.Label().Contains("Discord")), "no Spotify/Discord rows yet");
    var size = Row(settings, Loc.T("Settings.Size.Label"));
    size.Options().First(o => o.Label() == Loc.T("Settings.Size.Large")).Action();
    Check(store.Current.OverlaySize == "large" && size.Options().All(o => o.Stay), "Size Large is saved and stays open");
    Check(size.Options().First(o => o.Label() == Loc.T("Settings.Size.Large")).Active!(), "active size is marked");
    var position = Row(settings, Loc.T("Settings.Position.Label"));
    position.Options().First(o => o.Label() == Loc.T("Settings.Position.Right")).Action();
    Check(store.Current.OverlaySide == "right", "Position Right is saved");

    host.InFullMode = false;
    var full = FullModeRow();
    Check(full.Confirm && full.Label().EndsWith(Loc.T("Settings.FullMode.Off")), "full mode off: confirm, label says Off");
    full.Options()[0].Action();
    Check(host.Calls.SequenceEqual(["SwitchFullMode"]), "full mode option calls the host once");
    host.Calls.Clear();
    host.InFullMode = true;
    full = FullModeRow();
    Check(full.Options()[0].Label() == Loc.T("Settings.FullMode.Disable")
          && full.Label().EndsWith(Loc.T("Settings.FullMode.On")), "full mode on: Disable option, label says On");

    var settingsNav = new OverlayNavigator(content.Tabs, content.Settings, content.Info);
    settingsNav.Reset([]);
    settingsNav.Handle(OverlayCommand.Settings);
    while (settingsNav.Selected?.Label() != Loc.T("Menu.Language")) settingsNav.Handle(OverlayCommand.Down);
    Check(store.Current.Language == "auto", "language starts on auto");
    RunAction(settingsNav.Handle(OverlayCommand.Right));
    Check(store.Current.Language == "en", "Right on Language cycles auto to en");
    RunAction(settingsNav.Handle(OverlayCommand.Right));
    Check(store.Current.Language == "it", "then en to it");
    RunAction(settingsNav.Handle(OverlayCommand.Right));
    Check(store.Current.Language == "auto", "then back to auto");
    Loc.SetLanguage("en");

    Row(content.Settings(), Loc.T("Menu.Startup")).Toggle!();
    Check(host.Calls.SequenceEqual(["ToggleStartup"]), "Start with Windows toggles through the host");
    host.Calls.Clear();
    var tabRow = content.Settings()[0];
    tabRow.Toggle!();
    Check(tabRow.IsOn() && store.Current.HiddenTabs.Count == 0, "the Controls tab toggle stays on");
    Row(content.Settings(), Loc.T("Menu.Notifications")).Toggle!();
    Check(!store.Current.NotificationsEnabled, "Notifications toggle flips the config");
    var exit = Row(content.Settings(), Loc.T("Settings.Exit.Label"));
    exit.Options()[0].Action();
    Check(exit.Confirm && host.Calls.SequenceEqual(["Exit"]), "Exit asks to confirm and calls the host");
    host.Calls.Clear();
    Row(content.Settings(), Loc.T("Settings.Desk.Label")).Step!(1);
    Check(monitors.ForRole(MonitorRole.Desk) == "B", "Desk choice moves the role to the next profile");
    var switcher = Row(content.Settings(), Loc.T("Settings.MonitorSwitcher.Label", Loc.T("Settings.MonitorSwitcher.Searching")));
    Check(switcher.Options()[0].Stay, "MonitorSwitcher row stays open");
    switcher.Options()[0].Action();
    Check(host.Calls.SequenceEqual(["ChooseMonitorSwitcher"]), "Choose… calls the host");
    host.Calls.Clear();

    // Information row opens the Info page.
    var infoNav = new OverlayNavigator(content.Tabs, content.Settings, content.Info);
    infoNav.Reset([]);
    infoNav.Handle(OverlayCommand.Settings);
    while (infoNav.Selected?.Label() != Loc.T("Settings.Info.Label")) infoNav.Handle(OverlayCommand.Down);
    infoNav.Handle(OverlayCommand.A);
    Check(infoNav.Page == OverlayPage.Info, "A on Information opens the Info page");

    // Info panels.
    content.ResetTransient();
    var shortcutsTitle = Loc.T("Overlay.Info.Shortcuts");
    var monitorTitle = Loc.T("Overlay.Info.Monitor");
    bool Open(string title) => content.Info().First(i => i.Kind == ItemKind.Expander && i.Label() == title).IsOn();
    IEnumerable<string> ShownInfo() => content.Info().Where(i => i.Kind == ItemKind.Info && i.Visible()).Select(i => i.Label());
    Check(Open(shortcutsTitle) && !Open(monitorTitle), "ResetTransient opens Shortcuts only");
    Check(ShownInfo().Count() >= 8 && !ShownInfo().Contains(Loc.T("Overlay.Info.Profiles")), "only Shortcuts rows are visible");
    content.Info().First(i => i.Label() == monitorTitle).Toggle!();
    Check(!Open(shortcutsTitle) && Open(monitorTitle), "opening Monitor closes Shortcuts");
    Check(ShownInfo().Contains(Loc.T("Overlay.Info.Profiles")) && !ShownInfo().Contains(Loc.T("Overlay.Info.Key.Tabs")),
        "Monitor rows shown, Shortcuts rows hidden");
    content.Info().First(i => i.Label() == monitorTitle).Toggle!();
    Check(!ShownInfo().Any(), "toggling the open panel closes it");
    content.Info().First(i => i.Label() == Loc.T("Overlay.Info.System")).Toggle!();
    Check(ShownInfo().Count() == 3 && content.Info().Count(i => i.Kind == ItemKind.Expander) == 3, "System panel has its rows");

    // Italian.
    Loc.SetLanguage("it");
    Check(content.Tabs[0].Title() == "Controlli" && content.Controls().Any(i => i.Label() == "Energia"), "Italian tab and Power labels");
    Check(content.Controls().Any(i => i.Label() == "Mostra desktop") && content.Controls().Any(i => i.Label() == "Chiudi finestra"),
        "Italian window rows");
    Loc.SetLanguage("en");
    Check(content.Controls().Any(i => i.Label() == "Power"), "English again");
}

// --- Pad interpreter --------------------------------------------------------
{
    var clock = new TestClock { Now = DateTimeOffset.UnixEpoch };
    PadInterpreter New(bool open = false) => new(clock) { OverlayOpen = open };
    PadAction Cmd(OverlayCommand c) => new(PadActionKind.Command, c);
    bool Is(IReadOnlyList<PadAction> got, params PadAction[] want) => got.SequenceEqual(want);
    var toggle = new PadAction(PadActionKind.ToggleOverlay);

    var p = New();
    Check(Is(p.ButtonDown(1, PadButton.Back)) && Is(p.ButtonDown(1, PadButton.RB)), "chord press fires nothing");
    Check(Is(p.ButtonUp(1, PadButton.RB), toggle), "Back+RB toggles on release");
    Check(Is(p.ButtonUp(1, PadButton.Back)), "second release fires nothing");
    p = New();
    p.ButtonDown(1, PadButton.RB); p.ButtonDown(1, PadButton.Back);
    Check(Is(p.ButtonUp(1, PadButton.Back), toggle), "RB first, Back released first also toggles");
    p = New();
    p.ButtonDown(1, PadButton.Back); p.ButtonDown(1, PadButton.RB); p.ButtonDown(1, PadButton.A);
    Check(Is(p.ButtonUp(1, PadButton.RB)), "another button disarms the chord");
    p = New();
    p.ButtonDown(1, PadButton.Back); p.ButtonDown(2, PadButton.RB);
    Check(Is(p.ButtonUp(2, PadButton.RB)) && Is(p.ButtonUp(1, PadButton.Back)), "chord split across pads does nothing");

    p = New();
    p.ButtonDown(1, PadButton.Back);
    Check(Is(p.ButtonDown(1, PadButton.DpadUp), new PadAction(PadActionKind.MonitorTv)), "Back+Up -> TV");
    p.ButtonUp(1, PadButton.DpadUp);
    Check(Is(p.ButtonDown(1, PadButton.DpadUp), new PadAction(PadActionKind.MonitorTv)), "Back+Up again -> TV again");
    Check(Is(p.ButtonDown(1, PadButton.DpadDown), new PadAction(PadActionKind.MonitorDesk)), "Back+Down -> Desk");
    p = New(); p.CombosEnabled = false; p.ButtonDown(1, PadButton.Back);
    Check(Is(p.ButtonDown(1, PadButton.DpadUp)), "combos off: no monitor switch");
    p = New(open: true); p.ButtonDown(1, PadButton.Back);
    Check(Is(p.ButtonDown(1, PadButton.DpadUp), Cmd(OverlayCommand.Up)), "overlay open: Back+Up navigates");

    p = New(open: true);
    Check(Is(p.ButtonDown(1, PadButton.DpadDown), Cmd(OverlayCommand.Down)), "D-pad down navigates");
    clock.Now += TimeSpan.FromSeconds(0.39);
    Check(Is(p.Tick()), "no repeat before 0.4 s");
    clock.Now += TimeSpan.FromSeconds(0.02);
    Check(Is(p.Tick(), Cmd(OverlayCommand.Down)), "first repeat after 0.4 s");
    clock.Now += TimeSpan.FromSeconds(0.12);
    Check(Is(p.Tick(), Cmd(OverlayCommand.Down)), "then every 0.12 s");
    p.ButtonUp(1, PadButton.DpadDown); clock.Now += TimeSpan.FromSeconds(1);
    Check(Is(p.Tick()), "release stops the repeat");
    p.ButtonDown(1, PadButton.DpadRight); clock.Now += TimeSpan.FromSeconds(0.41);
    Check(Is(p.Tick(), Cmd(OverlayCommand.RightRepeat)), "held right repeats as RightRepeat");
    p.ButtonUp(1, PadButton.DpadRight);

    p = New(open: true);
    Check(Is(p.Axis(1, PadAxis.LeftX, -20000), Cmd(OverlayCommand.Left)), "stick left navigates");
    Check(Is(p.Axis(1, PadAxis.LeftX, -12000)), "stick between thresholds stays");
    Check(Is(p.Axis(1, PadAxis.LeftX, -5000)), "stick release fires nothing");
    clock.Now += TimeSpan.FromSeconds(1);
    Check(Is(p.Tick()), "released stick does not repeat");

    p = New(open: true);
    Check(Is(p.Axis(1, PadAxis.LeftTrigger, 17000), Cmd(OverlayCommand.PrevTab)), "LT -> previous tab");
    Check(Is(p.Axis(1, PadAxis.LeftTrigger, 15000)) && Is(p.Axis(1, PadAxis.LeftTrigger, 17000)), "trigger noise above release threshold ignored");
    p.Axis(1, PadAxis.LeftTrigger, 7000);
    Check(Is(p.Axis(1, PadAxis.LeftTrigger, 17000), Cmd(OverlayCommand.PrevTab)), "LT again after release");
    Check(Is(p.Axis(1, PadAxis.RightTrigger, 17000), Cmd(OverlayCommand.NextTab)), "RT -> next tab");

    p = New(open: true);
    p.ButtonDown(1, PadButton.Back); clock.Now += TimeSpan.FromSeconds(0.6);
    Check(Is(p.Tick(), Cmd(OverlayCommand.Settings)), "hold Back 0.6 s -> Settings");
    Check(Is(p.Tick()), "Settings fires once per hold");
    p.ButtonDown(1, PadButton.RB);
    Check(Is(p.ButtonUp(1, PadButton.RB)), "consumed Back hold does not arm the chord");
    p.ButtonUp(1, PadButton.Back);
    p.ButtonDown(1, PadButton.Back); p.ButtonDown(1, PadButton.RB);
    Check(Is(p.ButtonUp(1, PadButton.RB), toggle), "after releasing Back the chord works again");
    p.ButtonUp(1, PadButton.Back);

    p = New(open: true);
    p.ButtonDown(1, PadButton.Back); clock.Now += TimeSpan.FromSeconds(0.3);
    Check(Is(p.ButtonDown(1, PadButton.DpadDown), Cmd(OverlayCommand.Down)), "Back+Down while open navigates");
    clock.Now += TimeSpan.FromSeconds(0.5);
    Check(!p.Tick().Contains(Cmd(OverlayCommand.Settings)), "Back with another button is not a Settings hold");

    p = New(open: true);
    p.ButtonDown(1, PadButton.DpadDown); p.PadRemoved(1); clock.Now += TimeSpan.FromSeconds(1);
    Check(Is(p.Tick()), "removed pad stops repeating");
    p = New(); p.ButtonDown(1, PadButton.Back); p.ButtonDown(1, PadButton.RB); p.PadRemoved(1);
    Check(Is(p.ButtonUp(1, PadButton.RB)) && Is(p.ButtonUp(1, PadButton.Back)), "removed pad forgets its chord");

    foreach (var open in new[] { false, true })
    {
        p = New(open);
        Check(Is(p.ButtonDown(1, PadButton.Guide)) && Is(p.ButtonUp(1, PadButton.Guide)), "Guide ignored");
        var acts = new List<PadAction>();
        foreach (var b in new[] { PadButton.Start, PadButton.Back, PadButton.LB, PadButton.RB }) acts.AddRange(p.ButtonDown(1, b));
        foreach (var b in new[] { PadButton.RB, PadButton.LB, PadButton.Back, PadButton.Start }) acts.AddRange(p.ButtonUp(1, b));
        Check(acts.Count == 0, "Moonlight quit chord does nothing");
    }

    p = New(); p.ButtonDown(3, PadButton.A); p.ButtonUp(3, PadButton.A);
    Check(p.LastUsedPad == 3, "last used pad tracked");

    Check(PadInterpreter.BatteryLevel(1, 5) == 0 && PadInterpreter.BatteryLevel(1, 40) == 1
        && PadInterpreter.BatteryLevel(3, 70) == 2 && PadInterpreter.BatteryLevel(1, 71) == 3, "battery levels");
    Check(PadInterpreter.BatteryLevel(4, -1) == 3 && PadInterpreter.BatteryLevel(2, -1) == null
        && PadInterpreter.BatteryLevel(1, -1) == null && PadInterpreter.BatteryLevel(0, 50) == null, "battery unknown cases");
}

// --- Pad interpreter: duplicate pads and stale Back hold --------------------
{
    var clock = new TestClock { Now = DateTimeOffset.UnixEpoch };
    var toggle = new PadAction(PadActionKind.ToggleOverlay);
    var down = new PadAction(PadActionKind.Command, OverlayCommand.Down);

    // One physical pad seen twice (DS4Windows/ViGEm): every press arrives on two pads.
    var p = new PadInterpreter(clock);
    var acts = new List<PadAction>();
    foreach (var pad in new uint[] { 1, 2 }) { p.ButtonDown(pad, PadButton.Back); p.ButtonDown(pad, PadButton.RB); }
    foreach (var pad in new uint[] { 1, 2 }) acts.AddRange(p.ButtonUp(pad, PadButton.RB));
    Check(acts.SequenceEqual([toggle]), "duplicate pad toggles the overlay once");
    foreach (var pad in new uint[] { 1, 2 }) p.ButtonUp(pad, PadButton.Back);
    clock.Now += TimeSpan.FromSeconds(0.3);
    p.ButtonDown(1, PadButton.Back); p.ButtonDown(1, PadButton.RB);
    Check(p.ButtonUp(1, PadButton.RB).SequenceEqual([toggle]), "a later toggle still works");
    p.ButtonUp(1, PadButton.Back);

    p = new PadInterpreter(clock) { OverlayOpen = true };
    acts = [.. p.ButtonDown(1, PadButton.DpadDown), .. p.ButtonDown(2, PadButton.DpadDown)];
    Check(acts.SequenceEqual([down]), "duplicate pad moves one row");
    clock.Now += TimeSpan.FromSeconds(0.41);
    Check(p.Tick().SequenceEqual([down]), "duplicate pad repeats once");
    p.ButtonUp(1, PadButton.DpadDown); p.ButtonUp(2, PadButton.DpadDown);
    clock.Now += TimeSpan.FromSeconds(0.2);
    Check(p.ButtonDown(2, PadButton.DpadDown).SequenceEqual([down]), "second pad alone still navigates");
    p.ButtonUp(2, PadButton.DpadDown);
    clock.Now += TimeSpan.FromSeconds(0.1);
    p.ButtonDown(1, PadButton.DpadDown); p.ButtonUp(1, PadButton.DpadDown);
    Check(p.ButtonDown(1, PadButton.DpadDown).SequenceEqual([down]), "same pad pressing twice quickly is not deduplicated");

    // Back held in a game, then the overlay opens: no instant Settings.
    p = new PadInterpreter(clock);
    p.ButtonDown(1, PadButton.Back);
    clock.Now += TimeSpan.FromSeconds(0.3);
    p.Tick();
    p.OverlayOpen = true;
    clock.Now += TimeSpan.FromSeconds(0.4);
    Check(!p.Tick().Contains(new PadAction(PadActionKind.Command, OverlayCommand.Settings)),
        "Back held before the overlay opened does not open Settings");
}

// --- Pad in the overlay -----------------------------------------------------
{
    var store = new ConfigStore(Path.Combine(temp, "padui", "config.json"));
    var monitors = new MonitorProfiles(store, new MonitorSwitcherLocator(store), Path.Combine(temp, "padui", "none"));
    var content = new OverlayContent(store, monitors, new MonitorSwitcherLocator(store), new FakeHost());
    var nav = new OverlayNavigator(content.Tabs, content.Settings, content.Info);
    nav.Reset([]);
    var pad = nav.Footer(pad: true);
    Check(pad.Contains(new FooterHint("A", Loc.T("Overlay.Footer.Confirm"))), "pad footer: A confirms");
    Check(pad.Contains(new FooterHint("B", Loc.T("Overlay.Footer.Close"))), "pad footer: B closes");
    Check(pad.Contains(new FooterHint("Back", Loc.T("Overlay.Footer.SettingsHold"))), "pad footer: hold Back for Settings");
    Check(pad.All(h => h.Keys is not ("Enter" or "Esc" or "F2")), "pad footer has no keyboard keys");
    var keys = nav.Footer();
    Check(keys.Any(h => h.Keys == "Enter") && keys.Any(h => h.Keys == "F2"), "keyboard footer unchanged");

    var combos = content.Settings().FirstOrDefault(i => i.Label() == Loc.T("Settings.PadCombos"));
    Check(combos is { Kind: ItemKind.Toggle }, "Settings has the Pad combos toggle");
    if (combos is not null)
    {
        var before = store.Current.ControllerEnabled;
        Check(combos.IsOn() == before, "Pad combos shows the setting");
        combos.Toggle!();
        Check(store.Current.ControllerEnabled == !before, "Pad combos flips controller_enabled");
    }

    content.ResetTransient();
    var shortcuts = content.Info().Where(i => i.Kind == ItemKind.Info && i.Visible()).Select(i => i.Value()).ToList();
    foreach (var expected in new[] { "Back + RB", "Back + ↑ / ↓", "LT / RT", "A / B" })
        Check(shortcuts.Contains(expected), $"Info shortcuts list {expected}");
    Check(content.Info().Any(i => i.Visible() && i.Label() == Loc.T("Overlay.Info.Pad.SettingsHold")), "Info lists hold Back for Settings");
}

// --- Audio rules ---
{
    AudioDeviceRef D(string id, string name = "") => new(id, name == "" ? id : name);
    var ranked = new List<AudioDeviceRef> { D("A"), D("B"), D("C") };
    IReadOnlySet<string> Set(params string[] ids) => new HashSet<string>(ids);
    Check(AudioRules.Target(ranked, Set("B", "C"), "C") == "B", "Audio Target picks best available when default is lower");
    Check(AudioRules.Target(ranked, Set("B", "C"), "B") is null, "Audio Target null when default already best");
    Check(AudioRules.Target(ranked, Set(), "C") is null, "Audio Target null when nothing available");
    Check(AudioRules.Target([], Set("B"), "B") is null, "Audio Target null for empty ranking");
    Check(AudioRules.Target(ranked, Set("D"), "D") is null, "Audio Target null when only unranked available");

    var merged = AudioRules.Merge([D("A"), D("B")], [D("B", "B2"), D("C")]);
    Check(merged.Select(d => d.Id).SequenceEqual(["A", "B", "C"]) && merged[1].Name == "B2", "Audio Merge appends and renames");
    var seeded = AudioRules.Seed([D("X"), D("Y"), D("Z")], "Y");
    Check(seeded.Select(d => d.Id).SequenceEqual(["Y", "X", "Z"]), "Audio Seed puts default first");

    Check(Math.Abs(AudioRules.StepVolume(0.52, +1, 1) - 0.55) < 1e-9, "Audio step up snaps to 5%");
    Check(Math.Abs(AudioRules.StepVolume(0.52, -1, 1) - 0.50) < 1e-9, "Audio step down snaps to 5%");
    Check(AudioRules.StepVolume(0.98, +1, 1) == 1.0, "Audio step clamps to 1");
    Check(AudioRules.StepVolume(0.03, -1, 1) == 0.0, "Audio step clamps to 0");
    Check(Math.Abs(AudioRules.StepVolume(0.70, +1, 0.6) - 0.6) < 1e-9, "Audio step up clamps to cap");
    Check(Math.Abs(AudioRules.StepVolume(0.70, -1, 0.6) - 0.6) < 1e-9, "Audio step down then clamps to cap");
    bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;
    Check(Near(AudioRules.StepVolume(0.55, +1, 1), 0.60) && Near(AudioRules.StepVolume(0.55, -1, 1), 0.50), "Audio step from exact 0.55 both ways");
    Check(Near(AudioRules.StepVolume(0.5499999999, +1, 1), 0.60) && Near(AudioRules.StepVolume(0.5499999999, -1, 1), 0.50), "Audio step from 0.5499999999 both ways");
    Check(Near(AudioRules.StepVolume((double)0.55f, +1, 1), 0.60) && Near(AudioRules.StepVolume((double)0.55f, -1, 1), 0.50), "Audio step from float32 0.55 both ways");
    Check(Near(AudioRules.StepVolume((double)0.7f, +1, 1), 0.75) && Near(AudioRules.StepVolume((double)0.7f, -1, 1), 0.65), "Audio step from float32 0.7 both ways");
    var caps = new Dictionary<string, int> { ["A"] = 60 };
    Check(Math.Abs(AudioRules.CapFor(caps, "A") - 0.6) < 1e-9 && AudioRules.CapFor(caps, "B") == 1.0, "Audio CapFor");

    var ac = new AppConfig();
    Check(ac.AudioAutoOutput && ac.AudioAutoInput && ac.AudioOutputOrder.Count == 0 && ac.AudioInputOrder.Count == 0 && ac.AudioVolumeCaps.Count == 0, "Audio config defaults");
    var acJson = JsonSerializer.Serialize(ac, ConfigStore.JsonOptions);
    foreach (var key in new[] { "audio_output_order", "audio_input_order", "audio_auto_output", "audio_auto_input", "audio_volume_caps" })
        Check(acJson.Contains($"\"{key}\""), $"Audio config JSON has {key}");
    var audioPath = Path.Combine(temp, "audio-config.json");
    var audioStore = new ConfigStore(audioPath);
    audioStore.Update(c => { c.AudioOutputOrder = [D("o1", "Speakers")]; c.AudioVolumeCaps["o1"] = 60; c.AudioAutoInput = false; });
    var reloaded = new ConfigStore(audioPath).Current;
    Check(reloaded.AudioOutputOrder.Count == 1 && reloaded.AudioOutputOrder[0] == D("o1", "Speakers")
        && reloaded.AudioVolumeCaps["o1"] == 60 && !reloaded.AudioAutoInput, "Audio config round-trips through ConfigStore");
}

// --- Navigator: slider, media, sub-pages, reorder; pad media and volume --------
{
    var log = new List<string>();
    var media = new FakeMedia(log);
    var visible = true;
    var order = new List<string> { "a", "b", "c" };
    List<OverlayItem> Rows() =>
        order.Select(k => new OverlayItem
        {
            Kind = ItemKind.Reorder, Key = k, Label = () => k,
            Move = d =>
            {
                var i = order.IndexOf(k);
                var j = i + d;
                if (j < 0 || j >= order.Count) return;
                order.RemoveAt(i); order.Insert(j, k); log.Add($"move {k} {d}");
            },
            Commit = () => log.Add("commit"),
            Cancel = () => log.Add("cancel"),
            RemoveEntry = () => log.Add($"remove {k}"),
        }).ToList();
    var slider = new OverlayItem { Kind = ItemKind.Slider, Label = () => "Vol", Step = d => log.Add("vol" + d), Toggle = () => log.Add("mute") };
    var mic = new OverlayItem { Kind = ItemKind.Choice, Label = () => "Input", Step = d => log.Add("in" + d), Toggle = () => log.Add("micmute") };
    var mediaRow = new OverlayItem { Kind = ItemKind.Media, Label = () => "Media", Visible = () => visible };
    var tail = new OverlayItem { Kind = ItemKind.Action, Label = () => "Tail", Action = () => log.Add("tail") };
    var sub = new OverlayItem { Kind = ItemKind.Action, Label = () => "SubRow", Key = "sub", SubPage = () => Rows() };
    OverlayItem[] settings = [new() { Kind = ItemKind.Toggle, Label = () => "T", Toggle = () => { } }, sub];
    var nav = new OverlayNavigator(
        [new OverlayTab("Controlli", () => "C", () => new[] { slider, mic, mediaRow, tail }),
         new OverlayTab("Audio", () => "Au", () => Rows())],
        () => settings, () => [], media: media);
    void Run(NavResult r) => r.Action?.Invoke();

    nav.Reset([]);
    Check(nav.Selected == slider, "slider selected first");
    Run(nav.Handle(OverlayCommand.Right)); Run(nav.Handle(OverlayCommand.RightRepeat));
    Run(nav.Handle(OverlayCommand.Left)); Run(nav.Handle(OverlayCommand.LeftRepeat));
    Check(log.SequenceEqual(["vol1", "vol1", "vol-1", "vol-1"]), "slider steps with Left/Right and repeats");
    log.Clear();
    var ra = nav.Handle(OverlayCommand.A);
    Check(ra.Effect == NavEffect.RunHere, "slider A stays open"); Run(ra);
    Check(log.SequenceEqual(["mute"]), "slider A mutes");
    log.Clear();
    var f = nav.Footer();
    Check(f.Contains(new FooterHint("← →", Loc.T("Overlay.Footer.Volume"))) && f[0] == new FooterHint("Enter", Loc.T("Overlay.Footer.Mute")),
        "slider footer: arrows Volume, A Mute");

    nav.Handle(OverlayCommand.Down);
    Run(nav.Handle(OverlayCommand.A));
    Check(log.SequenceEqual(["micmute"]), "Choice with Toggle: A toggles");
    log.Clear();
    Run(nav.Handle(OverlayCommand.Right));
    Check(log.SequenceEqual(["in1"]), "Choice with Toggle: Right still steps");
    log.Clear();

    nav.Handle(OverlayCommand.Down);
    Check(nav.Selected == mediaRow, "media row selectable");
    Run(nav.Handle(OverlayCommand.Left)); Run(nav.Handle(OverlayCommand.Right)); Run(nav.Handle(OverlayCommand.A));
    Check(log.SequenceEqual(["prev", "next", "playpause"]), "media row: Left/Right/A");
    log.Clear();
    foreach (var c in new[] { OverlayCommand.PrevTrack, OverlayCommand.NextTrack, OverlayCommand.PlayPause }) Run(nav.Handle(c));
    Check(log.SequenceEqual(["prev", "next", "playpause"]), "global media commands on Tabs");
    log.Clear();
    nav.Handle(OverlayCommand.Settings);
    Run(nav.Handle(OverlayCommand.NextTrack)); Run(nav.Handle(OverlayCommand.PlayPause));
    Check(nav.Page == OverlayPage.Settings && log.SequenceEqual(["next", "playpause"]), "global media commands on Settings");
    log.Clear();

    // Sub-page
    nav.Handle(OverlayCommand.Down);
    Check(nav.Selected == sub, "SubRow selected in Settings");
    Check(nav.Handle(OverlayCommand.A).Effect == NavEffect.Redraw && nav.Page == OverlayPage.Sub, "A opens the sub-page");
    Check(nav.SubTitle == "SubRow" && nav.Items.Count == 3, "sub-page title and items");
    nav.Handle(OverlayCommand.B);
    Check(nav.Page == OverlayPage.Settings && nav.Selected == sub, "B returns to Settings at the same row");

    // Reorder
    nav.Handle(OverlayCommand.A);
    Check(nav.Index == 0 && !nav.Holding, "sub-page starts on first entry");
    nav.Handle(OverlayCommand.A);
    Check(nav.Holding, "A picks up");
    Check(nav.Footer().Any(h => h.Label == Loc.T("Overlay.Footer.Drop")), "footer shows Drop while holding");
    Check(nav.Handle(OverlayCommand.Up).Effect == NavEffect.None, "cannot move above first entry");
    Run(nav.Handle(OverlayCommand.Down));
    Check(nav.Index == 1 && order.SequenceEqual(["b", "a", "c"]) && nav.Selected!.Key == "a", "Down moves entry, selection follows");
    nav.Refresh();
    Check(nav.Index == 1 && nav.Selected!.Key == "a", "Refresh keeps the held entry");
    Run(nav.Handle(OverlayCommand.Down));
    Check(order.SequenceEqual(["b", "c", "a"]) && nav.Index == 2, "moves to the end");
    Check(nav.Handle(OverlayCommand.Down).Effect == NavEffect.None && nav.Index == 2, "Down cannot leave the list while holding");
    Check(nav.Handle(OverlayCommand.Left).Effect == NavEffect.None && nav.Handle(OverlayCommand.Remove).Effect == NavEffect.None, "other commands ignored while holding");
    log.Clear();
    var commit = nav.Handle(OverlayCommand.A);
    Check(commit.Effect == NavEffect.RunHere && !nav.Holding, "A drops"); Run(commit);
    Check(log.SequenceEqual(["commit"]), "A calls Commit");
    log.Clear();

    // cancel paths: B, Settings, tab change, Reset
    nav.Handle(OverlayCommand.Up);
    Check(nav.Index == 1, "Up navigates when not holding");
    nav.Handle(OverlayCommand.A);
    Run(nav.Handle(OverlayCommand.Down));
    var rb = nav.Handle(OverlayCommand.B);
    Check(rb.Effect == NavEffect.RunHere && !nav.Holding && nav.Page == OverlayPage.Sub, "B while holding cancels, stays on the page");
    Run(rb);
    Check(log.SequenceEqual(["move c 1", "cancel"]), "B observed Move then Cancel");
    log.Clear();
    nav.Handle(OverlayCommand.A);
    var rs = nav.Handle(OverlayCommand.Settings);
    Check(rs.Effect == NavEffect.RunHere && !nav.Holding && nav.Page == OverlayPage.Tabs, "Settings while holding cancels and changes page");
    Run(rs);
    Check(log.SequenceEqual(["cancel"]), "page change called Cancel");
    log.Clear();
    nav.Handle(OverlayCommand.NextTab);
    Check(nav.CurrentTab.Id == "Audio", "on Audio tab");
    nav.Handle(OverlayCommand.A);
    Check(nav.Holding, "holding on the tab");
    var rt = nav.Handle(OverlayCommand.PrevTab);
    Check(rt.Effect == NavEffect.RunHere && !nav.Holding && nav.CurrentTab.Id == "Controlli", "tab change while holding cancels");
    Run(rt);
    Check(log.SequenceEqual(["cancel"]), "tab change called Cancel");
    log.Clear();
    nav.Handle(OverlayCommand.NextTab);
    nav.Handle(OverlayCommand.A);
    Check(nav.Holding, "holding again on tab");
    var resetCancel = nav.Reset([]);
    Check(!nav.Holding && resetCancel != null, "Reset clears Holding and returns Cancel");
    resetCancel?.Invoke();
    Check(log.SequenceEqual(["cancel"]), "Reset cancel observed");
    Check(nav.Reset([]) == null, "Reset without holding returns null");
    log.Clear();

    // Remove
    order.Clear(); order.AddRange(["a", "b", "c"]);
    nav.Handle(OverlayCommand.NextTab);
    nav.Handle(OverlayCommand.Down);
    Run(nav.Handle(OverlayCommand.Remove));
    Check(log.SequenceEqual(["remove b"]), "Remove calls RemoveEntry");
    log.Clear();
    nav.Reset([]);
    Check(nav.Handle(OverlayCommand.Remove).Effect == NavEffect.None, "Remove ignored without RemoveEntry");

    // Refresh by key
    nav.Handle(OverlayCommand.NextTab);
    nav.Handle(OverlayCommand.Down); nav.Handle(OverlayCommand.Down);
    Check(nav.Selected!.Key == "c", "selected last entry");
    order.Remove("a");
    nav.Refresh();
    Check(nav.Selected!.Key == "c" && nav.Index == 1, "Refresh keeps selection by Key when an earlier item disappears");

    // Selected media row hides
    nav.Reset([]);
    nav.Handle(OverlayCommand.Down); nav.Handle(OverlayCommand.Down);
    Check(nav.Selected == mediaRow, "media row selected");
    visible = false;
    nav.Refresh();
    Check(nav.Selected == mic, "hidden media row moves selection to a neighbour");
    visible = true;

    var bare = new OverlayNavigator([new OverlayTab("Controlli", () => "C", () => new[] { tail })], () => [], () => []);
    bare.Reset([]);
    Check(bare.Handle(OverlayCommand.PlayPause).Effect == NavEffect.None, "no media control: media commands do nothing");
}

{
    var clock = new TestClock { Now = DateTimeOffset.UnixEpoch };
    PadInterpreter New(bool open = false) => new(clock) { OverlayOpen = open };
    PadAction Cmd(OverlayCommand c) => new(PadActionKind.Command, c);
    bool Is(IReadOnlyList<PadAction> got, params PadAction[] want) => got.SequenceEqual(want);
    var volDown = new PadAction(PadActionKind.VolumeDown);
    var volUp = new PadAction(PadActionKind.VolumeUp);
    var micMute = new PadAction(PadActionKind.MicMute);

    var p = New(open: true);
    Check(Is(p.ButtonDown(1, PadButton.LB), Cmd(OverlayCommand.PrevTrack)), "open: LB -> PrevTrack");
    Check(Is(p.ButtonDown(1, PadButton.RB), Cmd(OverlayCommand.NextTrack)), "open: RB -> NextTrack");
    Check(Is(p.ButtonDown(1, PadButton.X), Cmd(OverlayCommand.PlayPause)), "open: X -> PlayPause");
    Check(Is(p.ButtonDown(1, PadButton.Y), Cmd(OverlayCommand.Remove)), "open: Y -> Remove");
    p = New(open: true);
    p.ButtonDown(1, PadButton.Back);
    Check(Is(p.ButtonDown(1, PadButton.RB)), "open: Back+RB chord does not fire NextTrack");
    Check(Is(p.ButtonUp(1, PadButton.RB), new PadAction(PadActionKind.ToggleOverlay)), "open: chord still toggles");

    p = New();
    Check(Is(p.ButtonDown(1, PadButton.LB)) && Is(p.ButtonDown(1, PadButton.X)) && Is(p.ButtonDown(1, PadButton.Y)), "closed: LB/X/Y alone do nothing");
    p = New(); p.ButtonDown(1, PadButton.Back);
    Check(Is(p.ButtonDown(1, PadButton.DpadLeft), volDown), "closed: Back+Left -> VolumeDown");
    clock.Now += TimeSpan.FromSeconds(0.39);
    Check(Is(p.Tick()), "volume: no repeat before 0.4 s");
    clock.Now += TimeSpan.FromSeconds(0.02);
    Check(Is(p.Tick(), volDown), "volume: repeats after 0.4 s");
    clock.Now += TimeSpan.FromSeconds(0.12);
    Check(Is(p.Tick(), volDown), "volume: then every 0.12 s");
    p.ButtonUp(1, PadButton.Back);
    clock.Now += TimeSpan.FromSeconds(1);
    Check(Is(p.Tick()), "volume: repeat stops on Back release");
    p = New(); p.ButtonDown(1, PadButton.Back);
    Check(Is(p.ButtonDown(1, PadButton.DpadRight), volUp), "closed: Back+Right -> VolumeUp");
    p.ButtonUp(1, PadButton.DpadRight);
    clock.Now += TimeSpan.FromSeconds(1);
    Check(Is(p.Tick()), "volume: repeat stops on D-pad release");
    p.ButtonUp(1, PadButton.Back);
    p = New(); p.ButtonDown(1, PadButton.Back);
    Check(Is(p.ButtonDown(1, PadButton.Y), micMute), "closed: Back+Y -> MicMute");
    Check(Is(p.ButtonDown(1, PadButton.DpadUp), new PadAction(PadActionKind.MonitorTv)), "monitor combo still works");
    p = New(); p.CombosEnabled = false; p.ButtonDown(1, PadButton.Back);
    Check(Is(p.ButtonDown(1, PadButton.DpadLeft)) && Is(p.ButtonDown(1, PadButton.Y)), "combos off: no volume or mic");
    p = New(); p.ButtonDown(1, PadButton.Back); p.ButtonDown(1, PadButton.DpadLeft);
    p.OverlayOpen = true;
    clock.Now += TimeSpan.FromSeconds(1);
    Check(!p.Tick().Contains(volDown), "opening the overlay stops volume repeat");

    p = New();
    p.ButtonDown(1, PadButton.Back); p.ButtonDown(2, PadButton.Back);
    var dup = new List<PadAction>();
    dup.AddRange(p.ButtonDown(1, PadButton.DpadLeft)); dup.AddRange(p.ButtonDown(2, PadButton.DpadLeft));
    Check(dup.SequenceEqual([volDown]), "duplicate pad: one VolumeDown");
    clock.Now += TimeSpan.FromSeconds(0.41);
    Check(p.Tick().SequenceEqual([volDown]), "duplicate pad: volume repeats once");
}

try { Directory.Delete(temp, recursive: true); } catch { /* best effort */ }
Console.WriteLine(failures == 0 ? $"All {passed} checks passed." : $"{failures} failed, {passed} passed.");
return failures == 0 ? 0 : 1;

sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now;
    public override DateTimeOffset GetUtcNow() => Now;
}

sealed class FakeHost : IOverlayHost
{
    public List<string> Calls { get; } = [];
    public bool InFullMode { get; set; }
    public bool PreviousWindowIsForeground { get; set; } = true;
    public void SwitchMonitor(string profile) => Calls.Add("SwitchMonitor:" + profile);
    public void ToggleStartup() => Calls.Add("ToggleStartup");
    public void ChooseMonitorSwitcher() => Calls.Add("ChooseMonitorSwitcher");
    public void SwitchFullMode() => Calls.Add("SwitchFullMode");
    public void Exit() => Calls.Add("Exit");
}

sealed class FakeMedia(List<string> log) : IMediaControl
{
    public NowPlaying? Current => null;
    public event Action? Changed { add { } remove { } }
    public void Previous() => log.Add("prev");
    public void Next() => log.Add("next");
    public void PlayPause() => log.Add("playpause");
}

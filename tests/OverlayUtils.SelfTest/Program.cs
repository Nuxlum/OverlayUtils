using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using OverlayUtils.Core;
using OverlayUtils.Core.Audio;
using OverlayUtils.Core.Config;
using OverlayUtils.Core.Games;
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
          "discord_client_secret": "not-a-real-secret",
          "discord_favorites": ["1", "2"],
          "future_key": ["1", "2"]
        }
        """);
    var store = new ConfigStore(path);
    var cfg = store.Current;
    Check(cfg.HomeKeyMode == "alttab" && !cfg.ControllerEnabled && cfg.MonitorDesk == "Main", "Python config keys load");
    Check(cfg.Language == "auto" && cfg.NotificationsEnabled, "missing keys get defaults");
    Check(cfg.HiddenTabs.SequenceEqual(["Giochi"]), "lists load");

    var nullPath = Path.Combine(Path.GetDirectoryName(path)!, "nulls.json");
    File.WriteAllText(nullPath, """{ "audio_output_order": null, "audio_input_order": null, "audio_volume_caps": null, "hidden_tabs": null }""");
    var nullCfg = new ConfigStore(nullPath).Current;
    Check(nullCfg.AudioOutputOrder.Count == 0 && nullCfg.AudioInputOrder.Count == 0 && nullCfg.AudioVolumeCaps.Count == 0 && nullCfg.HiddenTabs.Count == 0, "null lists load as empty");

    var changed = 0;
    store.Changed += _ => changed++;
    store.Update(c => c.Language = "it");
    Check(changed == 1, "Changed raised once");
    var json = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
    Check(json.GetProperty("language").GetString() == "it", "update saved");
    Check(json.GetProperty("future_key").GetArrayLength() == 2, "unknown keys survive a save");
    Check(!json.TryGetProperty("spotify_client_id", out _) && !json.TryGetProperty("discord_client_secret", out _)
        && !json.TryGetProperty("discord_favorites", out _), "Spotify / Discord settings (dropped, incl. the client secret) are removed on save");

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
    File.WriteAllText(Path.Combine(legacy, "discord_token.json"), "{}");
    File.WriteAllText(Path.Combine(legacy, "discord_recent.json"), "[]");
    Directory.CreateDirectory(Path.Combine(legacy, "discord_cache"));
    Directory.CreateDirectory(Path.Combine(legacy, "games_cache"));
    File.WriteAllText(Path.Combine(legacy, "games_cache", "a.jpg"), "x");

    Check(ConfigMigration.MigrateIfNeeded(legacy, target), "migrates the first time");
    Check(File.Exists(Path.Combine(target, "config.json")), "config copied");
    Check(File.Exists(Path.Combine(target, "games_cache", "a.jpg")), "other caches copied");
    Check(!File.Exists(Path.Combine(target, "spotify_token.json")) && !Directory.Exists(Path.Combine(target, "spotify_cache"))
        && !File.Exists(Path.Combine(target, "discord_token.json")) && !File.Exists(Path.Combine(target, "discord_recent.json"))
        && !Directory.Exists(Path.Combine(target, "discord_cache")),
        "Spotify and Discord tokens and caches not copied (both dropped)");
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
    var content = new OverlayContent(store, monitors, new MonitorSwitcherLocator(store), host, new FakeAudio(), null, () => altF4++);
    OverlayNavigator NewNav()
    {
        var n = new OverlayNavigator(content.Tabs, content.Settings, content.Info);
        content.ResetTransient();
        n.Reset(["Home"]);
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
    Check(content.Tabs.Count == 4 && content.Tabs[1].Id == OverlayContent.ControlsTabId && nav.CurrentTab.Id == OverlayContent.ControlsTabId, "Controls is the first visible tab (Home hidden)");

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
    var tabRow = content.Settings()[1];
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
    Check(content.Tabs[1].Title() == "Controlli" && content.Controls().Any(i => i.Label() == "Alimentazione"), "Italian tab and Power labels");
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
    Check(PadInterpreter.BatteryPercent(1, 84) == 84 && PadInterpreter.BatteryPercent(3, 40) == 40
        && PadInterpreter.BatteryPercent(4, -1) == 100, "battery percent known cases");
    Check(PadInterpreter.BatteryPercent(2, -1) == null && PadInterpreter.BatteryPercent(1, -1) == null
        && PadInterpreter.BatteryPercent(0, 50) == null, "battery percent unknown cases");
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
    var content = new OverlayContent(store, monitors, new MonitorSwitcherLocator(store), new FakeHost(), new FakeAudio());
    var nav = new OverlayNavigator(content.Tabs, content.Settings, content.Info);
    nav.Reset(["Home"]);
    var pad = nav.Footer(pad: true);
    Check(pad.Contains(new FooterHint("A", Loc.T("Overlay.Footer.Confirm"), OverlayCommand.A)), "pad footer: A confirms");
    Check(pad.Contains(new FooterHint("B", Loc.T("Overlay.Footer.Close"), OverlayCommand.B)), "pad footer: B closes");
    Check(pad.Contains(new FooterHint("Back", Loc.T("Overlay.Footer.SettingsHold"), OverlayCommand.Settings)), "pad footer: hold Back for Settings");
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
    Check(f.Contains(new FooterHint("← →", Loc.T("Overlay.Footer.Volume"))) && f[0] == new FooterHint("Enter", Loc.T("Overlay.Footer.Mute"), OverlayCommand.A),
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

    // Held entry vanishes (device unplugged mid-reorder)
    nav.Reset([]); log.Clear();
    order.Clear(); order.AddRange(["a", "b", "c"]);
    nav.Handle(OverlayCommand.NextTab);
    nav.Handle(OverlayCommand.Down);
    nav.Handle(OverlayCommand.A);
    Check(nav.Holding && nav.Selected!.Key == "b", "holding b");
    order.Remove("b");
    var vanishCancel = nav.Refresh();
    Check(!nav.Holding, "Refresh drops the hold when the held entry vanished");
    vanishCancel?.Invoke(); log.Clear();
    Run(nav.Handle(OverlayCommand.Down)); Run(nav.Handle(OverlayCommand.A));
    Check(!log.Any(l => l.StartsWith("move") || l == "commit"), "neighbour's Move/Commit not called after the hold was dropped");

    // After the vanish, a new pick-up must survive Refresh (no stale key)
    nav.Reset([]); log.Clear();
    order.Clear(); order.AddRange(["a", "b", "c", "d"]);
    nav.Handle(OverlayCommand.NextTab);
    nav.Handle(OverlayCommand.Down);
    nav.Handle(OverlayCommand.A);
    order.Remove("b");
    nav.Refresh();
    Check(nav.Selected!.Key == "c", "fallback selection is c");
    nav.Handle(OverlayCommand.A);
    Check(nav.Holding, "picked up c");
    nav.Refresh();
    Check(nav.Holding && nav.Selected!.Key == "c", "Refresh keeps a pick-up made after the vanish");
    log.Clear();
    Run(nav.Handle(OverlayCommand.Down));
    Check(log.SequenceEqual(["move c 1"]) && nav.Selected!.Key == "c", "Down moves c");
    nav.Handle(OverlayCommand.B);

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
    p = New(); p.ButtonDown(1, PadButton.Back); p.ButtonDown(1, PadButton.LB);
    Check(Is(p.ButtonDown(1, PadButton.Y)), "closed: Back+LB+Y -> no MicMute");
    p = New(); p.ButtonDown(1, PadButton.Back); p.ButtonDown(1, PadButton.LB);
    Check(Is(p.ButtonDown(1, PadButton.DpadLeft)), "closed: Back+LB+Left -> no VolumeDown");
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

// --- Audio tab and Settings → Audio ------------------------------------------
{
    Loc.SetLanguage("en");
    var store = new ConfigStore(Path.Combine(temp, "audiotab", "config.json"));
    store.Update(c => c.HiddenTabs.Add("Home")); // these checks are about Controls and Audio; Home has its own section
    var monitors = new MonitorProfiles(store, new MonitorSwitcherLocator(store), Path.Combine(temp, "audiotab", "none"));
    var audio = new FakeAudio();
    audio.Outputs.AddRange([new AudioDeviceRef("spk", "Speakers"), new AudioDeviceRef("hs", "Headset")]);
    audio.Inputs.Add(new AudioDeviceRef("mic", "Mic"));
    audio.DefaultOutput = "spk";
    audio.DefaultInput = "mic";
    audio.Volume = 0.45;
    var mediaLog = new List<string>();
    var media = new FakeMedia(mediaLog);
    var content = new OverlayContent(store, monitors, new MonitorSwitcherLocator(store), new FakeHost(), audio, media);
    void Run(NavResult r) => r.Action?.Invoke();
    OverlayNavigator Nav()
    {
        var n = new OverlayNavigator(content.Tabs, content.Settings, content.Info, media: content.Media);
        content.ResetTransient();
        n.Reset(store.Current.HiddenTabs);
        return n;
    }

    Check(content.Media == media, "content exposes the media control for the navigator");
    Check(content.Tabs.Select(t => t.Id).SequenceEqual([OverlayContent.HomeTabId, OverlayContent.ControlsTabId, OverlayContent.AudioTabId, OverlayContent.GamesTabId])
        && OverlayContent.AudioTabId == "Audio", "Audio tab comes after Controls");
    Check(content.Tabs[2].Title() == Loc.T("Overlay.Tab.Audio"), "Audio tab title");
    var rows = content.Tabs[2].Items();
    Check(rows.Select(r => r.Kind).SequenceEqual([ItemKind.Choice, ItemKind.Choice, ItemKind.Slider, ItemKind.Media]),
        "Audio rows: Output, Input, Volume, Now playing");
    Check(rows[0].Label() == Loc.T("Overlay.Audio.Output") && rows[1].Label() == Loc.T("Overlay.Audio.Input")
        && rows[2].Label() == Loc.T("Overlay.Audio.Volume"), "Audio row labels");
    Check(rows[0].Value() == "Speakers" && rows[1].Value() == "Mic", "Output and Input show the default devices");
    audio.MicMuted = true;
    rows = content.Tabs[2].Items();
    Check(rows[1].Value() == Loc.T("Overlay.Audio.InputMuted", "Mic") && rows[1].Icon == Glyphs.MicOff, "Input shows muted");
    audio.MicMuted = false;
    rows = content.Tabs[2].Items();
    Check(rows[1].Icon == Glyphs.Mic, "Input icon is the mic when not muted");
    Check(rows[2].Value() == "45%" && !rows[2].IsOn() && rows[2].Icon == Glyphs.VolumeLevels[1], "Volume shows percent and level icon");
    audio.Muted = true;
    rows = content.Tabs[2].Items();
    Check(rows[2].IsOn() && rows[2].Icon == Glyphs.Mute, "Volume muted: IsOn and mute icon");
    audio.Muted = false;
    audio.DefaultOutput = null;
    Check(content.Tabs[2].Items()[0].Value() == Loc.T("Overlay.Audio.NoDevice"), "Output without a default device");
    audio.DefaultOutput = "spk";

    var nav = Nav();
    Run(nav.Handle(OverlayCommand.NextTab));
    Check(nav.CurrentTab.Id == OverlayContent.AudioTabId && nav.Selected?.Label() == Loc.T("Overlay.Audio.Output"),
        "Audio tab opens on Output");
    Run(nav.Handle(OverlayCommand.Right));
    Run(nav.Handle(OverlayCommand.Left));
    Check(audio.Calls.SequenceEqual(["Cycle:Output:1", "Cycle:Output:-1"]), "Output Left/Right cycle the output");
    audio.Calls.Clear();
    nav.Handle(OverlayCommand.Down);
    Run(nav.Handle(OverlayCommand.Right));
    Run(nav.Handle(OverlayCommand.A));
    Check(audio.Calls.SequenceEqual(["Cycle:Input:1", "ToggleMicMute"]), "Input Right cycles, A toggles the mic mute");
    audio.Calls.Clear();
    nav.Handle(OverlayCommand.Down);
    Run(nav.Handle(OverlayCommand.Right));
    Run(nav.Handle(OverlayCommand.RightRepeat));
    Run(nav.Handle(OverlayCommand.Left));
    var muteRun = nav.Handle(OverlayCommand.A);
    Run(muteRun);
    Check(audio.Calls.SequenceEqual(["StepVolume:1", "StepVolume:1", "StepVolume:-1", "ToggleMute"])
        && muteRun.Effect == NavEffect.RunHere, "Volume Right/Left step, A mutes with the overlay open");
    audio.Calls.Clear();

    Check(!content.Tabs[2].Items()[3].Visible(), "Now playing hidden with no media");
    nav.Handle(OverlayCommand.Down);
    Check(nav.Selected?.Label() == Loc.T("Overlay.Audio.Output"), "selection skips the hidden Now playing row");
    media.Current = new NowPlaying("Song", "Artist", "Spotify", true);
    var np = content.Tabs[2].Items()[3];
    Check(np.Visible() && np.Label() == "Song" && np.Value() == "Artist · Spotify" && np.IsOn(), "Now playing shows title, artist · app");
    media.Current = new NowPlaying("Song", "", "Spotify", false);
    Check(np.Value() == "Spotify" && !np.IsOn(), "Now playing without artist, paused");
    nav.Handle(OverlayCommand.Up);
    Check(nav.Selected?.Kind == ItemKind.Media, "Now playing selectable with media");
    Run(nav.Handle(OverlayCommand.A));
    Check(mediaLog.SequenceEqual(["playpause"]), "Now playing A plays/pauses");
    media.Current = null;
    mediaLog.Clear();

    // Settings → Audio.
    OverlayItem SettingsRow(string key) => content.Settings().First(i => i.Label() == Loc.T(key));
    var outputOrder = SettingsRow("Settings.Audio.OutputOrder");
    var inputOrder = SettingsRow("Settings.Audio.InputOrder");
    Check(outputOrder.Kind == ItemKind.Action && outputOrder.SubPage != null && inputOrder.SubPage != null,
        "Output order and Input order open sub-pages");
    var settingsLabels = content.Settings().Select(i => i.Label()).ToList();
    var audioIndexes = new[] { "Settings.Audio.OutputOrder", "Settings.Audio.InputOrder", "Settings.Audio.AutoOutput",
        "Settings.Audio.AutoInput", "Settings.Audio.MaxVolume" }.Select(k => settingsLabels.IndexOf(Loc.T(k))).ToList();
    Check(audioIndexes[0] >= 0 && audioIndexes.Zip(audioIndexes.Skip(1)).All(p => p.Second == p.First + 1),
        "Settings → Audio rows in order");
    Check(outputOrder.Section?.Invoke() == Loc.T("Overlay.Section.Audio"), "Audio section header");

    store.Update(c =>
    {
        c.AudioOutputOrder = [new("spk", "Speakers"), new("hs", "Headset"), new("old", "Old TV")];
        c.AudioInputOrder = [new("mic", "Mic"), new("gone", "Old mic")];
    });
    var page = outputOrder.SubPage!();
    Check(page.All(i => i.Kind == ItemKind.Reorder) && page.Select(i => i.Label()).SequenceEqual(["Speakers", "Headset", "Old TV"]),
        "Output order lists the config entries");
    Check(page.Select(i => i.Key).SequenceEqual(["spk", "hs", "old"]), "order rows keyed by device id");
    Check(page[0].Value() == Loc.T("Settings.Audio.Connected") && page[2].Value() == Loc.T("Settings.Audio.Disconnected"),
        "connected / disconnected shown");
    Check(page[0].RemoveEntry == null && page[1].RemoveEntry == null && page[2].RemoveEntry != null,
        "Remove only for disconnected entries");
    Check(inputOrder.SubPage!().Select(i => i.Label()).SequenceEqual(["Mic", "Old mic"]), "Input order lists the input entries");

    nav = Nav();
    nav.Handle(OverlayCommand.Settings);
    while (nav.Selected?.Label() != Loc.T("Settings.Audio.OutputOrder")) nav.Handle(OverlayCommand.Down);
    Run(nav.Handle(OverlayCommand.A));
    Check(nav.Page == OverlayPage.Sub && nav.Selected?.Key == "spk", "A opens the Output order page");
    Run(nav.Handle(OverlayCommand.A));
    Check(nav.Holding, "A picks up the entry");
    Run(nav.Handle(OverlayCommand.Down));
    Check(nav.Items.Select(i => i.Key).SequenceEqual(["hs", "spk", "old"]) && nav.Selected?.Key == "spk",
        "moving down shows the new order");
    Check(store.Current.AudioOutputOrder.Select(d => d.Id).SequenceEqual(["spk", "hs", "old"]), "config unchanged while holding");
    Run(nav.Handle(OverlayCommand.A));
    Check(!nav.Holding && store.Current.AudioOutputOrder.Select(d => d.Id).SequenceEqual(["hs", "spk", "old"]),
        "dropping saves the new order");
    Run(nav.Handle(OverlayCommand.A));
    Run(nav.Handle(OverlayCommand.Down));
    Run(nav.Handle(OverlayCommand.B));
    Check(!nav.Holding && nav.Page == OverlayPage.Sub
        && store.Current.AudioOutputOrder.Select(d => d.Id).SequenceEqual(["hs", "spk", "old"])
        && nav.Items.Select(i => i.Key).SequenceEqual(["hs", "spk", "old"]), "B cancels: order and config unchanged");
    Run(nav.Handle(OverlayCommand.A));
    Run(nav.Handle(OverlayCommand.Up));
    var dropped = nav.Reset([]);
    Check(dropped != null, "Reset while holding returns the cancel");
    dropped?.Invoke();
    Check(store.Current.AudioOutputOrder.Select(d => d.Id).SequenceEqual(["hs", "spk", "old"])
        && outputOrder.SubPage!().Select(i => i.Key).SequenceEqual(["hs", "spk", "old"]), "cancel after Reset restores the order");

    nav = Nav();
    nav.Handle(OverlayCommand.Settings);
    while (nav.Selected?.Label() != Loc.T("Settings.Audio.OutputOrder")) nav.Handle(OverlayCommand.Down);
    Run(nav.Handle(OverlayCommand.A));
    Check(nav.Handle(OverlayCommand.Remove).Effect == NavEffect.None, "Remove does nothing on a connected entry");
    while (nav.Selected?.Key != "old") nav.Handle(OverlayCommand.Down);
    Run(nav.Handle(OverlayCommand.Remove));
    nav.Refresh();
    Check(store.Current.AudioOutputOrder.Select(d => d.Id).SequenceEqual(["hs", "spk"]), "Remove deletes the disconnected entry");
    Check(nav.Selected?.Key == "spk", "selection moves to a neighbour after Remove");

    var autoOut = SettingsRow("Settings.Audio.AutoOutput");
    var autoIn = SettingsRow("Settings.Audio.AutoInput");
    Check(autoOut.Kind == ItemKind.Toggle && autoOut.IsOn() && autoIn.IsOn(), "auto-switch toggles on by default");
    autoOut.Toggle!();
    Check(!store.Current.AudioAutoOutput && store.Current.AudioAutoInput, "Auto-switch output flips audio_auto_output");
    autoIn.Toggle!();
    Check(!store.Current.AudioAutoInput, "Auto-switch input flips audio_auto_input");

    var maxVolume = SettingsRow("Settings.Audio.MaxVolume");
    Check(maxVolume.Kind == ItemKind.Choice && maxVolume.Visible() && maxVolume.Value() == "100%", "Max volume starts at 100%");
    Check(maxVolume.Description().Contains("Speakers"), "Max volume names the current output");
    maxVolume.Step!(1);
    Check(store.Current.AudioVolumeCaps.GetValueOrDefault("spk", 100) == 100, "Max volume never above 100%");
    maxVolume.Step!(-1);
    Check(store.Current.AudioVolumeCaps["spk"] == 95 && maxVolume.Value() == "95%", "Max volume steps down 5%");
    for (var i = 0; i < 30; i++) maxVolume.Step!(-1);
    Check(store.Current.AudioVolumeCaps["spk"] == 20, "Max volume never below 20%");
    maxVolume.Step!(1);
    Check(store.Current.AudioVolumeCaps["spk"] == 25 && !store.Current.AudioVolumeCaps.ContainsKey("hs"),
        "Max volume is per device");
    store.Update(c => c.AudioVolumeCaps["spk"] = 52);
    maxVolume.Step!(1);
    Check(store.Current.AudioVolumeCaps["spk"] == 55, "Max volume snaps a hand-edited cap to 5%");
    audio.DefaultOutput = null;
    Check(!maxVolume.Visible(), "Max volume hidden without a default output");
    audio.DefaultOutput = "spk";

    // Hiding the Audio tab (with the Games tab hidden, so Controls and Audio are the only tabs).
    store.Update(c => c.HiddenTabs.Add(OverlayContent.GamesTabId));
    var audioTabRow = content.Settings().First(i => i.Kind == ItemKind.Toggle && i.Label() == Loc.T("Overlay.Tab.Audio"));
    Check(audioTabRow.IsOn(), "Audio tab shown by default");
    audioTabRow.Toggle!();
    Check(store.Current.HiddenTabs.Contains("Audio") && !audioTabRow.IsOn(), "Audio tab toggle hides it");
    nav = Nav();
    Check(nav.VisibleTabs.Select(t => t.Id).SequenceEqual([OverlayContent.ControlsTabId]), "hidden Audio tab is not shown");
    audioTabRow.Toggle!();
    Check(!store.Current.HiddenTabs.Contains("Audio") && Nav().VisibleTabs.Count == 2, "Audio tab shown again");

    // Same navigator, toggled from Settings while open: the tab strip follows at once (Python visible_tabs()).
    nav = Nav();
    Run(nav.Handle(OverlayCommand.NextTab));
    nav.Handle(OverlayCommand.Settings);
    while (nav.Selected?.Label() != Loc.T("Overlay.Tab.Audio")) nav.Handle(OverlayCommand.Down);
    Run(nav.Handle(OverlayCommand.A));
    Check(nav.SetHiddenTabs(store.Current.HiddenTabs) == null, "updating hidden tabs drops no hold");
    Check(nav.Page == OverlayPage.Settings && nav.Selected?.Label() == Loc.T("Overlay.Tab.Audio"),
        "updating hidden tabs keeps the page and the selection");
    Check(nav.VisibleTabs.Select(t => t.Id).SequenceEqual([OverlayContent.ControlsTabId]), "hidden at once on the open navigator");
    nav.Handle(OverlayCommand.B);
    Check(nav.Page == OverlayPage.Tabs && nav.CurrentTab.Id == OverlayContent.ControlsTabId && nav.Selected != null,
        "back on the tabs, the current tab falls back to Controls");
    audioTabRow.Toggle!();
    nav.SetHiddenTabs(store.Current.HiddenTabs);
    Check(nav.VisibleTabs.Count == 2 && nav.CurrentTab.Id == OverlayContent.ControlsTabId, "shown again at once, still on Controls");
    Run(nav.Handle(OverlayCommand.NextTab));
    Check(nav.CurrentTab.Id == OverlayContent.AudioTabId, "the Audio tab is reachable again");
    store.Update(c => c.HiddenTabs.Add("Audio"));
    nav.SetHiddenTabs(store.Current.HiddenTabs);
    Check(nav.CurrentTab.Id == OverlayContent.ControlsTabId && nav.Selected?.Label() == content.Controls().First(i => i.Visible()).Label(),
        "hiding the open tab moves to Controls and selects its first row");
    store.Update(c =>
    {
        c.HiddenTabs.Remove("Audio");
        c.HiddenTabs.Remove(OverlayContent.GamesTabId);
    });

    var shortcuts = content.Info().Where(i => i.Kind == ItemKind.Info && i.Visible()).Select(i => i.Value()).ToList();
    foreach (var expected in new[] { "Back + ← / →", "Back + Y", "LB / RB / X" })
        Check(shortcuts.Contains(expected), $"Info shortcuts list {expected}");
}

// --- Channels in the overlay -------------------------------------------------
{
    Loc.SetLanguage("en");
    var store = new ConfigStore(Path.Combine(temp, "channels", "config.json"));
    store.Update(c => c.HiddenTabs.Add("Home")); // these checks start from Controls; Home has its own section
    var monitors = new MonitorProfiles(store, new MonitorSwitcherLocator(store), Path.Combine(temp, "channels", "none"));
    var audio = new FakeAudio();
    audio.Outputs.AddRange([new AudioDeviceRef("spk", "Speakers"), new AudioDeviceRef("hs", "Headset")]);
    audio.DefaultOutput = "spk";
    var host = new FakeHost();
    var content = new OverlayContent(store, monitors, new MonitorSwitcherLocator(store), host, audio);
    void Run(NavResult r) => r.Action?.Invoke();
    OverlayNavigator Nav()
    {
        var n = new OverlayNavigator(content.Tabs, content.Settings, content.Info, media: content.Media);
        content.ResetTransient();
        n.Reset(store.Current.HiddenTabs);
        return n;
    }
    List<string?> Keys() => content.Audio().Where(i => i.Visible()).Select(i => i.Key).ToList();
    DeviceMix Stored(string device) => store.Current.AudioMix[device];
    const string game = @"C:\Steam\steamapps\common\Foo\foo.exe";
    const string discord = @"C:\Users\u\AppData\Local\Discord\Discord.exe";
    const string spotify = @"C:\Spotify\Spotify.exe";

    Check(!Keys().Any(k => k?.StartsWith("ch.") == true || k?.StartsWith("app.") == true), "no channel rows without apps");
    Check(!Keys().Contains("audio.presets") && !Keys().Contains("audio.savepreset"), "no preset rows without apps or presets");

    store.Update(c => c.AudioChannels = [.. MixRules.DefaultChannels]);
    audio.AppList.AddRange([new AudioApp(discord, "Discord"), new AudioApp(game, "Foo")]);
    Check(Keys().SequenceEqual(["audio.output", "audio.input", "audio.volume", "ch.game", "app." + game, "ch.chat", "app." + discord,
        "audio.savepreset"]), "channel rows only for channels with apps, in channel order, apps under them");
    var rows = content.Audio();
    var gameRow = rows.First(r => r.Key == "ch.game");
    var fooRow = rows.First(r => r.Key == "app." + game);
    Check(gameRow.Kind == ItemKind.Slider && !gameRow.Child && fooRow.Kind == ItemKind.Slider && fooRow.Child,
        "channel rows are sliders, app rows are child sliders");
    Check(gameRow.Section?.Invoke() == Loc.T("Overlay.Section.Channels") && rows.First(r => r.Key == "ch.chat").Section == null,
        "Channels section header on the first channel");
    Check(gameRow.Label() == "Game" && gameRow.Icon == Glyphs.ChannelIcon("game") && fooRow.Label() == "Foo", "channel and app labels and icon");
    Check(gameRow.Value() == "100%" && !gameRow.IsOn() && fooRow.Value() == "100%", "levels start at 100%");

    store.Update(c => c.AudioMix["spk"] = new DeviceMix(new() { ["game"] = new MixLevel(0.5, false) }, []));
    audio.Calls.Clear();
    var nav = Nav();
    Run(nav.Handle(OverlayCommand.NextTab));
    while (nav.Selected?.Key != "ch.game") nav.Handle(OverlayCommand.Down);
    Run(nav.Handle(OverlayCommand.Right));
    Check(Math.Abs(Stored("spk").Channels["game"].Volume - 0.55) < 1e-9 && audio.Calls.SequenceEqual(["ApplyMix"]),
        "Right on a channel raises its stored volume 5% and applies the mix");
    Check(!store.Current.AudioMix.ContainsKey("hs"), "only the current device's mix changes");
    Check(nav.Selected?.Value() == "55%", "channel row shows the new level");
    Run(nav.Handle(OverlayCommand.Left));
    Run(nav.Handle(OverlayCommand.LeftRepeat));
    Check(Math.Abs(Stored("spk").Channels["game"].Volume - 0.45) < 1e-9, "Left (and held Left) lower it");
    Run(nav.Handle(OverlayCommand.A));
    Check(Stored("spk").Channels["game"] == new MixLevel(0.45, true) && nav.Selected!.IsOn(), "A mutes the channel, volume kept");
    Run(nav.Handle(OverlayCommand.A));
    Check(!Stored("spk").Channels["game"].Muted, "A again unmutes");
    Run(nav.ClickLevel(nav.Index, 0.3));
    Check(Math.Abs(Stored("spk").Channels["game"].Volume - 0.3) < 1e-9, "clicking the bar sets the channel level");
    for (var i = 0; i < 30; i++) content.Audio().First(r => r.Key == "ch.game").Step!(1);
    Check(Stored("spk").Channels["game"].Volume == 1.0, "channel volume never above 100%");
    for (var i = 0; i < 30; i++) content.Audio().First(r => r.Key == "ch.game").Step!(-1);
    Check(Stored("spk").Channels["game"].Volume == 0.0, "channel volume never below 0%");

    // App rows, with a stored key in other casing (JSON dictionaries are case-sensitive).
    store.Update(c => c.AudioMix["spk"] = new DeviceMix(new(c.AudioMix["spk"].Channels), new() { [discord.ToUpperInvariant()] = new MixLevel(0.3, false) }));
    audio.Calls.Clear();
    while (nav.Selected?.Key != "app." + discord) nav.Handle(OverlayCommand.Down);
    Check(nav.Selected?.Value() == "30%", "app row reads its level case-insensitively");
    Run(nav.Handle(OverlayCommand.A));
    Check(Stored("spk").Apps.Count == 1 && MixRules.AppLevel(Stored("spk"), discord) == new MixLevel(0.3, true) && audio.Calls.SequenceEqual(["ApplyMix"]),
        "A on an app mutes it (one key, volume kept) and applies the mix");
    Run(nav.Handle(OverlayCommand.Right));
    Check(Stored("spk").Apps.Count == 1 && Math.Abs(MixRules.AppLevel(Stored("spk"), discord).Volume - 0.35) < 1e-9, "Right on an app raises it 5%");

    // A device seen for the first time gets the mix stored.
    audio.DefaultOutput = "hs";
    content.Audio().First(r => r.Key == "ch.chat").Step!(-1);
    Check(store.Current.AudioMix.ContainsKey("hs") && Math.Abs(Stored("hs").Channels["chat"].Volume - 0.95) < 1e-9
        && Stored("spk").Channels.GetValueOrDefault("chat", new MixLevel(1.0, false)).Volume == 1.0, "a new device gets its own mix");
    audio.DefaultOutput = null;
    content.Mix.StepChannel("chat", -1);
    Check(store.Current.AudioMix.Count == 2, "no default output: nothing stored");
    audio.DefaultOutput = "spk";

    // Selection follows the app by Key.
    while (nav.Selected?.Key != "app." + discord) nav.Handle(OverlayCommand.Down);
    audio.AppList.Insert(0, new AudioApp(@"C:\Games\other.exe", "Other game"));
    store.Update(c => c.AudioPrograms[@"C:\Games\other.exe"] = "chat");
    var before = nav.Index;
    nav.Refresh();
    Check(nav.Selected?.Key == "app." + discord && nav.Index == before + 1, "selection follows the app when another appears above it");
    audio.AppList.RemoveAt(0);
    store.Update(c => c.AudioPrograms.Remove(@"C:\Games\other.exe"));
    nav.Refresh();

    // Settings → Audio → Programs.
    OverlayItem SettingsRow(string key) => content.Settings().First(i => i.Label() == Loc.T(key));
    var settingsLabels = content.Settings().Select(i => i.Label()).ToList();
    Check(settingsLabels.IndexOf(Loc.T("Settings.Audio.Channels")) == settingsLabels.IndexOf(Loc.T("Settings.Audio.MaxVolume")) + 1
        && settingsLabels.IndexOf(Loc.T("Settings.Audio.Programs")) == settingsLabels.IndexOf(Loc.T("Settings.Audio.Channels")) + 1,
        "Channels and Programs after Max volume");
    store.Update(c =>
    {
        c.AudioPrograms[discord] = "chat";
        c.AudioPrograms[game] = "game";
        c.AudioPrograms[spotify] = "other";
    });
    var programs = SettingsRow("Settings.Audio.Programs");
    Check(programs.SubPage != null, "Programs opens a sub-page");
    var programRows = programs.SubPage!().Where(r => r.Kind == ItemKind.Choice).ToList();
    Check(programRows.Count == 3 && programRows.Any(r => r.Key == "prog." + discord && r.Value() == "Chat"),
        "one Choice row per known program, value = channel name");
    nav = Nav();
    nav.Handle(OverlayCommand.Settings);
    while (nav.Selected?.Label() != Loc.T("Settings.Audio.Programs")) nav.Handle(OverlayCommand.Down);
    Run(nav.Handle(OverlayCommand.A));
    Check(nav.Page == OverlayPage.Sub, "A opens the Programs page");
    while (nav.Selected?.Key != "prog." + discord) nav.Handle(OverlayCommand.Down);
    audio.Calls.Clear();
    Run(nav.Handle(OverlayCommand.Right));
    Check(store.Current.AudioPrograms[discord] == "media" && nav.Selected?.Value() == "Media" && audio.Calls.SequenceEqual(["ApplyMix"]),
        "Right moves the program to the next channel (Media) and applies the mix");
    var keys = Keys();
    Check(keys.IndexOf("app." + discord) == keys.IndexOf("ch.media") + 1 && !keys.Contains("ch.chat"),
        "the Audio tab shows it under Media");
    Run(nav.Handle(OverlayCommand.Right));
    Run(nav.Handle(OverlayCommand.Right));
    Check(store.Current.AudioPrograms[discord] == "game", "Right wraps through the channels");
    Run(nav.Handle(OverlayCommand.Left));
    Check(store.Current.AudioPrograms[discord] == "other", "Left goes back");
    host.NextProgram = @"C:\Apps\Zoom\Zoom.exe";
    while (nav.Selected?.Label() != Loc.T("Settings.Audio.AddProgram")) nav.Handle(OverlayCommand.Down);
    var add = nav.Handle(OverlayCommand.A);
    Check(add.Effect == NavEffect.RunHere, "Add program runs with the overlay open");
    Run(add);
    nav.Refresh();
    Check(host.Calls.Contains("ChooseProgram") && store.Current.AudioPrograms[@"C:\Apps\Zoom\Zoom.exe"] == "chat",
        "Add program stores the picked exe with its automatic channel");
    store.Update(c => c.AudioPrograms[@"C:\Apps\Zoom\Zoom.exe"] = "game");
    host.NextProgram = @"C:\APPS\ZOOM\ZOOM.EXE";
    Run(nav.Handle(OverlayCommand.A));
    var zoom = store.Current.AudioPrograms.Where(p => p.Key.Equals(@"C:\Apps\Zoom\Zoom.exe", StringComparison.OrdinalIgnoreCase)).ToList();
    Check(zoom.Count == 1 && zoom[0].Value == "game", "adding a known program in other casing keeps its entry and channel");
    store.Update(c => c.AudioPrograms.Remove(@"C:\Apps\Zoom\Zoom.exe"));

    // Settings → Audio → Channels.
    var channels = SettingsRow("Settings.Audio.Channels");
    var channelRows = channels.SubPage!().Where(r => r.Kind == ItemKind.Reorder).ToList();
    Check(channelRows.Select(r => r.Key).SequenceEqual(["game", "chat", "media", "other"]), "Channels page lists the channels in order");
    Check(channelRows[0].Value() == Loc.T("Icon.game") && channelRows.All(r => r.RemoveEntry == null), "value = icon name; defaults not deletable");
    nav = Nav();
    nav.Handle(OverlayCommand.Settings);
    while (nav.Selected?.Label() != Loc.T("Settings.Audio.Channels")) nav.Handle(OverlayCommand.Down);
    Run(nav.Handle(OverlayCommand.A));
    Check(nav.Page == OverlayPage.Sub && nav.Selected?.Key == "game", "A opens the Channels page");
    Run(nav.Handle(OverlayCommand.Right));
    Check(store.Current.AudioChannels[0].Icon == Glyphs.ChannelIconNames[1] && nav.Selected?.Icon == Glyphs.ChannelIcon(Glyphs.ChannelIconNames[1]),
        "Right cycles the channel icon");
    Run(nav.Handle(OverlayCommand.Left));
    Run(nav.Handle(OverlayCommand.Left));
    Check(store.Current.AudioChannels[0].Icon == Glyphs.ChannelIconNames[^1], "Left wraps the icons");
    Run(nav.Handle(OverlayCommand.Right));
    Check(nav.Footer().Any(f => f.Keys == "← →" && f.Label == Loc.T("Overlay.Footer.Icon")), "footer: arrows change the icon");
    Check(channels.SubPage!().First(r => r.Key == "channels.rename").ActionLabel?.Invoke() == Loc.T("Overlay.Footer.Rename"),
        "Rename names its A action");
    Run(nav.Handle(OverlayCommand.A));
    Run(nav.Handle(OverlayCommand.Down));
    Check(nav.Holding && store.Current.AudioChannels[0].Id == "game", "picked up; config unchanged while holding");
    Check(nav.Handle(OverlayCommand.Right).Effect == NavEffect.None, "Left/Right ignored while holding");
    Run(nav.Handle(OverlayCommand.A));
    Check(store.Current.AudioChannels.Select(c => c.Id).SequenceEqual(["chat", "game", "media", "other"]), "dropping saves the channel order");
    Check(Keys().IndexOf("ch.chat") < Keys().IndexOf("ch.game"), "Audio tab follows the channel order");
    Run(nav.Handle(OverlayCommand.A));
    Run(nav.Handle(OverlayCommand.Up));
    Run(nav.Handle(OverlayCommand.B));
    Check(store.Current.AudioChannels.Select(c => c.Id).SequenceEqual(["chat", "game", "media", "other"])
        && nav.Items.Where(r => r.Kind == ItemKind.Reorder).Select(r => r.Key).SequenceEqual(["chat", "game", "media", "other"]), "B cancels the move");

    host.NextName = null; // accept the suggestion
    while (nav.Selected?.Label() != Loc.T("Settings.Audio.AddChannel")) nav.Handle(OverlayCommand.Down);
    var addChannel = nav.Handle(OverlayCommand.A);
    Check(addChannel.Effect == NavEffect.RunHere, "Add channel runs with the overlay open");
    Run(addChannel);
    nav.Refresh();
    Check(host.Calls.Contains("AskName:" + Loc.T("Settings.Audio.ChannelName", 5)), "Add channel suggests Channel 5");
    Check(host.LastTitle == Loc.T("Dialog.Name.NewChannel"), "Add channel titles the prompt");
    Check(store.Current.AudioChannels.Last() is { Id: "c1", Name: "Channel 5" }, "Add channel appends c1 with the name");
    host.NextName = "   ";
    Run(nav.Handle(OverlayCommand.A));
    Check(store.Current.AudioChannels.Count == 5, "a blank name adds nothing");
    host.NextName = "Voice";
    while (nav.Selected?.Label() != Loc.T("Settings.Audio.Rename")) nav.Handle(OverlayCommand.Down);
    Check(nav.Selected?.Value() == "Chat", "Rename starts on the first channel");
    while (nav.Selected?.Value() != "Channel 5") Run(nav.Handle(OverlayCommand.Right));
    Run(nav.Handle(OverlayCommand.A));
    Check(host.Calls.Last() == "AskName:Channel 5" && store.Current.AudioChannels.Last().Name == "Voice", "Rename asks with the current name and saves it");
    Check(host.LastTitle == Loc.T("Dialog.Name.RenameChannel"), "Rename titles the prompt");
    Check(nav.Selected?.Value() == "Voice", "Rename keeps the renamed channel picked");

    store.Update(c => c.AudioPrograms[spotify] = "c1");
    Check(programs.SubPage!().First(r => r.Key == "prog." + spotify).Value() == "Voice", "Programs page shows the custom channel");
    while (nav.Selected?.Key != "c1") nav.Handle(OverlayCommand.Down);
    Check(nav.Selected?.RemoveEntry != null, "a custom channel can be deleted");
    audio.Calls.Clear();
    Check(nav.Selected?.ConfirmRemove == true, "deleting a custom channel asks for a second press");
    Run(nav.Handle(OverlayCommand.Remove));
    Check(store.Current.AudioChannels.Count == 5 && store.Current.AudioPrograms[spotify] == "c1" && nav.IsRemovePending(nav.Index),
        "the first Y only arms the delete");
    Run(nav.Handle(OverlayCommand.Remove));
    nav.Refresh();
    Check(store.Current.AudioChannels.Count == 4 && store.Current.AudioPrograms[spotify] == "other" && audio.Calls.Contains("ApplyMix"),
        "deleting a custom channel moves its programs to Other");
    while (nav.Selected?.Key != "game") nav.Handle(OverlayCommand.Down);
    Check(nav.Handle(OverlayCommand.Remove).Effect == NavEffect.None, "Y does nothing on a default channel");

    // Presets.
    store.Update(c => c.AudioMix["spk"] = new DeviceMix(new() { ["game"] = new MixLevel(0.4, false), ["chat"] = new MixLevel(0.7, true) }, []));
    host.NextName = null;
    var save = content.Audio().First(r => r.Key == "audio.savepreset");
    Check(save.Kind == ItemKind.Action && save.Stay, "Save preset runs with the overlay open");
    save.Action!();
    Check(host.Calls.Last() == "AskName:" + Loc.T("Overlay.Audio.PresetName", 1) && store.Current.AudioPresets.Single().Name == "Preset 1",
        "Save preset suggests Preset 1 and stores it");
    var presetsRow = content.Audio().First(r => r.Key == "audio.presets");
    Check(presetsRow.Kind == ItemKind.Choice && presetsRow.Visible() && presetsRow.Value() == "Preset 1", "Presets row shows the preset");
    store.Update(c => c.AudioMix["spk"] = new DeviceMix(new() { ["game"] = new MixLevel(0.9, false), ["chat"] = new MixLevel(0.2, false) }, []));
    host.NextName = "Night";
    content.Audio().First(r => r.Key == "audio.savepreset").Action!();
    host.NextName = "preset 1"; // same name, other casing: replaces
    store.Update(c => c.AudioMix["spk"] = new DeviceMix(new() { ["game"] = new MixLevel(0.4, false), ["chat"] = new MixLevel(0.7, true) }, []));
    content.Audio().First(r => r.Key == "audio.savepreset").Action!();
    Check(store.Current.AudioPresets.Select(p => p.Name).SequenceEqual(["preset 1", "Night"]), "same name replaces the preset");
    host.NextName = null;
    content.Audio().First(r => r.Key == "audio.savepreset").Action!();
    Check(host.Calls.Last() == "AskName:" + Loc.T("Overlay.Audio.PresetName", 3), "suggested name skips names in use");
    Check(host.LastTitle == Loc.T("Dialog.Name.SavePreset"), "Save preset titles the prompt");
    store.Update(c => c.AudioPresets.RemoveAt(2));
    store.Update(c => c.AudioMix["spk"] = new DeviceMix(new() { ["game"] = new MixLevel(1.0, false), ["chat"] = new MixLevel(1.0, false) }, []));
    nav = Nav();
    Run(nav.Handle(OverlayCommand.NextTab));
    while (nav.Selected?.Key != "audio.presets") nav.Handle(OverlayCommand.Down);
    Check(nav.Selected?.Value() == "preset 1", "Presets starts on the first preset");
    Check(nav.Footer()[0] == new FooterHint("Enter", Loc.T("Overlay.Footer.Apply"), OverlayCommand.A), "footer: A applies on Presets");
    Run(nav.Handle(OverlayCommand.Right));
    Check(nav.Selected?.Value() == "Night" && Stored("spk").Channels["game"].Volume == 1.0, "Right picks the next preset without applying");
    Run(nav.Handle(OverlayCommand.Right));
    audio.Calls.Clear();
    var apply = nav.Handle(OverlayCommand.A);
    Run(apply);
    Check(Stored("spk").Channels["game"] == new MixLevel(0.4, false) && Stored("spk").Channels["chat"] == new MixLevel(0.7, true)
        && audio.Calls.SequenceEqual(["ApplyMix"]), "applying the saved preset restores the channel volumes");
    Check(apply.Effect == NavEffect.RunHere, "applying keeps the overlay open");
    Run(nav.Handle(OverlayCommand.Right));
    content.ResetTransient();
    Check(content.Audio().First(r => r.Key == "audio.presets").Value() == "preset 1", "preset pick resets when the overlay opens");

    var strings = Loc.AllStrings(CultureInfo.InvariantCulture);
    foreach (var name in Glyphs.ChannelIconNames)
        Check(strings.ContainsKey("Icon." + name) && Glyphs.ChannelIcon(name).Length == 1, $"icon {name} has a name and a glyph");
    Check(Glyphs.ChannelIconNames.Count == 16 && MixRules.DefaultChannels.All(c => Glyphs.ChannelIconNames.Contains(c.Icon)),
        "16 channel icons incl. the defaults");
    Check(Glyphs.ChannelIcon("bogus") == Glyphs.ChannelIcon("other"), "unknown icon falls back to other");

    // Default channel names follow the language while they keep the stored English default.
    Loc.SetLanguage("it");
    try
    {
        store.Update(c => c.AudioPrograms[discord] = "other");
        var itKeys = content.Audio();
        Check(itKeys.First(r => r.Key == "ch.other").Label() == "Altro" && itKeys.First(r => r.Key == "ch.game").Label() == "Gioco",
            "Italian: default channel names on the Audio tab");
        var itChannels = SettingsRow("Settings.Audio.Channels").SubPage!();
        Check(itChannels.First(r => r.Key == "other").Label() == "Altro" && itChannels.First(r => r.Key == "media").Label() == "Media",
            "Italian: default channel names on the Channels page");
        Check(SettingsRow("Settings.Audio.Programs").SubPage!().First(r => r.Key == "prog." + discord).Value() == "Altro",
            "Italian: Programs shows the localized channel");
        var itRename = itChannels.First(r => r.Key == "channels.rename");
        for (var i = 0; i < 8 && itRename.Value() != "Altro"; i++) itRename.Step!(1);
        host.NextName = "Sottofondo";
        itRename.Toggle!();
        Check(host.Calls.Last() == "AskName:Altro", "Italian: Rename suggests the localized name");
        Check(SettingsRow("Settings.Audio.Channels").SubPage!().First(r => r.Key == "other").Label() == "Sottofondo"
            && content.Audio().First(r => r.Key == "ch.other").Label() == "Sottofondo", "a renamed default shows its stored name");
        content.Mix.AddChannel("Other");
        Check(SettingsRow("Settings.Audio.Channels").SubPage!().First(r => r.Key == "c1").Label() == "Other",
            "a custom channel named like a default keeps its stored name");
        content.Mix.DeleteChannel("c1");
        content.Mix.RenameChannel("other", "Other");
        // Accepting the localized suggestion unchanged keeps the default (not "Altro" frozen in config).
        var renameOther = SettingsRow("Settings.Audio.Channels").SubPage!().First(r => r.Key == "channels.rename");
        for (var i = 0; i < 8 && renameOther.Value() != "Altro"; i++) renameOther.Step!(1);
        host.NextName = null;
        renameOther.Toggle!();
        Check(host.Calls.Last() == "AskName:Altro" && store.Current.AudioChannels.First(c => c.Id == "other").Name == "Other",
            "Italian: accepting the suggested name stores the English default");
        host.NextName = "  Altro ";
        renameOther.Toggle!();
        Check(store.Current.AudioChannels.First(c => c.Id == "other").Name == "Other", "the localized name with spaces too");
    }
    finally
    {
        Loc.SetLanguage("en");
    }
    Check(content.Audio().First(r => r.Key == "ch.other").Label() == "Other", "English: default names as stored");
}

// --- Footer: an item names its A action --------------------------------------
{
    OverlayItem[] rows =
    [
        new() { Kind = ItemKind.Choice, Label = () => "Named", Step = _ => { }, Toggle = () => { }, ActionLabel = () => "Go" },
        new() { Kind = ItemKind.Choice, Label = () => "Plain", Step = _ => { }, Toggle = () => { } },
        new() { Kind = ItemKind.Slider, Label = () => "Level", Value = () => "50%", Toggle = () => { }, ActionLabel = () => "Silence" },
    ];
    var nav = new OverlayNavigator([new OverlayTab("t", () => "T", () => rows)], () => [], () => []);
    nav.Reset([]);
    Check(nav.Footer()[0] == new FooterHint("Enter", "Go", OverlayCommand.A), "ActionLabel replaces the A label");
    Check(nav.Footer(pad: true)[0] == new FooterHint("A", "Go", OverlayCommand.A), "ActionLabel on the pad footer too");
    Check(nav.Footer().Any(f => f.Keys == "← →" && f.Label == Loc.T("Overlay.Footer.Change")), "ActionLabel keeps the arrow hint");
    nav.Handle(OverlayCommand.Down);
    Check(nav.Footer()[0].Label == Loc.T("Overlay.Footer.Toggle"), "without ActionLabel the kind rule applies");
    nav.Handle(OverlayCommand.Down);
    Check(nav.Footer()[0].Label == "Silence" && nav.Footer().Any(f => f.Label == Loc.T("Overlay.Footer.Volume")), "ActionLabel on a slider");
}

// --- Pad while the name dialog is open ---------------------------------------
{
    Check(DialogPad.Route(OverlayCommand.A) == DialogPadAction.Accept, "pad A accepts the open name dialog");
    Check(DialogPad.Route(OverlayCommand.B) == DialogPadAction.Cancel, "pad B cancels the open name dialog");
    foreach (var command in Enum.GetValues<OverlayCommand>().Where(c => c is not (OverlayCommand.A or OverlayCommand.B)))
        Check(DialogPad.Route(command) == DialogPadAction.Ignore, $"pad {command} is swallowed while the name dialog is open");
    var strings = Loc.AllStrings(CultureInfo.InvariantCulture);
    foreach (var key in new[] { "Dialog.Name.NewChannel", "Dialog.Name.RenameChannel", "Dialog.Name.SavePreset", "Dialog.Name.PadHint" })
        Check(strings.ContainsKey(key), $"string {key} exists");
}

// --- Mouse entry points -----------------------------------------------------
{
    var log = new List<string>();
    var clock = new TestClock { Now = DateTimeOffset.UnixEpoch };
    OverlayItem Act(string name) => new() { Kind = ItemKind.Action, Label = () => name, Action = () => log.Add(name) };
    var power = new OverlayItem
    {
        Kind = ItemKind.Segmented, Label = () => "Power", Confirm = true,
        Options = () => [new OverlayOption(() => "Sleep", () => log.Add("sleep")), new OverlayOption(() => "Off", () => log.Add("off"))],
    };
    var pick = new OverlayItem { Kind = ItemKind.Choice, Label = () => "Pick", Step = d => log.Add($"step {d}") };
    var vol = new OverlayItem { Kind = ItemKind.Slider, Label = () => "Vol", Step = d => log.Add($"vstep {d}"), SetLevel = v => log.Add("level " + v.ToString(CultureInfo.InvariantCulture)) };
    var media = new OverlayItem { Kind = ItemKind.Media, Label = () => "Now" };
    var hidden = new OverlayItem { Kind = ItemKind.Action, Label = () => "Hidden", Visible = () => false };
    var tabA = new OverlayTab("Controlli", () => "A", () => [Act("first"), power, pick, vol, media, hidden]);
    var tabB = new OverlayTab("Audio", () => "B", () => [Act("b-first"), Act("b-second")]);
    var order = new List<string> { "x", "y" };
    var reorder = new OverlayItem { Kind = ItemKind.Reorder, Label = () => "x", Key = "x", Move = d => log.Add($"move {d}"), Commit = () => log.Add("commit"), Cancel = () => log.Add("cancel") };
    var nav = new OverlayNavigator([tabA, tabB], () => [Act("s-first"), Act("s-second")], () => [reorder], clock, new FakeMedia(log));
    nav.Reset([]);
    void Run(NavResult r) => r.Action?.Invoke();

    Check(nav.Hover(2) && nav.Index == 2, "hover selects the row");
    Check(!nav.Hover(2), "hover on the selected row changes nothing");
    Check(!nav.Hover(5) && nav.Index == 2, "hover on a hidden row is ignored");
    Check(!nav.Hover(99), "hover outside the list is ignored");

    nav.Hover(1);
    Run(nav.Click(1, 1));
    Check(nav.IsPending(1, 1), "click arms Power");
    nav.Hover(0);
    Check(!nav.IsPending(1, 1), "hovering away disarms the confirm");
    nav.Hover(1);
    var again = nav.Click(1, 1);
    Check(again.Effect == NavEffect.Redraw && !log.Contains("off"), "hovering back does not re-arm: the next click only arms");

    log.Clear();
    Run(nav.ClickStep(2, 1));
    Check(log.SequenceEqual(["step 1"]) && nav.Index == 2, "picker arrow click selects and steps");
    log.Clear();
    Run(nav.ClickLevel(3, 1.4)); Run(nav.ClickLevel(3, -0.2)); Run(nav.ClickLevel(3, 0.5));
    Check(log.SequenceEqual(["level 1", "level 0", "level 0.5"]), "volume bar clicks clamp to 0..1");
    log.Clear();
    Run(nav.ClickMedia(4, -1)); Run(nav.ClickMedia(4, 0)); Run(nav.ClickMedia(4, 1));
    Check(log.Count == 3 && nav.Index == 4, "media buttons call previous, play/pause, next");

    Check(nav.ClickTab(1).Effect == NavEffect.Redraw && nav.CurrentTab.Id == "Audio" && nav.Index == 0, "tab click switches tab");
    nav.Handle(OverlayCommand.Settings);
    nav.ClickTab(0);
    Check(nav.Page == OverlayPage.Tabs && nav.CurrentTab.Id == "Controlli", "tab click from Settings returns to that tab");

    // A held Reorder row ignores tab clicks and hover.
    var holdNav = new OverlayNavigator([new OverlayTab("Controlli", () => "A", () => [reorder]), tabB], () => [], () => [], clock);
    holdNav.Reset([]);
    holdNav.Handle(OverlayCommand.A);
    Check(holdNav.Holding, "row picked up");
    Check(holdNav.ClickTab(1).Effect == NavEffect.None && holdNav.Holding, "tab click while holding does nothing");
    Check(!holdNav.Hover(0) && holdNav.Holding, "hover while holding is ignored");

    var keys = nav.Footer();
    Check(keys.Any(h => h.Keys == "Esc" && h.Command == OverlayCommand.B), "footer Esc hint carries B");
    Check(keys.Any(h => h.Keys == "F2" && h.Command == OverlayCommand.Settings), "footer F2 hint carries Settings");
    Check(keys.Any(h => h.Keys == "Enter" && h.Command == OverlayCommand.A), "footer Enter hint carries A");
    var padKeys = nav.Footer(pad: true);
    Check(padKeys.Any(h => h.Keys == "B" && h.Command == OverlayCommand.B) && padKeys.Any(h => h.Keys == "Back" && h.Command == OverlayCommand.Settings), "pad footer hints carry commands");
    nav.Hover(2);
    Check(nav.Footer().Where(h => h.Keys == "← →").All(h => h.Command == null), "arrow hints have no single command");
}

// --- Volume bar -------------------------------------------------------------
{
    Check(AudioRules.ClampVolume(1.2, 0.6) == 0.6, "bar level clamps to the cap");
    Check(AudioRules.ClampVolume(-0.1, 1) == 0.0, "bar level clamps to 0");
    Check(AudioRules.ClampVolume(0.37, 1) == 0.37, "bar level inside the range is kept");

    var store = new ConfigStore(Path.Combine(temp, "volbar", "config.json"));
    var monitors = new MonitorProfiles(store, new MonitorSwitcherLocator(store), Path.Combine(temp, "volbar", "none"));
    var audio = new FakeAudio();
    audio.Outputs.Add(new AudioDeviceRef("spk", "Speakers"));
    audio.DefaultOutput = "spk";
    var content = new OverlayContent(store, monitors, new MonitorSwitcherLocator(store), new FakeHost(), audio);
    var nav = new OverlayNavigator(content.Tabs, content.Settings, content.Info);
    nav.Reset(["Home"]);
    nav.ClickTab(1);
    var volumeRow = nav.Items.ToList().FindIndex(i => i.Key == "audio.volume");
    Check(volumeRow >= 0, "Volume row found");
    nav.ClickLevel(volumeRow, 0.5).Action?.Invoke();
    Check(audio.Calls.Contains("SetVolume:0.5"), "clicking the middle of the bar sets 50 %");
}

// --- Mixing rules ---
{
    Check(MixRules.DefaultChannels.Select(c => c.Id).SequenceEqual(new[] { "game", "chat", "media", "other" }), "default channels in order");
    Check(MixRules.Classify(@"C:\Users\a\AppData\Local\Discord\Discord.exe", false) == "chat", "Discord is chat");
    Check(MixRules.Classify(@"C:\x\chrome.exe", false) == "media" && MixRules.Classify(@"C:\x\Spotify.exe", false) == "media" && MixRules.Classify(@"C:\x\vlc.exe", false) == "media", "browser and players are media");
    Check(MixRules.Classify(@"D:\SteamLibrary\steamapps\common\X\x.exe", false) == "game", "Steam library exe is game");
    Check(MixRules.Classify("D:/SteamLibrary/steamapps/common/X/x.exe", false) == "game", "forward slashes work for game folders");
    Check(MixRules.Classify(@"C:\Program Files\Epic Games\Y\y.exe", false) == "game", "Epic exe is game");
    Check(MixRules.Classify(@"C:\GOG Galaxy\Games\Z\z.exe", false) == "game", "GOG exe is game");
    Check(MixRules.Classify(@"C:\tools\unknown.exe", true) == "game" && MixRules.Classify(@"C:\tools\unknown.exe", false) == "other", "unknown: fullscreen game, else other");
    Check(MixRules.Classify(@"C:\x\DISCORD.EXE", true) == "chat", "known app wins over fullscreen, case-insensitive");
    var programs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [@"C:\x\chrome.exe"] = "chat" };
    Check(MixRules.ChannelOf(@"c:\X\Chrome.exe", programs, false) == "chat", "stored choice wins over the rule");
    Check(MixRules.ChannelOf(@"C:\x\vlc.exe", programs, false) == "media", "no stored choice uses the rule");
    Check(Math.Abs(MixRules.Effective(new MixLevel(0.5, false), new MixLevel(0.8, false)) - 0.4) < 1e-9, "effective is the product");
    Check(MixRules.Effective(new MixLevel(0.5, true), new MixLevel(0.8, false)) == 0 && MixRules.Effective(new MixLevel(0.5, false), new MixLevel(0.8, true)) == 0, "either mute gives 0");

    var mixA = new DeviceMix(new() { ["game"] = new MixLevel(0.3, false) }, new() { ["a.exe"] = new MixLevel(0.9, true) });
    var mixes = new Dictionary<string, DeviceMix> { ["A"] = mixA };
    var copied = MixRules.MixFor(mixes, "B", "A");
    Check(copied.Channels["game"].Volume == 0.3 && copied.Apps["a.exe"].Muted && !ReferenceEquals(copied.Channels, mixA.Channels), "unknown device copies the current mix");
    Check(ReferenceEquals(MixRules.MixFor(mixes, "A", "B"), mixA), "known device returns its own mix");
    var empty = MixRules.MixFor(mixes, "C", null);
    Check(empty.Channels.Count == 0 && empty.Apps.Count == 0, "no current device gives an empty mix (nothing stored = nothing written)");
    var empty2 = MixRules.MixFor(mixes, "C", "missing");
    Check(empty2.Channels.Count == 0 && empty2.Apps.Count == 0, "current device without a mix gives an empty mix");

    var chans = new List<AudioChannel>(MixRules.DefaultChannels) { new("c1", "One", "star") };
    var progs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["a.exe"] = "c1", ["b.exe"] = "c1", ["c.exe"] = "chat" };
    var (left, moved) = MixRules.DeleteChannel(chans, progs, "c1");
    Check(left.Count == 4 && left.All(c => c.Id != "c1") && moved["a.exe"] == "other" && moved["b.exe"] == "other" && moved["c.exe"] == "chat", "deleting a custom channel moves its programs to other");
    var (same, samePrograms) = MixRules.DeleteChannel(chans, progs, "game");
    Check(same.Count == 5 && samePrograms["a.exe"] == "c1", "default channels are not deletable");
    Check(MixRules.NextCustomId(chans) == "c2", "next custom id after c1");
    Check(MixRules.NextCustomId([.. MixRules.DefaultChannels, new("c1", "", ""), new("c3", "", "")]) == "c4", "next custom id after c1, c3");

    var baseMix = new DeviceMix(new() { ["game"] = new MixLevel(1, false), ["chat"] = new MixLevel(1, false) }, new() { ["a.exe"] = new MixLevel(0.5, false) });
    var preset = new MixPreset("P", new() { ["game"] = new MixLevel(0.2, true), ["gone"] = new MixLevel(0.1, false) });
    var applied = MixRules.ApplyPreset(baseMix, preset, chans);
    Check(applied.Channels["game"].Volume == 0.2 && applied.Channels["game"].Muted && applied.Channels["chat"].Volume == 1 && !applied.Channels.ContainsKey("gone") && applied.Apps["a.exe"].Volume == 0.5, "preset ignores unknown channels and keeps the rest");
    Check(baseMix.Channels["game"].Volume == 1, "preset does not mutate the original mix");

    var plainPrograms = new Dictionary<string, string> { [@"C:\x\chrome.exe"] = "chat" };
    Check(MixRules.ChannelOf(@"c:\X\CHROME.exe", plainPrograms, false) == "chat", "stored choice wins with a case-sensitive dictionary");
    Check(MixRules.Classify(@"C:\steamapps\commonX\a.exe", false) == "other", "steamapps commonX is not a game folder");
    var before = progs.ToDictionary(p => p.Key, p => p.Value);
    MixRules.DeleteChannel(chans, progs, "c1");
    Check(progs.Count == before.Count && before.All(p => progs[p.Key] == p.Value), "DeleteChannel leaves its input unmodified");
    var loadedMix = System.Text.Json.JsonSerializer.Deserialize<DeviceMix>("""{"channels":{"game":{"volume":0.5,"muted":false}},"apps":{"C:\\x\\A.exe":{"volume":0.25,"muted":true}}}""", ConfigStore.JsonOptions)!;
    Check(MixRules.AppLevel(loadedMix, @"c:\x\a.EXE") == new MixLevel(0.25, true), "AppLevel finds a differently cased exe in a JSON-loaded mix");
    Check(MixRules.AppLevel(loadedMix, @"C:\none.exe") == new MixLevel(1.0, false), "AppLevel defaults to 1.0 unmuted");
    var copiedLoaded = MixRules.MixFor(new Dictionary<string, DeviceMix> { ["A"] = loadedMix }, "B", "A");
    Check(copiedLoaded.Apps.TryGetValue(@"c:\x\a.exe", out var cl) && cl.Muted, "copied mix keeps app lookups case-insensitive");
    var presetApplied = MixRules.ApplyPreset(loadedMix, new MixPreset("Q", []), MixRules.DefaultChannels);
    Check(presetApplied.Apps.ContainsKey(@"c:\x\a.exe"), "preset copy keeps app lookups case-insensitive");

    var dupMix = new DeviceMix([], new() { [@"C:\a.exe"] = new MixLevel(0.1, false), [@"c:\A.exe"] = new MixLevel(0.2, true) });
    var dupCopy = MixRules.MixFor(new Dictionary<string, DeviceMix> { ["A"] = dupMix }, "B", "A");
    var dupPreset = MixRules.ApplyPreset(dupMix, new MixPreset("Q", []), MixRules.DefaultChannels);
    Check(dupCopy.Apps.Count == 1 && dupPreset.Apps.Count == 1, "case-variant duplicate app keys collapse to one entry without throwing");

    var groups = MixRules.GroupByProgram(new[] { "CHROME.EXE", "chrome.exe", "Chrome.exe", "vlc.exe" }, s => s);
    Check(groups.Count == 2 && groups[0].Sessions.Count == 3 && groups[1].Sessions.Count == 1, "sessions group by exe case-insensitively");

    var ac = new AppConfig();
    Check(ac.AudioChannels.Count == 0 && ac.AudioPrograms.Count == 0 && ac.AudioMix.Count == 0 && ac.AudioPresets.Count == 0, "mixing config defaults are empty");
    var json = System.Text.Json.JsonSerializer.Serialize(ac, ConfigStore.JsonOptions);
    foreach (var key in new[] { "audio_channels", "audio_programs", "audio_mix", "audio_presets" })
        Check(json.Contains($"\"{key}\""), $"config key {key}");
    var mixPath = Path.Combine(temp, "mixcfg", "config.json");
    var mixStore = new ConfigStore(mixPath);
    mixStore.Update(c =>
    {
        c.AudioChannels = [.. MixRules.DefaultChannels, new AudioChannel("c1", "Mine", "star")];
        c.AudioPrograms[@"C:\x\a.exe"] = "c1";
        c.AudioMix["dev"] = mixA;
        c.AudioPresets = [preset];
    });
    var back = new ConfigStore(mixPath).Current;
    Check(back.AudioChannels.Count == 5 && back.AudioChannels[4] == new AudioChannel("c1", "Mine", "star") && back.AudioPrograms[@"C:\x\a.exe"] == "c1"
        && back.AudioMix["dev"].Channels["game"].Volume == 0.3 && back.AudioMix["dev"].Apps["a.exe"].Muted
        && back.AudioPresets[0].Name == "P" && back.AudioPresets[0].Channels["game"].Muted, "mixing config round-trips through ConfigStore");
    var nullMix = Path.Combine(temp, "mixnull.json");
    File.WriteAllText(nullMix, """{ "audio_channels": null, "audio_programs": null, "audio_mix": null, "audio_presets": null }""");
    var nm = new ConfigStore(nullMix).Current;
    Check(nm.AudioChannels.Count == 0 && nm.AudioPrograms.Count == 0 && nm.AudioMix.Count == 0 && nm.AudioPresets.Count == 0, "null mixing collections load as empty");
}

// --- Mixing rules: channel of a program, validated ---
{
    var channels = new List<AudioChannel>(MixRules.DefaultChannels) { new("c2", "Two", "star") };
    var programs = new Dictionary<string, string> { [@"C:\x\a.exe"] = "c1", [@"C:\x\b.exe"] = "c2", [@"C:\x\chrome.exe"] = "chat" };
    Check(MixRules.ChannelFor(@"C:\X\A.EXE", programs, channels) == "other", "a stored channel that no longer exists falls back to other");
    Check(MixRules.ChannelFor(@"c:\x\B.exe", programs, channels) == "c2", "a stored custom channel is kept (any casing)");
    Check(MixRules.ChannelFor(@"C:\x\chrome.exe", programs, channels) == "chat", "a stored default channel is kept");
    Check(MixRules.ChannelFor(@"C:\x\vlc.exe", programs, channels) == "media", "an unknown program uses the rule");
    Check(MixRules.ChannelFor(@"C:\x\vlc.exe", programs, []) == "media", "no channel list means the default channels");

    var sl = MixRules.SessionLevel(new MixLevel(0.5, true), new MixLevel(0.8, false));
    Check(Math.Abs(sl.Volume - 0.4) < 1e-9 && sl.Muted, "muted channel: session volume kept, mute flag set");
    sl = MixRules.SessionLevel(new MixLevel(0.5, false), new MixLevel(0.8, true));
    Check(Math.Abs(sl.Volume - 0.4) < 1e-9 && sl.Muted, "muted app: session volume kept, mute flag set");
    sl = MixRules.SessionLevel(new MixLevel(0.5, false), new MixLevel(0.8, false));
    Check(Math.Abs(sl.Volume - 0.4) < 1e-9 && !sl.Muted, "unmuted: product, not muted");
    Check(MixRules.SessionLevel(new MixLevel(1.5, false), new MixLevel(1.2, false)).Volume == 1.0, "session volume clamped above 1");
    Check(MixRules.SessionLevel(new MixLevel(-0.5, false), new MixLevel(0.8, false)).Volume == 0.0, "session volume clamped below 0");
}

// --- Mixing rules: only what the user set is written ---
{
    var none = new DeviceMix([], []);
    Check(!MixRules.ShouldWrite(none, "game", @"C:\x\a.exe"), "untouched channel and app: the session is not written");
    var set = new DeviceMix(new() { ["game"] = new MixLevel(1.0, false) }, new() { [@"C:\x\A.exe"] = new MixLevel(1.0, false) });
    Check(MixRules.ShouldWrite(set, "game", @"C:\x\b.exe"), "a stored channel level (even 100 % unmuted) is written");
    Check(MixRules.ShouldWrite(set, "chat", @"c:\X\a.EXE"), "a stored app level is written (any casing)");
    Check(!MixRules.ShouldWrite(set, "chat", @"C:\x\b.exe"), "neither stored: not written");
    var loaded = System.Text.Json.JsonSerializer.Deserialize<DeviceMix>("""{"channels":null,"apps":null}""", ConfigStore.JsonOptions)!;
    Check(!MixRules.ShouldWrite(loaded, "game", @"C:\x\a.exe"), "a mix loaded with null collections writes nothing and does not throw");
    var copiedEmpty = MixRules.MixFor(new Dictionary<string, DeviceMix> { ["A"] = new([], []) }, "B", "A");
    Check(copiedEmpty.Channels.Count == 0 && copiedEmpty.Apps.Count == 0, "a new device copying an untouched mix stays untouched");

    var path = Path.Combine(temp, "shouldwrite", "config.json");
    var store = new ConfigStore(path);
    var audio = new FakeAudio();
    audio.Outputs.Add(new AudioDeviceRef("spk", "Speakers"));
    audio.DefaultOutput = "spk";
    var mix = new MixController(store, audio);
    Check(mix.CurrentMix().Channels.Count == 0 && mix.ChannelLevel("game") == new MixLevel(1.0, false), "untouched: UI shows 100 % unmuted, nothing stored");
    mix.ToggleChannelMute("game");
    mix.ToggleChannelMute("game");
    var stored = store.Current.AudioMix["spk"];
    Check(stored.Channels["game"] == new MixLevel(1.0, false) && MixRules.ShouldWrite(stored, "game", @"C:\x\a.exe"),
        "unmute after mute stays stored, so the unmute is written");
    Check(!MixRules.ShouldWrite(stored, "chat", @"C:\x\a.exe"), "editing one channel does not make the others written");
    mix.ToggleAppMute(@"C:\x\a.exe");
    mix.ToggleAppMute(@"C:\x\a.exe");
    Check(MixRules.ShouldWrite(store.Current.AudioMix["spk"], "chat", @"C:\x\a.exe"), "an app set back to 100 % is still written");
}

// --- Overlay: Remove that asks for confirmation ---
{
    var clock = new TestClock { Now = DateTimeOffset.UnixEpoch };
    var log = new List<string>();
    OverlayItem Row(string key, bool confirm) => new()
    {
        Kind = ItemKind.Reorder, Key = key, Label = () => key, RemoveEntry = () => log.Add("remove " + key), ConfirmRemove = confirm,
    };
    var rows = new[] { Row("a", true), Row("b", true), Row("c", false) };
    var nav = new OverlayNavigator([new OverlayTab("Controlli", () => "C", () => rows)], () => [], () => [], clock);
    void Run(NavResult r) => r.Action?.Invoke();
    nav.Reset([]);
    var first = nav.Handle(OverlayCommand.Remove);
    Check(first.Effect == NavEffect.Redraw && log.Count == 0 && nav.IsRemovePending(0), "first Remove arms the delete");
    Check(nav.Footer(pad: true).Any(h => h.Keys == "Y" && h.Command == OverlayCommand.Remove && h.Label == Loc.T("Overlay.Footer.ConfirmDelete")),
        "footer asks for the second press");
    Check(!nav.IsPending(0, 0), "a pending delete is not a pending segment confirm");
    clock.Now += TimeSpan.FromSeconds(2.9);
    var second = nav.Handle(OverlayCommand.Remove);
    Check(second.Effect == NavEffect.RunHere && !nav.IsRemovePending(0), "second Remove within 3 s deletes");
    Run(second);
    Check(log.SequenceEqual(["remove a"]), "the row was deleted once");
    log.Clear();
    Run(nav.Handle(OverlayCommand.Remove));
    clock.Now += TimeSpan.FromSeconds(3.1);
    var late = nav.Handle(OverlayCommand.Remove);
    Check(late.Effect == NavEffect.Redraw && log.Count == 0 && nav.IsRemovePending(0), "a late second press re-arms instead of deleting");
    nav.Handle(OverlayCommand.Down);
    Check(!nav.IsRemovePending(0) && !nav.IsRemovePending(1), "moving to another row disarms");
    Run(nav.Handle(OverlayCommand.Remove));
    Run(nav.Handle(OverlayCommand.Right));
    Check(!nav.IsRemovePending(1), "another command disarms");
    Run(nav.Handle(OverlayCommand.Remove));
    Run(nav.Handle(OverlayCommand.A));
    Check(!nav.IsRemovePending(1) && log.Count == 0, "picking the row up disarms");
    Run(nav.Handle(OverlayCommand.B));
    Run(nav.Handle(OverlayCommand.Remove));
    Check(!nav.ExpirePending(), "ExpirePending false before the deadline");
    clock.Now += TimeSpan.FromSeconds(3.1);
    Check(nav.ExpirePending() && !nav.IsRemovePending(1), "ExpirePending clears a pending delete");
    nav.Handle(OverlayCommand.Down);
    Run(nav.Handle(OverlayCommand.Remove));
    Check(log.SequenceEqual(["remove c"]), "a row without ConfirmRemove still deletes at once");
}

// --- Overlay: suggested channel name skips names in use ---
{
    var defaults = MixRules.DefaultChannels.ToList();
    Check(OverlayContent.SuggestedChannelName(defaults) == Loc.T("Settings.Audio.ChannelName", 5), "four channels: Channel 5");
    var taken = new List<AudioChannel>(defaults) { new("c1", Loc.T("Settings.Audio.ChannelName", 6), "star") };
    Check(OverlayContent.SuggestedChannelName(taken) == Loc.T("Settings.Audio.ChannelName", 7), "Channel 6 in use: Channel 7");
    var upper = new List<AudioChannel>(defaults) { new("c1", Loc.T("Settings.Audio.ChannelName", 6).ToUpperInvariant(), "star") };
    Check(OverlayContent.SuggestedChannelName(upper) == Loc.T("Settings.Audio.ChannelName", 7), "names in use compare case-insensitively");
}

// --- Switch breaker ---
{
    var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    DateTime At(double seconds) => t0.AddSeconds(seconds);
    var b = new SwitchBreaker();
    Check(b.Check(At(0), true) == BreakerDecision.Allowed && b.Check(At(1), true) == BreakerDecision.Allowed
        && b.Check(At(2), true) == BreakerDecision.Allowed, "three switches within 10 s are allowed");
    Check(b.Check(At(3), true) == BreakerDecision.Tripped, "the fourth switch within 10 s trips the breaker");
    Check(b.Check(At(4), true) == BreakerDecision.Paused && b.Check(At(13.5), true) == BreakerDecision.Paused,
        "paused while device events keep coming within 10 s");
    Check(b.Check(At(24), true) == BreakerDecision.Allowed, "the next event after a 10 s quiet period resumes");
    Check(b.Check(At(25), true) == BreakerDecision.Allowed && b.Check(At(26), true) == BreakerDecision.Allowed
        && b.Check(At(27), true) == BreakerDecision.Tripped, "after resuming the count starts again");

    var spread = new SwitchBreaker();
    foreach (var s in new[] { 0.0, 1, 2, 11, 12 })
        Check(spread.Check(At(s), true) == BreakerDecision.Allowed, $"switches older than 10 s do not count ({s})");

    var noSwitch = new SwitchBreaker();
    noSwitch.Check(At(0), true);
    noSwitch.Check(At(1), true);
    Check(noSwitch.Check(At(2), false) == BreakerDecision.Allowed && noSwitch.Check(At(3), true) == BreakerDecision.Allowed,
        "an event that needs no switch does not count");
    Check(noSwitch.Check(At(4), true) == BreakerDecision.Tripped, "but switches still do");
    Check(noSwitch.Check(At(9), false) == BreakerDecision.Paused, "an event while paused restarts the quiet period");
    Check(noSwitch.Check(At(18), true) == BreakerDecision.Paused, "quiet period counts from the last event");
    Check(noSwitch.Check(At(28.5), true) == BreakerDecision.Allowed, "resumes after 10 s without events");
}

// --- Steam library ----------------------------------------------------------
{
    var root = Path.Combine(temp, "steam", "Steam Root");
    var lib2 = Path.Combine(temp, "steam", "Library Two");
    Directory.CreateDirectory(Path.Combine(root, "steamapps"));
    Directory.CreateDirectory(Path.Combine(lib2, "steamapps"));
    File.WriteAllText(Path.Combine(root, "steamapps", "libraryfolders.vdf"),
        "\"libraryfolders\"\n{\n \"0\"\n {\n  \"path\" \"" + root.Replace("\\", "\\\\") + "\"\n }\n \"1\"\n {\n  \"path\" \"" + lib2.Replace("\\", "\\\\") + "\"\n  \"label\" \"say \\\"hi\\\"\"\n }\n}\n");
    void Manifest(string lib, int appid, string name, long lastPlayed, string? installDir = null) =>
        File.WriteAllText(Path.Combine(lib, "steamapps", $"appmanifest_{appid}.acf"),
            $"\"AppState\"\n{{\n \"appid\" \"{appid}\"\n \"name\" \"{name}\"\n \"LastPlayed\" \"{lastPlayed}\"\n"
            + (installDir is null ? "" : $" \"installdir\" \"{installDir}\"\n") + "}\n");
    Manifest(root, 10, "Alpha™", 100);
    Manifest(lib2, 20, "ゼルダ Beta", 300, "Ember Hollow");
    Manifest(root, 228980, "Steamworks Common Redistributables", 0);
    File.WriteAllText(Path.Combine(root, "steamapps", "appmanifest_99.acf"), "garbage {{{");

    var cache = Path.Combine(root, "appcache", "librarycache");
    Directory.CreateDirectory(Path.Combine(cache, "20", "abcdef"));
    File.WriteAllText(Path.Combine(cache, "20", "abcdef", "library_600x900.jpg"), "x");
    File.WriteAllText(Path.Combine(cache, "10_library_600x900.jpg"), "x");

    // Binary shortcuts.vdf: 0x00 map, 0x01 string, 0x02 int32, 0x08 end.
    var user = "123";
    var config = Path.Combine(root, "userdata", user, "config");
    Directory.CreateDirectory(Path.Combine(config, "grid"));
    var bytes = new List<byte>();
    void Str(string s) { bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(s)); bytes.Add(0); }
    void Map(string key) { bytes.Add(0x00); Str(key); }
    void Text(string key, string value) { bytes.Add(0x01); Str(key); Str(value); }
    void Int(string key, int value) { bytes.Add(0x02); Str(key); bytes.AddRange(BitConverter.GetBytes(value)); }
    Map("shortcuts"); Map("0");
    Text("AppName", "My Emu & Co"); Text("Exe", "\"C:\\Games\\My Emu\\emu.exe\""); Int("appid", unchecked((int)0x9ABCDEF0)); Int("LastPlayTime", 200);
    bytes.Add(0x08); bytes.Add(0x08); bytes.Add(0x08);
    File.WriteAllBytes(Path.Combine(config, "shortcuts.vdf"), bytes.ToArray());
    File.WriteAllText(Path.Combine(config, "grid", $"{0x9ABCDEF0u}p.png"), "x");

    var reg = new FakeRegistry();
    reg.Set(Microsoft.Win32.RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamPath", root);
    reg.Set(Microsoft.Win32.RegistryHive.CurrentUser, @"Software\Valve\Steam\ActiveProcess", "ActiveUser", user);

    var text = Vdf.ParseText("\"a\" { \"b\" \"1\" \"c\" { \"d\" \"x \\\"y\\\"\" } }");
    Check(text.Get("a")?.Get("c")?.Value("d") == "x \"y\"", "text VDF: nested blocks and escaped quotes");

    var games = SteamLibrary.Read(reg);
    var byId = games.ToDictionary(g => g.Id);
    Check(byId.ContainsKey("steam:10") && byId.ContainsKey("steam:20"), "both library folders scanned");
    Check(!byId.ContainsKey("steam:228980"), "redistributables skipped");
    Check(byId.TryGetValue("steam:10", out var alpha) && alpha.Title == "Alpha™" && alpha.LastPlayed == 100
        && alpha.LaunchTarget == "steam://rungameid/10", "manifest fields and launch URL");
    Check(byId.TryGetValue("steam:20", out var beta) && beta.Title == "ゼルダ Beta", "unicode title kept");
    Check(beta?.ArtPath?.EndsWith("library_600x900.jpg") == true, "art found in the per-game folder");
    Check(alpha?.ArtPath?.EndsWith("10_library_600x900.jpg") == true, "art found in the flat layout");
    var gameId = ((ulong)0x9ABCDEF0u << 32) | 0x02000000;
    Check(byId.TryGetValue($"steam:{gameId}", out var shortcut) && shortcut.Title == "My Emu & Co"
        && shortcut.LaunchTarget == $"steam://rungameid/{gameId}" && shortcut.LastPlayed == 200, "non-Steam shortcut read");
    Check(shortcut?.ArtPath?.EndsWith("p.png") == true, "shortcut grid art found");
    Check(games.Count == 3, "garbled manifest ignored");
    Check(beta?.InstallDir == Path.Combine(lib2, "steamapps", "common", "Ember Hollow"), "Steam installdir resolved under the library's steamapps/common");
    Check(alpha?.InstallDir is null, "Steam manifest without installdir: no install folder");
    Check(shortcut?.InstallDir == @"C:\Games\My Emu", "Steam shortcut: install folder is the exe's folder");

    Check(SteamLibrary.Read(new FakeRegistry()).Count == 0, "no Steam installed: empty, no throw");
    var truncated = Path.Combine(config, "shortcuts.vdf");
    File.WriteAllBytes(truncated, bytes.Take(bytes.Count / 2).ToArray());
    Check(SteamLibrary.Read(reg).Count == 2, "truncated shortcuts.vdf: shortcuts skipped, games kept");
}

// --- Other libraries and library rules -------------------------------------
{
    var hk = Microsoft.Win32.RegistryHive.LocalMachine;
    var reg = new FakeRegistry();

    // Epic: manifests folder from the launcher's registry AppDataPath.
    var epicData = Path.Combine(temp, "epic", "Data");
    Directory.CreateDirectory(Path.Combine(epicData, "Manifests"));
    reg.Set(hk, @"SOFTWARE\WOW6432Node\Epic Games\EpicGamesLauncher", "AppDataPath", epicData + Path.DirectorySeparatorChar);
    void Item(string file, string json) => File.WriteAllText(Path.Combine(epicData, "Manifests", file), json);
    Item("a.item", """{"DisplayName":"Fortnite & Friends","AppName":"Fort","MainGameAppName":"Fort","InstallLocation":"D:\\Epic Games\\Fort","LaunchExecutable":"Fort.exe","AppCategories":["public","games"]}""");
    Item("b.item", """{"DisplayName":"Fort DLC","AppName":"FortDlc","MainGameAppName":"Fort","InstallLocation":"D:\\Epic Games\\Fort","AppCategories":["public","games","addons"]}""");
    Item("c.item", """{"DisplayName":"Some Tool","AppName":"Tool","MainGameAppName":"Tool","AppCategories":["public","applications"]}""");
    Item("d.item", "{ not json");
    var epic = EpicLibrary.Read(reg);
    Check(epic.Count == 1 && epic[0].Id == "epic:Fort" && epic[0].Title == "Fortnite & Friends"
        && epic[0].LaunchTarget == "com.epicgames.launcher://apps/Fort?action=launch&silent=true", "Epic game read, DLC/apps/garbage skipped");
    Check(epic[0].InstallDir == @"D:\Epic Games\Fort", "Epic InstallLocation is the install folder");
    Item("e.item", """{"DisplayName":"Slash","AppName":"Slash","MainGameAppName":"Slash","InstallLocation":"D:\\Epic\\Game\\","AppCategories":["games"]}""");
    Item("f.item", """{"DisplayName":"Root","AppName":"Root","MainGameAppName":"Root","InstallLocation":"D:\\","AppCategories":["games"]}""");
    var epic2 = EpicLibrary.Read(reg).ToDictionary(g => g.Id);
    Check(epic2["epic:Slash"].InstallDir == @"D:\Epic\Game", "Epic install folder: trailing separator trimmed");
    Check(epic2["epic:Root"].InstallDir is null, "Epic install folder at a drive root: null");
    Check(GamePaths.Full(@"E:\") is null && GamePaths.Full("") is null && GamePaths.Full(null) is null, "GamePaths.Full: drive roots and empty give null");
    Check(GamePaths.Full(Path.GetDirectoryName(@"D:\game.exe")) is null, "an exe at a drive root has no install folder");
    Check(GamePaths.Full(@"\\srv\share") is null && GamePaths.Full(@"\\srv\share\g\") == @"\\srv\share\g", "GamePaths.Full: share root null, share folder trimmed");
    Item("e.item", "{}"); Item("f.item", "{}");
    Check(EpicLibrary.Read(new FakeRegistry()).Count == 0, "no Epic: empty");

    // GOG: games from GOG's registry entries.
    var gogKey = @"SOFTWARE\WOW6432Node\GOG.com\Games";
    var gogDir = Path.Combine(temp, "gog", "The Witcher 3");
    Directory.CreateDirectory(gogDir);
    var gogExe = Path.Combine(gogDir, "witcher3.exe");
    File.WriteAllText(gogExe, "x");
    reg.Set(hk, gogKey + @"\1207664643", "gameName", "The Witcher 3");
    reg.Set(hk, gogKey + @"\1207664643", "gameID", "1207664643");
    reg.Set(hk, gogKey + @"\1207664643", "exe", gogExe);
    reg.Set(hk, gogKey + @"\1207664643", "workingDir", gogDir);
    reg.Set(hk, gogKey + @"\1207664643", "launchParam", "-conf \"witcher.conf\"");
    reg.Set(hk, gogKey + @"\1207664643", "path", gogDir);
    reg.Set(hk, gogKey + @"\555", "gameName", "A DLC");
    var gogDir2 = Path.Combine(temp, "gog", "Other Game");
    Directory.CreateDirectory(gogDir2);
    var gogExe2 = Path.Combine(gogDir2, "other.exe");
    File.WriteAllText(gogExe2, "x");
    reg.Set(hk, gogKey + @"\777", "gameName", "Other Game");
    reg.Set(hk, gogKey + @"\777", "exe", gogExe2);
    var gogAll = GogLibrary.Read(reg);
    var gog = gogAll.Where(g => g.Id == "gog:1207664643").ToList();
    Check(gogAll.Count == 2 && gog.Count == 1 && gog[0].LaunchTarget == gogExe && gog[0].WorkingDir == gogDir,
        "GOG game read with its working directory; entry without exe skipped");
    Check(gog[0].InstallDir == gogDir, "GOG registry path is the install folder");
    Check(gogAll.Single(g => g.Id == "gog:777").InstallDir == gogDir2, "GOG without path: the exe's folder");
    Check(gog[0].Arguments == "-conf \"witcher.conf\"", "GOG launchParam kept as the arguments (DOSBox / ScummVM games)");

    // Emulators: uninstall entries matched by name, exe must exist.
    var uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    var raDir = Path.Combine(temp, "emu", "RetroArch");
    Directory.CreateDirectory(raDir);
    File.WriteAllText(Path.Combine(raDir, "retroarch.exe"), "x");
    reg.Set(hk, uninstall + @"\RetroArch", "DisplayName", "RetroArch 1.19");
    reg.Set(hk, uninstall + @"\RetroArch", "InstallLocation", raDir);
    var dolphinExe = Path.Combine(temp, "emu", "Dolphin Emulator", "Dolphin.exe");
    Directory.CreateDirectory(Path.GetDirectoryName(dolphinExe)!);
    File.WriteAllText(dolphinExe, "x");
    reg.Set(Microsoft.Win32.RegistryHive.CurrentUser, uninstall + @"\Dolphin", "DisplayName", "Dolphin");
    reg.Set(Microsoft.Win32.RegistryHive.CurrentUser, uninstall + @"\Dolphin", "DisplayIcon", "\"" + dolphinExe + "\",0");
    var meloDir = Path.Combine(temp, "emu", "melonDS");
    Directory.CreateDirectory(meloDir);
    File.WriteAllText(Path.Combine(meloDir, "uninst.exe"), "x");
    File.WriteAllText(Path.Combine(meloDir, "melonDS.exe"), "x");
    reg.Set(hk, uninstall + @"\melonDS", "DisplayName", "melonDS");
    reg.Set(hk, uninstall + @"\melonDS", "DisplayIcon", Path.Combine(meloDir, "uninst.exe"));
    reg.Set(hk, uninstall + @"\melonDS", "InstallLocation", meloDir);
    reg.Set(hk, uninstall + @"\Notepad++", "DisplayName", "Notepad++");
    reg.Set(hk, uninstall + @"\PCSX2", "DisplayName", "PCSX2");
    reg.Set(hk, uninstall + @"\PCSX2", "InstallLocation", Path.Combine(temp, "emu", "missing"));
    var emulators = EmulatorLibrary.Read(reg).ToDictionary(e => e.Title);
    Check(emulators.ContainsKey("RetroArch 1.19") && emulators.ContainsKey("Dolphin"), "installed emulators detected");
    Check(!emulators.ContainsKey("Notepad++") && !emulators.ContainsKey("PCSX2"), "unrelated apps and missing exes skipped");
    Check(emulators["Dolphin"].LaunchTarget == dolphinExe && emulators["Dolphin"].Source == GameSource.Emulator, "emulator exe from DisplayIcon");
    Check(emulators["Dolphin"].InstallDir == Path.GetDirectoryName(dolphinExe) && emulators["RetroArch 1.19"].InstallDir == raDir, "emulator install folder is the exe's folder");
    Check(emulators["melonDS"].LaunchTarget == Path.Combine(meloDir, "melonDS.exe"), "a DisplayIcon pointing at the uninstaller is not used");

    // Hand-added apps.
    var appExe = Path.Combine(temp, "apps", "Quiver Launcher & Co", "quiver.exe");
    Directory.CreateDirectory(Path.GetDirectoryName(appExe)!);
    File.WriteAllText(appExe, "x");
    var apps = ManualApps.Read([appExe, Path.Combine(temp, "apps", "gone.exe")]);
    Check(apps.Count == 1 && apps[0].Title == "quiver" && apps[0].LaunchTarget == appExe && apps[0].Id == "app:" + appExe.ToLowerInvariant(),
        "hand-added app read (path with spaces and &), missing one skipped");

    Check(apps[0].InstallDir == Path.GetDirectoryName(appExe), "hand-added app install folder is the exe's folder");

    // Library rules.
    GameEntry G(string id, GameSource src, string title, long played = 0) => new(id, src, title, "x", played, null, null);
    var all = GameLibrary.Merge([[G("steam:1", GameSource.Steam, "b", 5), G("steam:2", GameSource.Steam, "Zed")],
                                 [G("epic:1", GameSource.Epic, "alpha"), G("steam:1", GameSource.Steam, "dup")]], [GameSource.Gog]);
    Check(all.Count == 3, "merge drops duplicate ids");
    Check(GameLibrary.Merge([[G("epic:1", GameSource.Epic, "a")]], [GameSource.Epic]).Count == 0, "hidden source dropped");
    var sorted = GameLibrary.Sort(all);
    Check(sorted.Select(g => g.Id).SequenceEqual(["steam:1", "epic:1", "steam:2"]), "sort: last played, then title A-Z ignoring case");
    Check(GameLibrary.Filter(sorted, GameSource.Epic).Single().Id == "epic:1" && GameLibrary.Filter(sorted, null).Count == 3, "filter by source");
    Check(GameLibrary.FilterCycle(sorted).SequenceEqual(new GameSource?[] { null, GameSource.Steam, GameSource.Epic }), "filter cycle lists present sources");
    Check(GameLibrary.NextFilter(null, sorted) == GameSource.Steam && GameLibrary.NextFilter(GameSource.Epic, sorted) == null, "next filter wraps to All");
    Check(GameLibrary.NextFilter(GameSource.Gog, sorted) == null, "vanished filter falls back to All");
    var many = Enumerable.Range(0, 1000).Select(i => G($"steam:{i}", (GameSource)(i % 6), $"Game {i % 97}", i % 13)).ToList();
    var watch = System.Diagnostics.Stopwatch.StartNew();
    var big = GameLibrary.Filter(GameLibrary.Sort(many), GameSource.Steam);
    Check(watch.ElapsedMilliseconds < 50 && big.Count > 0, "sort and filter of 1,000 games is fast");

    var store = new ConfigStore(Path.Combine(temp, "games", "config.json"));
    Check(store.Current.GamesSourcesHidden.Count == 0 && store.Current.GamesManualApps.Count == 0, "games config defaults");
    var json = JsonSerializer.Serialize(store.Current, ConfigStore.JsonOptions);
    Check(json.Contains("\"games_sources_hidden\"") && json.Contains("\"games_manual_apps\""), "games config keys");
}

// --- Home tab ---------------------------------------------------------------
{
    Loc.SetLanguage("en");
    var store = new ConfigStore(Path.Combine(temp, "hometab", "config.json"));
    var monitors = new MonitorProfiles(store, new MonitorSwitcherLocator(store), Path.Combine(temp, "hometab", "none"));
    var audio = new FakeAudio();
    audio.Outputs.Add(new AudioDeviceRef("spk", "Speakers"));
    audio.Inputs.Add(new AudioDeviceRef("mic", "Mic"));
    audio.DefaultOutput = "spk";
    audio.DefaultInput = "mic";
    var media = new FakeMedia(new List<string>());
    var content = new OverlayContent(store, monitors, new MonitorSwitcherLocator(store), new FakeHost(), audio, media);
    void Run(NavResult r) => r.Action?.Invoke();
    OverlayNavigator Nav()
    {
        var n = new OverlayNavigator(content.Tabs, content.Settings, content.Info, media: content.Media);
        content.ResetTransient();
        n.Reset(store.Current.HiddenTabs);
        return n;
    }

    Check(content.Tabs.Select(t => t.Id).SequenceEqual(["Home", "Controlli", "Audio", "Giochi"]) && OverlayContent.HomeTabId == "Home",
        "tabs: Home, Controls, Audio, Games");
    Check(content.Tabs[0].Tiles && !content.Tabs[1].Tiles && !content.Tabs[2].Tiles && !content.Tabs[3].Tiles, "only Home draws tiles");
    Check(content.Tabs[0].Title() == "Home", "Home tab title");
    Check(content.Home().Select(i => i.Key).SequenceEqual(
        ["audio.volume", "audio.media", "audio.input", "controls.monitor", "controls.closewindow", "controls.bigpicture"]),
        "Home items in order");

    var nav = Nav();
    Check(nav.CurrentTab.Id == "Home" && nav.Selected?.Key == "audio.volume", "overlay opens on Home, Volume selected");
    nav.Handle(OverlayCommand.Down);
    Check(nav.Selected?.Key == "audio.input", "Down from Volume skips the Now playing tile without media");
    nav.Handle(OverlayCommand.Down);
    Check(nav.Selected?.Key == "controls.closewindow", "invisible monitor tile skipped too");
    nav.Handle(OverlayCommand.Up); nav.Handle(OverlayCommand.Up);
    Check(nav.Selected?.Key == "audio.volume", "Up returns to Volume");
    Run(nav.Handle(OverlayCommand.Right));
    Run(nav.Handle(OverlayCommand.A));
    Check(audio.Calls.SequenceEqual(["StepVolume:1", "ToggleMute"]), "Volume tile: Right steps, A mutes");
    audio.Calls.Clear();
    nav.Handle(OverlayCommand.Down);
    Run(nav.Handle(OverlayCommand.A));
    Check(audio.Calls.SequenceEqual(["ToggleMicMute"]), "Input tile: A toggles the mic mute");
    audio.Calls.Clear();

    media.Current = new NowPlaying("Song", "Artist", "Spotify", true);
    nav = Nav();
    nav.Handle(OverlayCommand.Down);
    Check(nav.Selected?.Key == "audio.media", "Now playing tile selectable with media");
    media.Current = null;

    store.Update(c => c.HiddenTabs.Add("Home"));
    nav = Nav();
    Check(nav.CurrentTab.Id == "Controlli" && nav.Selected != null, "Home hidden: the overlay opens on Controls");
    store.Update(c => c.HiddenTabs.Clear());

    var home = content.Home();
    Check(home.Where(i => i.Visible()).Select(i => i.Key).SequenceEqual(["audio.volume", "audio.input", "controls.closewindow", "controls.bigpicture"]),
        "Home without media or profiles still has Volume, Mic, Close window");
    var audioRows = content.Audio();
    Check(content.Home()[0] != audioRows[0] && content.Home().Count == 6, "each Home call builds its own items");

    var settingsNav = new OverlayNavigator(content.Tabs, content.Settings, content.Info);
    settingsNav.Reset([]);
    var tabRows = content.Settings().Take(4).ToList();
    Check(tabRows[0].Label() == "Home" && tabRows[0].Section?.Invoke() == Loc.T("Overlay.Section.Tabs")
        && tabRows[1].Label() == Loc.T("Overlay.Tab.Controls") && tabRows[1].Section == null, "Settings: Home row first with the Tabs section");
    Check(tabRows[0].IsOn(), "Home tab on by default");
    tabRows[0].Toggle!();
    Check(store.Current.HiddenTabs.Contains("Home") && !content.Settings()[0].IsOn(), "Home toggle hides it");
    content.Settings()[0].Toggle!();
    Check(!store.Current.HiddenTabs.Contains("Home"), "Home toggle shows it again");
}

// --- Games tab --------------------------------------------------------------
{
    var store = new ConfigStore(Path.Combine(temp, "gamestab", "config.json"));
    var monitors = new MonitorProfiles(store, new MonitorSwitcherLocator(store), Path.Combine(temp, "gamestab", "none"));
    var host = new FakeHost();
    GameEntry G(string id, GameSource src, string title) => new(id, src, title, "launch:" + id, 0, null, null);
    var view = new FakeGames();
    view.Set([G("steam:1", GameSource.Steam, "A"), G("steam:2", GameSource.Steam, "B"), G("steam:3", GameSource.Steam, "C"),
              G("epic:4", GameSource.Epic, "D"), G("epic:5", GameSource.Epic, "E")]);
    var content = new OverlayContent(store, monitors, new MonitorSwitcherLocator(store), host, new FakeAudio(), null, null, view);
    var nav = new OverlayNavigator(content.Tabs, content.Settings, content.Info);
    nav.Reset([]);
    Check(content.Tabs.Select(t => t.Id).SequenceEqual(["Home", "Controlli", "Audio", "Giochi"]), "Games tab after Audio, stored id Giochi");
    nav.ClickTab(3);
    Check(nav.Selected?.Kind == ItemKind.Grid && nav.SelectedTile?.Id == "steam:1", "Games tab opens on the first tile");

    // 3 columns: [1 2 3] [4 5]
    nav.Handle(OverlayCommand.Right); nav.Handle(OverlayCommand.Right);
    Check(nav.SelectedTile?.Id == "steam:3", "right moves along the row");
    nav.Handle(OverlayCommand.Right);
    Check(nav.SelectedTile?.Id == "epic:4", "right at the row end goes to the next row");
    nav.Handle(OverlayCommand.Up);
    Check(nav.SelectedTile?.Id == "steam:1", "up keeps the column");
    nav.Handle(OverlayCommand.Up);
    Check(nav.SelectedTile?.Id == "steam:1", "up at the top stays");
    nav.Handle(OverlayCommand.Right); nav.Handle(OverlayCommand.Right); nav.Handle(OverlayCommand.Down);
    Check(nav.SelectedTile?.Id == "epic:5", "down on a short last row clamps to its last tile");
    nav.Handle(OverlayCommand.RightRepeat);
    Check(nav.SelectedTile?.Id == "epic:5", "right past the last tile stays");
    nav.Handle(OverlayCommand.LeftRepeat);
    Check(nav.SelectedTile?.Id == "epic:4", "held left keeps moving");

    var launch = nav.Handle(OverlayCommand.A);
    Check(launch.Effect == NavEffect.HideAndRun, "A launches after hiding");
    launch.Action?.Invoke();
    Check(view.Launched.SequenceEqual(["epic:4"]), "A launches the selected tile");

    view.Set([G("steam:0", GameSource.Steam, "New"), .. view.Games]);
    nav.Refresh();
    Check(nav.SelectedTile?.Id == "epic:4", "a game added before the selection keeps the same tile selected");
    view.Set(view.Games.Where(g => g.Id != "epic:4").ToList());
    nav.Refresh();
    Check(nav.SelectedTile is { } neighbour && neighbour.Id != "epic:4", "removing the selected game selects a neighbour");

    // Y cycles the source filter.
    var footer = nav.Footer(pad: true);
    Check(footer.Any(h => h.Keys == "A" && h.Label == Loc.T("Overlay.Footer.Play"))
        && footer.Any(h => h.Keys == "Y" && h.Label == Loc.T("Overlay.Footer.Filter") && h.Command == OverlayCommand.Remove), "footer: A Play, Y Filter");
    nav.Handle(OverlayCommand.Remove).Action?.Invoke();
    nav.Refresh();
    Check(nav.Selected?.Label() == Loc.T("Games.Source.Steam") && nav.SelectedTile?.Source == GameSource.Steam, "Y filters to Steam");
    nav.Handle(OverlayCommand.Remove).Action?.Invoke();
    nav.Refresh();
    Check(nav.SelectedTile?.Source == GameSource.Epic, "Y filters to Epic");
    view.Set(view.Games.Where(g => g.Source != GameSource.Epic).ToList());
    nav.Refresh();
    Check(nav.Selected?.Label() == Loc.T("Overlay.Games.All"), "filtered source gone: back to All");

    view.Set([]);
    nav.Refresh();
    Check(content.Games().Any(i => i.Visible() && i.Label() == Loc.T("Overlay.Games.Empty")), "empty state shown");

    // Mouse.
    view.Set([G("steam:1", GameSource.Steam, "A"), G("steam:2", GameSource.Steam, "B")]);
    nav.Refresh();
    Check(nav.HoverTile(1) && nav.SelectedTile?.Id == "steam:2", "hover selects a tile");
    var click = nav.ClickTile(0);
    click.Action?.Invoke();
    Check(click.Effect == NavEffect.HideAndRun && view.Launched.Last() == "steam:1", "click launches that tile");

    // The grid keeps its tile across a visit to Settings and another tab.
    view.Set([G("steam:1", GameSource.Steam, "A"), G("steam:2", GameSource.Steam, "B"), G("epic:3", GameSource.Epic, "C")]);
    nav.Refresh();
    nav.HoverTile(2);
    nav.Handle(OverlayCommand.Settings);
    nav.Handle(OverlayCommand.B);
    nav.Refresh();
    Check(nav.SelectedTile?.Id == "epic:3", "back from Settings: same tile selected");
    nav.Handle(OverlayCommand.PrevTab);
    nav.Handle(OverlayCommand.NextTab);
    nav.Refresh();
    Check(nav.SelectedTile?.Id == "epic:3", "back from another tab: same tile selected");

    // Launch uses the game shown, even if the list changed before the next redraw.
    view.Set([G("steam:0", GameSource.Steam, "Z"), .. view.Games]);
    nav.Handle(OverlayCommand.A).Action?.Invoke();
    Check(view.Launched.Last() == "epic:3", "A launches the selected game by id, not by position");
    nav.Refresh();

    // A filter that drops the selected game starts on its first tile; one that keeps it keeps it.
    nav.Handle(OverlayCommand.Remove).Action?.Invoke(); // All -> Steam
    nav.Refresh();
    Check(nav.SelectedTile?.Id == "steam:0", "filter without the selected game: first tile");
    nav.Handle(OverlayCommand.Remove).Action?.Invoke(); // Steam -> Epic: its first tile
    nav.Handle(OverlayCommand.Remove).Action?.Invoke(); // Epic -> All
    nav.Refresh();
    Check(nav.SelectedTile?.Id == "epic:3", "filter that has the selected game keeps it");

    // A filter that fell back to All stays on All when its source comes back.
    nav.Handle(OverlayCommand.Remove).Action?.Invoke();
    nav.Handle(OverlayCommand.Remove).Action?.Invoke(); // Epic
    var withEpic = view.Games;
    view.Set(withEpic.Where(g => g.Source != GameSource.Epic).ToList());
    nav.Refresh();
    view.Set(withEpic);
    nav.Refresh();
    Check(nav.Selected?.Label() == Loc.T("Overlay.Games.All"), "a fallen-back filter does not come back by itself");

    // Before the first read: Loading, not "No games found".
    var loading = new FakeGames { Loading = true };
    var loadingContent = new OverlayContent(store, monitors, new MonitorSwitcherLocator(store), host, new FakeAudio(), null, null, loading);
    Check(loadingContent.Games().Any(i => i.Visible() && i.Label() == Loc.T("Overlay.Games.Loading"))
        && !loadingContent.Games().Any(i => i.Visible() && i.Label() == Loc.T("Overlay.Games.Empty")), "loading state before the first read");

    // Settings -> Games.
    var settings = content.Settings();
    var epicToggle = settings.First(i => i.Label() == Loc.T("Games.Source.Epic"));
    epicToggle.Toggle!();
    Check(store.Current.GamesSourcesHidden.Contains("epic") && view.Refreshes > 0, "hiding a source stores it and refreshes");
    var appsPage = settings.First(i => i.Label() == Loc.T("Settings.Games.Apps")).SubPage!();
    host.NextProgram = @"C:\Tools\quiver.exe";
    appsPage.First(i => i.Label() == Loc.T("Settings.Games.AddApp")).Action!();
    Check(store.Current.GamesManualApps.SequenceEqual([@"C:\Tools\quiver.exe"]), "Add app stores the exe");
    var entry = content.Settings().First(i => i.Label() == Loc.T("Settings.Games.Apps")).SubPage!().First(i => i.RemoveEntry != null);
    Check(entry.Action != null && entry.ActionLabel?.Invoke() == Loc.T("Settings.Games.RemoveApp"), "A on an app row removes it too, and says so");
    entry.RemoveEntry!();
    Check(store.Current.GamesManualApps.Count == 0, "Remove deletes the app");
}

// Current game.
{
    static GameEntry Game(string id, string? installDir) => new(id, GameSource.App, id, @"C:\x.exe", 0, null, null, InstallDir: installDir);
    var a = Game("a", @"C:\Games\Foo");
    var b = Game("b", @"C:\Games\Foobar");
    var launcher = Game("l", @"C:\Launcher");
    var inner = Game("i", @"C:\Launcher\games\Inner");
    var root = Game("r", @"D:\");
    Check(CurrentGame.Match(@"C:\Games\Foo\bin\foo.exe", [a, b])?.Id == "a", "exe under the folder matches");
    Check(CurrentGame.Match(@"c:\games\FOOBAR\x.exe", [a, b])?.Id == "b", "case-insensitive, no prefix-by-name match");
    Check(CurrentGame.Match(@"C:\Launcher\games\Inner\i.exe", [launcher, inner])?.Id == "i", "longest folder wins");
    Check(CurrentGame.Match(@"D:\anything.exe", [root]) is null, "a drive root never matches");
    Check(CurrentGame.Match(@"C:\Other\x.exe", [a]) is null, "no match");
    Check(CurrentGame.Match(@"C:\Games\Foo\x.exe", [Game("n", null), Game("e", "")]) is null, "missing folders are skipped");
    Check(CurrentGame.Match(@"C:\Games\Foo", [a]) is null, "the folder itself is not a file inside it");
    Check(CurrentGame.Match("", [a]) is null && CurrentGame.Match(@"C:\Games\Foo\x.exe", []) is null, "empty exe / no games");
    Check(CurrentGame.PlayTime(TimeSpan.FromSeconds(20)) == "0 min", "under a minute");
    Check(CurrentGame.PlayTime(TimeSpan.FromMinutes(72)) == "1 h 12 min", "hours and minutes");
    Check(CurrentGame.PlayTime(TimeSpan.FromMinutes(59)) == "59 min", "59 minutes stay in minutes");
    Check(CurrentGame.PlayTime(TimeSpan.FromMinutes(-5)) == "0 min", "negative time clamps to zero");
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
    /// <summary>What the name dialog returns: null accepts the suggestion.</summary>
    public string? NextName { get; set; }
    public string? NextProgram { get; set; }
    /// <summary>Title of the last name prompt.</summary>
    public string? LastTitle { get; private set; }
    public void AskName(string title, string suggested, Action<string> done)
    {
        LastTitle = title;
        Calls.Add("AskName:" + suggested);
        done(NextName ?? suggested);
    }
    public void ChooseProgram(Action<string> done)
    {
        Calls.Add("ChooseProgram");
        if (NextProgram is { } exe) done(exe);
    }
}

sealed class FakeMedia(List<string> log) : IMediaControl
{
    public NowPlaying? Current { get; set; }
    public event Action? Changed { add { } remove { } }
    public void Previous() => log.Add("prev");
    public void Next() => log.Add("next");
    public void PlayPause() => log.Add("playpause");
}

sealed class FakeAudio : IAudioView
{
    public List<string> Calls { get; } = [];
    public List<AudioDeviceRef> Outputs { get; } = [];
    public List<AudioDeviceRef> Inputs { get; } = [];
    public string? DefaultOutput { get; set; }
    public string? DefaultInput { get; set; }
    public double Volume { get; set; }
    public bool Muted { get; set; }
    public bool MicMuted { get; set; }
    public IReadOnlyList<AudioDeviceRef> Available(AudioFlow flow) => flow == AudioFlow.Output ? Outputs : Inputs;
    public AudioDeviceRef? Default(AudioFlow flow)
    {
        var (list, id) = flow == AudioFlow.Output ? (Outputs, DefaultOutput) : (Inputs, DefaultInput);
        return id is null ? null : list.Find(d => d.Id == id);
    }
    public void Cycle(AudioFlow flow, int direction) => Calls.Add($"Cycle:{flow}:{direction}");
    public void StepVolume(int direction) => Calls.Add($"StepVolume:{direction}");
    public void SetVolume(double level) => Calls.Add("SetVolume:" + level.ToString(CultureInfo.InvariantCulture));
    public void ToggleMute() => Calls.Add("ToggleMute");
    public void ToggleMicMute() => Calls.Add("ToggleMicMute");
    public List<AudioApp> AppList { get; } = [];
    public IReadOnlyList<AudioApp> Apps => AppList;
    public void ApplyMix() => Calls.Add("ApplyMix");
}

sealed class FakeRegistry : IRegistryView
{
    readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, List<string>> _subKeys = new(StringComparer.OrdinalIgnoreCase);
    static string K(Microsoft.Win32.RegistryHive hive, string key, bool wow64) => $"{hive}|{wow64}|{key}";
    public void Set(Microsoft.Win32.RegistryHive hive, string key, string value, string data, bool wow64 = false)
    {
        _values[K(hive, key, wow64) + "|" + value] = data;
        var slash = key.LastIndexOf((char)92);
        if (slash > 0)
        {
            var parent = K(hive, key[..slash], wow64);
            if (!_subKeys.TryGetValue(parent, out var list)) _subKeys[parent] = list = [];
            var child = key[(slash + 1)..];
            if (!list.Contains(child, StringComparer.OrdinalIgnoreCase)) list.Add(child);
        }
    }
    public string? GetString(Microsoft.Win32.RegistryHive hive, string key, string value, bool wow64 = false) =>
        _values.TryGetValue(K(hive, key, wow64) + "|" + value, out var data) ? data : null;
    public IReadOnlyList<string> SubKeys(Microsoft.Win32.RegistryHive hive, string key, bool wow64 = false) =>
        _subKeys.TryGetValue(K(hive, key, wow64), out var list) ? list : [];
}

sealed class FakeGames : IGameView
{
    public IReadOnlyList<GameEntry> Games { get; private set; } = [];
    public bool Loading { get; init; }
    public List<string> Launched { get; } = [];
    public int Refreshes { get; private set; }
    public event Action? Changed;
    public void Set(IReadOnlyList<GameEntry> games) { Games = games; Changed?.Invoke(); }
    public void Refresh() => Refreshes++;
    public void Launch(GameEntry game) => Launched.Add(game.Id);
}

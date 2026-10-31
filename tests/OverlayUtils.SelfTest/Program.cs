using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using OverlayUtils.Core;
using OverlayUtils.Core.Config;
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

try { Directory.Delete(temp, recursive: true); } catch { /* best effort */ }
Console.WriteLine(failures == 0 ? $"All {passed} checks passed." : $"{failures} failed, {passed} passed.");
return failures == 0 ? 0 : 1;

sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now;
    public override DateTimeOffset GetUtcNow() => Now;
}

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using OverlayUtils.Core;
using OverlayUtils.Core.Config;
using OverlayUtils.Core.Legacy;
using OverlayUtils.Core.Localization;
using OverlayUtils.Core.Monitors;
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

try { Directory.Delete(temp, recursive: true); } catch { /* best effort */ }
Console.WriteLine(failures == 0 ? $"All {passed} checks passed." : $"{failures} failed, {passed} passed.");
return failures == 0 ? 0 : 1;

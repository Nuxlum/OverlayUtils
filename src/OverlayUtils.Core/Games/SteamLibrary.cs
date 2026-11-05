using System.Globalization;
using Microsoft.Win32;
using OverlayUtils.Core.Diagnostics;

namespace OverlayUtils.Core.Games;

/// <summary>
/// Installed Steam games and the non-Steam shortcuts added to Steam, read from Steam's own files
/// (Python: LIBRERIA GIOCHI). The Steam folder comes from the registry; every launch goes through
/// steam://rungameid/.
/// </summary>
public static class SteamLibrary
{
    const string SteamKey = @"Software\Valve\Steam";
    const ulong ShortcutFlag = 0x02000000; // non-Steam gameid: (appid << 32) | this
    static readonly HashSet<long> ExcludedAppIds = [228980]; // Steamworks Common Redistributables
    static readonly string[] CapsuleNames = ["library_600x900.jpg", "library_capsule.jpg"];
    static readonly string[] HeaderNames = ["library_header.jpg", "header.jpg"];
    static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp"];

    /// <summary>The Steam folder from the registry, or null when Steam is not installed.</summary>
    public static string? Root(IRegistryView registry) =>
        registry.GetString(RegistryHive.CurrentUser, SteamKey, "SteamPath") is { Length: > 0 } root && Directory.Exists(root) ? root : null;

    public static IReadOnlyList<GameEntry> Read(IRegistryView registry)
    {
        if (Root(registry) is not { } root) return [];
        var games = new List<GameEntry>();
        try
        {
            foreach (var library in Libraries(root))
                games.AddRange(Installed(root, library));
        }
        catch (Exception e)
        {
            Log.Error("Reading Steam games", e);
        }
        try
        {
            games.AddRange(Shortcuts(root, ActiveUser(registry, root)));
        }
        catch (Exception e)
        {
            Log.Error("Reading non-Steam shortcuts", e);
        }
        return games;
    }

    static IEnumerable<string> Libraries(string root)
    {
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(root) };
        var file = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        if (File.Exists(file))
        {
            var folders = Vdf.ParseText(File.ReadAllText(file)).Get("libraryfolders");
            foreach (var (_, folder) in folders?.Children ?? [])
                if (folder.Value("path") is { Length: > 0 } path && Directory.Exists(path))
                    libraries.Add(Path.GetFullPath(path));
        }
        return libraries;
    }

    static IEnumerable<GameEntry> Installed(string root, string library)
    {
        var steamapps = Path.Combine(library, "steamapps");
        if (!Directory.Exists(steamapps)) yield break;
        foreach (var manifest in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
        {
            VdfNode? state;
            try { state = Vdf.ParseText(File.ReadAllText(manifest)).Get("AppState"); }
            catch (IOException) { continue; }
            if (state?.Value("appid") is not { } idText || !long.TryParse(idText, out var appid)) continue;
            var name = state.Value("name") ?? $"App {appid}";
            if (ExcludedAppIds.Contains(appid) || name.Contains("redistributable", StringComparison.OrdinalIgnoreCase)) continue;
            long.TryParse(state.Value("LastPlayed"), out var lastPlayed);
            yield return new GameEntry($"steam:{appid}", GameSource.Steam, name, $"steam://rungameid/{appid}",
                lastPlayed, Art(root, appid), null,
                InstallDir: state.Value("installdir") is { Length: > 0 } dir ? GamePaths.Full(Path.Combine(steamapps, "common", dir)) : null);
        }
    }

    static string? Art(string root, long appid)
    {
        var cache = Path.Combine(root, "appcache", "librarycache");
        var folder = Path.Combine(cache, appid.ToString(CultureInfo.InvariantCulture));
        var files = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .GroupBy(f => Path.GetFileName(f).ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.First())
            : [];
        foreach (var name in CapsuleNames.Concat(HeaderNames))
        {
            // current layout (a folder per game) or the old one ("<appid>_name")
            if (files.TryGetValue(name, out var found)) return found;
            var flat = Path.Combine(cache, $"{appid}_{name}");
            if (File.Exists(flat)) return flat;
        }
        return null;
    }

    static string? ActiveUser(IRegistryView registry, string root)
    {
        var user = registry.GetString(RegistryHive.CurrentUser, SteamKey + @"\ActiveProcess", "ActiveUser");
        if (!string.IsNullOrEmpty(user) && user != "0") return user;
        // Steam closed: the user whose configuration changed last
        var userdata = Path.Combine(root, "userdata");
        if (!Directory.Exists(userdata)) return null;
        return Directory.EnumerateDirectories(userdata)
            .Select(d => (Dir: d, Config: Path.Combine(d, "config", "localconfig.vdf")))
            .Where(x => File.Exists(x.Config))
            .OrderByDescending(x => File.GetLastWriteTimeUtc(x.Config))
            .Select(x => Path.GetFileName(x.Dir))
            .FirstOrDefault();
    }

    static IEnumerable<GameEntry> Shortcuts(string root, string? user)
    {
        if (user is null) return [];
        var config = Path.Combine(root, "userdata", user, "config");
        var file = Path.Combine(config, "shortcuts.vdf");
        if (!File.Exists(file)) return [];
        var entries = Vdf.ParseBinary(File.ReadAllBytes(file)).Get("shortcuts");
        var grid = Path.Combine(config, "grid");
        var games = new List<GameEntry>();
        foreach (var (_, entry) in entries?.Children ?? [])
        {
            if (!int.TryParse(entry.Value("appid"), out var raw)) continue;
            var appid = unchecked((uint)raw);
            var gameId = ((ulong)appid << 32) | ShortcutFlag;
            int.TryParse(entry.Value("lastplaytime"), out var lastPlayed);
            var name = entry.Value("appname") is { Length: > 0 } n ? n : $"Shortcut {appid}";
            games.Add(new GameEntry($"steam:{gameId}", GameSource.Steam, name, $"steam://rungameid/{gameId}",
                Math.Max(0, lastPlayed), ShortcutArt(grid, appid, entry.Value("icon")), ShortcutExe(entry.Value("exe")),
                InstallDir: GamePaths.Full(Path.GetDirectoryName(entry.Value("exe")?.Trim().Trim('"')))));
        }
        return games;
    }

    /// <summary>The shortcut's target exe (stored quoted), for its icon when there is no grid art.</summary>
    static string? ShortcutExe(string? exe)
    {
        if (string.IsNullOrWhiteSpace(exe)) return null;
        var path = exe.Trim().Trim('"');
        return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path) ? path : null;
    }

    static string? ShortcutArt(string grid, uint appid, string? icon)
    {
        foreach (var stem in new[] { $"{appid}p", $"{appid}" })
            foreach (var ext in ImageExtensions)
            {
                var path = Path.Combine(grid, stem + ext);
                if (File.Exists(path)) return path;
            }
        return icon is { Length: > 0 } && ImageExtensions.Contains(Path.GetExtension(icon).ToLowerInvariant()) && File.Exists(icon)
            ? icon : null;
    }
}

using Microsoft.Win32;

namespace OverlayUtils.Core.Games;

/// <summary>
/// Common emulators found through their installers' uninstall entries; each is one tile that opens the
/// emulator itself (no ROM scanning). Portable emulators are added by hand as apps.
/// </summary>
public static class EmulatorLibrary
{
    const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    const string UninstallWow = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    // Display-name word -> exe names to look for in the install folder.
    static readonly (string Name, string[] Exes)[] Known =
    [
        ("RetroArch", ["retroarch.exe"]),
        ("Dolphin", ["Dolphin.exe"]),
        ("PCSX2", ["pcsx2-qt.exe", "pcsx2.exe"]),
        ("DuckStation", ["duckstation-qt-x64-ReleaseLTCG.exe", "duckstation-qt.exe"]),
        ("PPSSPP", ["PPSSPPWindows64.exe", "PPSSPPWindows.exe"]),
        ("RPCS3", ["rpcs3.exe"]),
        ("Cemu", ["Cemu.exe"]),
        ("xemu", ["xemu.exe"]),
        ("Ryujinx", ["Ryujinx.exe"]),
        ("Citron", ["citron.exe"]),
        ("Sudachi", ["sudachi.exe"]),
        ("Xenia", ["xenia_canary.exe", "xenia.exe"]),
        ("melonDS", ["melonDS.exe"]),
        ("mGBA", ["mGBA.exe"]),
        ("Flycast", ["flycast.exe"]),
        ("Redream", ["redream.exe"]),
        ("Vita3K", ["Vita3K.exe"]),
    ];

    public static IReadOnlyList<GameEntry> Read(IRegistryView registry)
    {
        var found = new Dictionary<string, GameEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, root) in new[]
                 {
                     (RegistryHive.LocalMachine, Uninstall), (RegistryHive.LocalMachine, UninstallWow), (RegistryHive.CurrentUser, Uninstall),
                 })
        {
            foreach (var sub in registry.SubKeys(hive, root))
            {
                var key = root + @"\" + sub;
                var name = registry.GetString(hive, key, "DisplayName");
                if (string.IsNullOrEmpty(name)) continue;
                var known = Known.FirstOrDefault(k => ContainsWord(name, k.Name));
                if (known.Name is null) continue;
                // The known exe in the install folder first: DisplayIcon is often the uninstaller.
                var exe = FromFolder(registry.GetString(hive, key, "InstallLocation"), known.Exes)
                          ?? FromIcon(registry.GetString(hive, key, "DisplayIcon"), known.Exes);
                if (exe is null) continue;
                var id = "emulator:" + exe.ToLowerInvariant();
                found.TryAdd(id, new GameEntry(id, GameSource.Emulator, name, exe, 0, null, exe, Path.GetDirectoryName(exe)));
            }
        }
        return found.Values.ToList();
    }

    static bool ContainsWord(string displayName, string word) =>
        displayName.Split([' ', '-', '_', '(', ')', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Any(part => string.Equals(part, word, StringComparison.OrdinalIgnoreCase));

    /// <summary>DisplayIcon is often `"C:\path\app.exe",0`; kept only if it is an existing exe of the emulator.</summary>
    static string? FromIcon(string? icon, string[] exes)
    {
        if (string.IsNullOrWhiteSpace(icon)) return null;
        var path = icon.Trim();
        var comma = path.LastIndexOf(',');
        if (comma > 0 && int.TryParse(path[(comma + 1)..].Trim(), out _)) path = path[..comma];
        path = path.Trim().Trim('"');
        var name = Path.GetFileName(path);
        return exes.Any(e => string.Equals(e, name, StringComparison.OrdinalIgnoreCase)) && File.Exists(path) ? path : null;
    }

    static string? FromFolder(string? folder, string[] exes)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        folder = folder.Trim().Trim('"');
        return exes.Select(e => Path.Combine(folder, e)).FirstOrDefault(File.Exists);
    }
}

using System.Text.Json;
using Microsoft.Win32;
using OverlayUtils.Core.Diagnostics;

namespace OverlayUtils.Core.Games;

/// <summary>
/// Installed Epic Games titles, from the launcher's install manifests. The manifest folder comes from the
/// launcher's registry AppDataPath; DLC, applications and broken manifests are skipped.
/// </summary>
public static class EpicLibrary
{
    const string LauncherKey = @"SOFTWARE\WOW6432Node\Epic Games\EpicGamesLauncher";

    public static IReadOnlyList<GameEntry> Read(IRegistryView registry)
    {
        var data = registry.GetString(RegistryHive.LocalMachine, LauncherKey, "AppDataPath");
        if (string.IsNullOrEmpty(data)) return [];
        var folder = Path.Combine(data, "Manifests");
        if (!Directory.Exists(folder)) return [];
        var games = new List<GameEntry>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.item"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (ToGame(doc.RootElement) is { } game) games.Add(game);
            }
            catch (Exception e) when (e is JsonException or IOException or InvalidOperationException)
            {
                Log.Error($"Reading Epic manifest {Path.GetFileName(file)}", e);
            }
        }
        return games;
    }

    static GameEntry? ToGame(JsonElement m)
    {
        var app = Text(m, "AppName");
        if (app is null) return null;
        var main = Text(m, "MainGameAppName");
        if (main is not null && !string.Equals(main, app, StringComparison.OrdinalIgnoreCase)) return null; // DLC
        if (m.TryGetProperty("bIsIncompleteInstall", out var incomplete) && incomplete.ValueKind == JsonValueKind.True) return null;
        if (!m.TryGetProperty("AppCategories", out var categories) || categories.ValueKind != JsonValueKind.Array
            || !categories.EnumerateArray().Any(c => string.Equals(c.GetString(), "games", StringComparison.OrdinalIgnoreCase)))
            return null;
        var title = Text(m, "DisplayName") ?? app;
        var exe = Text(m, "InstallLocation") is { } dir && Text(m, "LaunchExecutable") is { } launch ? Path.Combine(dir, launch) : null;
        return new GameEntry($"epic:{app}", GameSource.Epic, title,
            $"com.epicgames.launcher://apps/{Uri.EscapeDataString(app)}?action=launch&silent=true", 0, null, exe);
    }

    static string? Text(JsonElement m, string name) =>
        m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
}

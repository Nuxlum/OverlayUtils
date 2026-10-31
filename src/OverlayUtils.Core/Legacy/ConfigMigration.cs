using OverlayUtils.Core.Diagnostics;

namespace OverlayUtils.Core.Legacy;

/// <summary>
/// First run after the rename: copy %APPDATA%\AppUtili (settings, Spotify and
/// Discord tokens, caches) to %APPDATA%\OverlayUtils so nothing has to be set
/// up again. The old folder is left in place; nothing reads it afterwards.
/// </summary>
public static class ConfigMigration
{
    public const string MarkerFile = "migrated-from-AppUtili.txt";

    /// <summary>Old file name -> new file name; null = do not copy.</summary>
    private static readonly Dictionary<string, string?> Renames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["errori.log"] = null, // old log, not useful
    };

    /// <summary>Returns true if settings were migrated now.</summary>
    public static bool MigrateIfNeeded(string legacyDir, string newDir)
    {
        try
        {
            if (!Directory.Exists(legacyDir)) return false;
            if (File.Exists(Path.Combine(newDir, "config.json"))) return false;

            CopyTree(legacyDir, newDir);
            File.WriteAllText(Path.Combine(newDir, MarkerFile),
                $"Settings copied from {legacyDir} on {DateTime.Now:yyyy-MM-dd HH:mm}.{Environment.NewLine}" +
                $"The old folder can be deleted once everything works.{Environment.NewLine}");
            Log.Info($"Migrated settings from {legacyDir}");
            return true;
        }
        catch (Exception e)
        {
            Log.Error("Migrating AppUtili settings", e);
            return false;
        }
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var name = Path.GetFileName(file);
            if (Renames.TryGetValue(name, out var renamed))
            {
                if (renamed is null) continue;
                name = renamed;
            }
            var destination = Path.Combine(target, name);
            if (!File.Exists(destination)) File.Copy(file, destination);
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyTree(dir, Path.Combine(target, Path.GetFileName(dir)));
    }
}

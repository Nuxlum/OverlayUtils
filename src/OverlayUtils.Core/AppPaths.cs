namespace OverlayUtils.Core;

/// <summary>Well-known names and folders. Everything user-specific lives under %APPDATA%\OverlayUtils.</summary>
public static class AppPaths
{
    public const string AppName = "OverlayUtils";
    public const string StandardExeName = "OverlayUtils.exe";
    public const string UIAccessExeName = "OverlayUtils.UIAccess.exe";

    /// <summary>Name of the product before the rename; used only for migration and cleanup.</summary>
    public const string LegacyAppName = "AppUtili";

    private static string? _configDirOverride;

    /// <summary>For tests: redirect the config folder.</summary>
    internal static void OverrideConfigDir(string? path) => _configDirOverride = path;

    public static string RoamingAppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public static string ConfigDir => _configDirOverride ?? Path.Combine(RoamingAppData, AppName);
    public static string LegacyConfigDir => Path.Combine(RoamingAppData, LegacyAppName);

    public static string ConfigFile => Path.Combine(ConfigDir, "config.json");
    public static string ErrorLog => Path.Combine(ConfigDir, "errors.log");

    /// <summary>Folder of the running exe (both launchers sit side by side).</summary>
    public static string AppDir => AppContext.BaseDirectory;

    /// <summary>Protected install folder required by UIAccess.</summary>
    public static string FullModeInstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);

    /// <summary>Per-user install folder used when leaving full mode.</summary>
    public static string StandardInstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

    public static string LegacyInstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), LegacyAppName);

    /// <summary>MonitorSwitcher (monitorprofileswitcher.com) saves its profiles here.</summary>
    public static string MonitorProfilesDir => Path.Combine(RoamingAppData, "MonitorSwitcher", "Profiles");

    public static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);
}

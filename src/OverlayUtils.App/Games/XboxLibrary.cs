using System.IO;
using System.Xml.Linq;
using OverlayUtils.Core.Diagnostics;
using OverlayUtils.Core.Games;
using Windows.Management.Deployment;

namespace OverlayUtils.App.Games;

/// <summary>
/// PC Game Pass / Microsoft Store games: the current user's packages whose install folder has a
/// MicrosoftGame.config (the GDK game manifest), launched through their AUMID. Read only; nothing is installed or
/// changed.
/// </summary>
internal static class XboxLibrary
{
    const string GameConfig = "MicrosoftGame.config";

    // Packages whose failure was logged already: a broken or staged package fails on every read.
    static readonly HashSet<string> Logged = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<GameEntry> Read()
    {
        var games = new List<GameEntry>();
        foreach (var package in new PackageManager().FindPackagesForUser(""))
        {
            try
            {
                if (package.IsFramework || package.IsResourcePackage) continue;
                // A plain path (no StorageFolder): this runs for every package on every read.
                var folder = package.InstalledPath;
                if (string.IsNullOrEmpty(folder)) continue;
                var config = Path.Combine(folder, GameConfig);
                if (!File.Exists(config)) continue;

                var entry = package.GetAppListEntriesAsync().AsTask().GetAwaiter().GetResult().FirstOrDefault();
                if (entry is null) continue; // no Start menu entry: nothing to launch
                var (configTitle, logo) = ReadConfig(config, folder);
                if (logo is null && package.Logo is { IsFile: true } storeLogo && File.Exists(storeLogo.LocalPath))
                    logo = storeLogo.LocalPath; // the package's store logo when the config names none that exists
                var title = entry.DisplayInfo.DisplayName is { Length: > 0 } name ? name
                    : configTitle ?? package.DisplayName;
                games.Add(new GameEntry("xbox:" + package.Id.FamilyName.ToLowerInvariant(), GameSource.Xbox, title,
                    @"shell:AppsFolder\" + entry.AppUserModelId, 0, null, logo));
            }
            catch (Exception e)
            {
                // One broken package must not hide the others; logged once per session.
                string name;
                try { name = package.Id.FullName; }
                catch (Exception) { name = "?"; }
                bool first;
                lock (Logged) first = Logged.Add(name);
                if (first) Log.Error($"Xbox library package {name}", e);
            }
        }
        return games;
    }

    /// <summary>DefaultDisplayName (when it is not a resource reference) and the largest square logo that exists.</summary>
    static (string? Title, string? Logo) ReadConfig(string path, string folder)
    {
        try
        {
            var root = XDocument.Load(path).Root;
            if (root is null) return (null, null);
            var visuals = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "ShellVisuals");
            var title = visuals?.Attribute("DefaultDisplayName")?.Value;
            if (title is { } t && (t.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) || t.Length == 0)) title = null;
            string? logo = null;
            foreach (var attribute in new[] { "Square480x480Logo", "Square150x150Logo", "StoreLogo", "Square44x44Logo" })
            {
                if (visuals?.Attribute(attribute)?.Value is not { Length: > 0 } relative) continue;
                var candidate = Path.Combine(folder, relative);
                if (File.Exists(candidate)) { logo = candidate; break; }
            }
            return (title, logo);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException or ArgumentException)
        {
            return (null, null);
        }
    }
}

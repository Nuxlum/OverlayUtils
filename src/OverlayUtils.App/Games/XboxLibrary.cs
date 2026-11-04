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

    public static IReadOnlyList<GameEntry> Read()
    {
        var games = new List<GameEntry>();
        foreach (var package in new PackageManager().FindPackagesForUser(""))
        {
            try
            {
                if (package.IsFramework || package.IsResourcePackage) continue;
                string folder;
                try { folder = package.InstalledLocation.Path; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or FileNotFoundException) { continue; }
                var config = Path.Combine(folder, GameConfig);
                if (!File.Exists(config)) continue;

                var entry = package.GetAppListEntriesAsync().AsTask().GetAwaiter().GetResult().FirstOrDefault();
                if (entry is null) continue; // no Start menu entry: nothing to launch
                var (configTitle, logo) = ReadConfig(config, folder);
                var title = entry.DisplayInfo.DisplayName is { Length: > 0 } name ? name
                    : configTitle ?? package.DisplayName;
                games.Add(new GameEntry("xbox:" + package.Id.FamilyName.ToLowerInvariant(), GameSource.Xbox, title,
                    @"shell:AppsFolder\" + entry.AppUserModelId, 0, null, logo));
            }
            catch (Exception e)
            {
                Log.Error("Xbox library package", e); // one broken package must not hide the others
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

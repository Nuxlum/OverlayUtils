using System.Diagnostics;

namespace OverlayUtils.Core.Games;

/// <summary>Apps added by hand in Settings → Games (launchers we can't read, portable emulators, anything else).</summary>
public static class ManualApps
{
    public static IReadOnlyList<GameEntry> Read(IReadOnlyList<string> paths)
    {
        var games = new List<GameEntry>();
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            var id = "app:" + path.ToLowerInvariant();
            if (games.Any(g => g.Id == id)) continue;
            games.Add(new GameEntry(id, GameSource.App, TitleOf(path), path, 0, null, path, Path.GetDirectoryName(path)));
        }
        return games;
    }

    /// <summary>The exe's file description, else its file name without the extension.</summary>
    public static string TitleOf(string path)
    {
        try
        {
            if (FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } description) return description.Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        return Path.GetFileNameWithoutExtension(path);
    }
}

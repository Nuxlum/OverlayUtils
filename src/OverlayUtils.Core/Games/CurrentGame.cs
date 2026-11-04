using OverlayUtils.Core.Localization;

namespace OverlayUtils.Core.Games;

/// <summary>Finds which library game a running program belongs to, and formats how long it has run.</summary>
public static class CurrentGame
{
    /// <summary>
    /// The game whose install folder contains <paramref name="exePath"/> (the longest folder wins), or null.
    /// Folders are compared on the same normal form with a trailing separator, so "Foo" never matches "Foobar";
    /// missing folders and drive roots never match: nothing shown is better than a wrong title.
    /// </summary>
    public static GameEntry? Match(string exePath, IEnumerable<GameEntry> games)
    {
        var exe = GamePaths.Full(exePath);
        if (exe is null) return null;
        GameEntry? best = null;
        var bestLength = 0;
        foreach (var game in games)
        {
            var dir = GamePaths.Full(game.InstallDir);
            if (dir is null || dir.Length <= bestLength) continue;
            if (!exe.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            best = game;
            bestLength = dir.Length;
        }
        return best;
    }

    /// <summary>"12 min" under an hour, "1 h 12 min" from then on.</summary>
    public static string PlayTime(TimeSpan running)
    {
        var minutes = (int)Math.Max(0, running.TotalMinutes);
        return minutes < 60
            ? Loc.T("Overlay.Playing.Minutes", minutes)
            : Loc.T("Overlay.Playing.Hours", minutes / 60, minutes % 60);
    }
}

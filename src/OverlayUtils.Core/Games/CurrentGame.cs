using OverlayUtils.Core.Localization;

namespace OverlayUtils.Core.Games;

/// <summary>Finds which library game a running program belongs to, and formats how long it has run.</summary>
public static class CurrentGame
{
    static readonly Lazy<ExcludedFolders> ThisPc = new(() => ExcludedFolders.ForThisPc(SteamLibrary.Root(new WindowsRegistryView())));

    /// <summary><see cref="Match(string, IEnumerable{GameEntry}, ExcludedFolders)"/> with this PC's system and Steam folders excluded.</summary>
    public static GameEntry? Match(string exePath, IEnumerable<GameEntry> games) => Match(exePath, games, ThisPc.Value);

    /// <summary>
    /// The game whose install folder contains <paramref name="exePath"/> (the longest folder wins), or null.
    /// Folders are compared on the same normal form with a trailing separator, so "Foo" never matches "Foobar";
    /// missing folders, drive roots and <paramref name="excluded"/> folders never match (a non-Steam shortcut to
    /// explorer.exe has C:\Windows as its folder): nothing shown is better than a wrong title.
    /// </summary>
    public static GameEntry? Match(string exePath, IEnumerable<GameEntry> games, ExcludedFolders excluded)
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
            if (excluded.Excludes(dir)) continue;
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

/// <summary>
/// Folders that are never a game's install folder: <see cref="Trees"/> with everything under them (Windows), and
/// <see cref="Exact"/> folders themselves only (Program Files roots, the Steam root and its library folder; a game
/// inside them still matches). Any "steamapps\common" folder, of any Steam library, is excluded too.
/// </summary>
public sealed record ExcludedFolders(IReadOnlyList<string> Trees, IReadOnlyList<string> Exact)
{
    /// <summary>The Windows folder, the Program Files roots and, when known, the Steam root and its common folder.</summary>
    public static ExcludedFolders ForThisPc(string? steamRoot)
    {
        var exact = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };
        if (!string.IsNullOrWhiteSpace(steamRoot))
        {
            exact.Add(steamRoot);
            exact.Add(Path.Combine(steamRoot, "steamapps", "common"));
        }
        return new([Environment.GetFolderPath(Environment.SpecialFolder.Windows)], exact);
    }

    /// <summary>True when <paramref name="dir"/> (a <see cref="GamePaths.Full"/> form) is one of these folders.</summary>
    internal bool Excludes(string dir)
    {
        foreach (var tree in Trees)
            if (GamePaths.Full(tree) is { } t
                && (dir.Equals(t, StringComparison.OrdinalIgnoreCase)
                    || dir.StartsWith(t + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                return true;
        foreach (var folder in Exact)
            if (GamePaths.Full(folder) is { } f && dir.Equals(f, StringComparison.OrdinalIgnoreCase))
                return true;
        var sep = Path.DirectorySeparatorChar;
        return dir.EndsWith($"{sep}steamapps{sep}common", StringComparison.OrdinalIgnoreCase);
    }
}

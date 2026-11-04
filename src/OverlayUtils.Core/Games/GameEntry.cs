namespace OverlayUtils.Core.Games;

public enum GameSource { Steam, Epic, Gog, Xbox, Emulator, App }

/// <summary>
/// One tile of the Games tab. <see cref="Id"/> is "source:launcher id" and stays the same across refreshes,
/// so the selection and the launch never jump to another game.
/// </summary>
public sealed record GameEntry(
    string Id,
    GameSource Source,
    string Title,
    string LaunchTarget,
    long LastPlayed,
    string? ArtPath,
    string? IconPath,
    string? WorkingDir = null,
    string? Arguments = null, // for an exe target (GOG launchParam)
    string? InstallDir = null); // the game's install folder, for matching the running game

static class GamePaths
{
    /// <summary>The full form of a folder path, or null when it is empty or not a valid path.</summary>
    public static string? Full(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path.Trim()); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}

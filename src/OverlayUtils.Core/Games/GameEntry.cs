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
    string? Arguments = null); // for an exe target (GOG launchParam)

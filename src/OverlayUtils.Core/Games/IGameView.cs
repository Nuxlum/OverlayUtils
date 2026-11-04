namespace OverlayUtils.Core.Games;

/// <summary>The game library as the overlay sees it; the App's GameService implements it, the self-test fakes it.</summary>
public interface IGameView
{
    /// <summary>Merged, sorted games of the visible sources (the last list read).</summary>
    IReadOnlyList<GameEntry> Games { get; }

    /// <summary>The first read has not finished yet (the tab says Loading, not "No games found").</summary>
    bool Loading { get; }

    /// <summary>The list changed (any thread).</summary>
    event Action? Changed;

    /// <summary>Re-reads the library in the background.</summary>
    void Refresh();

    /// <summary>Starts a game (called after the overlay has hidden).</summary>
    void Launch(GameEntry game);
}

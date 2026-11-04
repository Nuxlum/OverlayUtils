namespace OverlayUtils.Core.Games;

/// <summary>Merging, sorting and filtering of the readers' results for the Games tab.</summary>
public static class GameLibrary
{
    /// <summary>All sources' games without hidden sources and duplicate ids (first one wins).</summary>
    public static IReadOnlyList<GameEntry> Merge(IEnumerable<IReadOnlyList<GameEntry>> sources, IReadOnlyCollection<GameSource> hidden)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var games = new List<GameEntry>();
        foreach (var source in sources)
            foreach (var game in source)
                if (!hidden.Contains(game.Source) && seen.Add(game.Id))
                    games.Add(game);
        return games;
    }

    /// <summary>Most recently played first (only Steam reports it), then title A-Z ignoring case.</summary>
    public static IReadOnlyList<GameEntry> Sort(IEnumerable<GameEntry> games) =>
        games.OrderByDescending(g => g.LastPlayed)
            .ThenBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(g => g.Id, StringComparer.Ordinal)
            .ToList();

    public static IReadOnlyList<GameEntry> Filter(IReadOnlyList<GameEntry> games, GameSource? filter) =>
        filter is { } source ? games.Where(g => g.Source == source).ToList() : games;

    /// <summary>All (null) followed by the sources that have at least one game, in enum order.</summary>
    public static IReadOnlyList<GameSource?> FilterCycle(IReadOnlyList<GameEntry> games)
    {
        var present = games.Select(g => g.Source).ToHashSet();
        var cycle = new List<GameSource?> { null };
        cycle.AddRange(Enum.GetValues<GameSource>().Where(present.Contains).Select(s => (GameSource?)s));
        return cycle;
    }

    /// <summary>The filter after <paramref name="current"/>; a filter whose source has no games any more goes back to All.</summary>
    public static GameSource? NextFilter(GameSource? current, IReadOnlyList<GameEntry> games)
    {
        var cycle = FilterCycle(games).ToList();
        var index = cycle.IndexOf(current);
        return index < 0 ? null : cycle[(index + 1) % cycle.Count];
    }

    /// <summary>The filter to show: <paramref name="current"/> while its source still has games, else All.</summary>
    public static GameSource? Valid(GameSource? current, IReadOnlyList<GameEntry> games) =>
        current is { } s && games.Any(g => g.Source == s) ? current : null;
}

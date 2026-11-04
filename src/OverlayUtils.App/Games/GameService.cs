using System.Diagnostics;
using System.IO;
using OverlayUtils.Core.Config;
using OverlayUtils.Core.Diagnostics;
using OverlayUtils.Core.Games;
using OverlayUtils.Core.Localization;

namespace OverlayUtils.App.Games;

/// <summary>
/// The Games tab's library: every source is read on a worker (at start, when the overlay opens if the last read is
/// older than <see cref="StaleAfter"/>, and at once when Settings → Games changes), the last merged list is kept,
/// <see cref="Changed"/> fires when it differs. A source that fails is logged and counted as empty. Reading never
/// writes anything; launching goes through the shell.
/// </summary>
internal sealed class GameService(ConfigStore config, Action<string> notify) : IGameView
{
    /// <summary>Opening the overlay again within this time does not read the launchers again.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);

    readonly IRegistryView _registry = new WindowsRegistryView();
    readonly object _lock = new();
    IReadOnlyList<GameEntry> _games = [];
    volatile bool _loaded;
    bool _reading;
    bool _again;
    long _lastRead; // Stopwatch timestamp of the last finished read, 0 = never

    public IReadOnlyList<GameEntry> Games => Volatile.Read(ref _games);

    public bool Loading => !_loaded;

    public event Action? Changed;

    public void Refresh()
    {
        lock (_lock)
        {
            if (_reading)
            {
                _again = true; // a read is running: one more after it, with the newest settings
                return;
            }
            _reading = true;
        }
        _ = Task.Run(ReadLoop);
    }

    /// <summary>Reads again only if the last read is older than <see cref="StaleAfter"/> (overlay opened).</summary>
    public void RefreshIfStale()
    {
        var last = Interlocked.Read(ref _lastRead);
        if (last != 0 && Stopwatch.GetElapsedTime(last) < StaleAfter) return;
        Refresh();
    }

    void ReadLoop()
    {
        while (true)
        {
            try { ReadOnce(); }
            catch (Exception e) { Log.Error("Games read", e); }
            lock (_lock)
            {
                if (!_again)
                {
                    _reading = false;
                    return;
                }
                _again = false;
            }
        }
    }

    void ReadOnce()
    {
        var current = config.Current;
        var hidden = current.GamesSourcesHidden
            .Select(s => Enum.TryParse<GameSource>(s, ignoreCase: true, out var source) ? source : (GameSource?)null)
            .OfType<GameSource>()
            .ToHashSet();
        var manual = current.GamesManualApps.ToList();
        var sources = new List<IReadOnlyList<GameEntry>>();
        void Add(GameSource source, Func<IReadOnlyList<GameEntry>> read)
        {
            if (hidden.Contains(source)) return; // not even read
            try { sources.Add(read()); }
            catch (Exception e) { Log.Error($"Games read {source}", e); }
        }
        Add(GameSource.Steam, () => SteamLibrary.Read(_registry));
        Add(GameSource.Epic, () => EpicLibrary.Read(_registry));
        Add(GameSource.Gog, () => GogLibrary.Read(_registry));
        Add(GameSource.Xbox, XboxLibrary.Read);
        Add(GameSource.Emulator, () => EmulatorLibrary.Read(_registry));
        Add(GameSource.App, () => ManualApps.Read(manual));

        var games = GameLibrary.Sort(GameLibrary.Merge(sources, hidden));
        Interlocked.Exchange(ref _lastRead, Stopwatch.GetTimestamp());
        var first = !_loaded;
        _loaded = true;
        if (!first && games.SequenceEqual(Games)) return;
        Volatile.Write(ref _games, games);
        try { Changed?.Invoke(); } // also after the first read, so "Looking for games…" goes away
        catch (Exception e) { Log.Error("Games changed", e); }
    }

    /// <summary>Opens the game's launch target through the shell (URI, AUMID or exe with its folder and arguments).</summary>
    public void Launch(GameEntry game) => _ = Task.Run(() =>
    {
        try
        {
            var start = new ProcessStartInfo(game.LaunchTarget) { UseShellExecute = true };
            if (game.WorkingDir is { Length: > 0 } folder && Directory.Exists(folder)) start.WorkingDirectory = folder;
            if (game.Arguments is { Length: > 0 } arguments) start.Arguments = arguments;
            Process.Start(start)?.Dispose();
        }
        catch (Exception e)
        {
            Log.Error($"Game launch {game.Id}", e);
            try { notify(Loc.T("Notify.GameLaunchFailed", game.Title)); }
            catch (Exception n) { Log.Error("Game launch notify", n); }
        }
    });
}

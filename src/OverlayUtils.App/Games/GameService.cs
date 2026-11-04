using System.Diagnostics;
using System.IO;
using OverlayUtils.Core.Config;
using OverlayUtils.Core.Diagnostics;
using OverlayUtils.Core.Games;

namespace OverlayUtils.App.Games;

/// <summary>
/// The Games tab's library: every source is read on a worker (when the overlay opens and when Settings → Games
/// changes), the last merged list is kept, <see cref="Changed"/> fires when it differs. A source that fails is
/// logged and counted as empty. Reading never writes anything; launching goes through the shell.
/// </summary>
internal sealed class GameService(ConfigStore config) : IGameView
{
    readonly IRegistryView _registry = new WindowsRegistryView();
    readonly object _lock = new();
    IReadOnlyList<GameEntry> _games = [];
    bool _reading;
    bool _again;

    public IReadOnlyList<GameEntry> Games => Volatile.Read(ref _games);

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
        if (games.SequenceEqual(Games)) return;
        Volatile.Write(ref _games, games);
        try { Changed?.Invoke(); }
        catch (Exception e) { Log.Error("Games changed", e); }
    }

    /// <summary>Opens the game's launch target through the shell (URI, AUMID or exe with its working folder).</summary>
    public void Launch(GameEntry game) => _ = Task.Run(() =>
    {
        try
        {
            var start = new ProcessStartInfo(game.LaunchTarget) { UseShellExecute = true };
            if (game.WorkingDir is { Length: > 0 } folder && Directory.Exists(folder)) start.WorkingDirectory = folder;
            Process.Start(start)?.Dispose();
        }
        catch (Exception e)
        {
            Log.Error($"Game launch {game.Id}", e);
        }
    });
}

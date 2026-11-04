using Microsoft.Win32;

namespace OverlayUtils.Core.Games;

/// <summary>Installed GOG games, from the entries GOG writes to the registry (entries without an exe, e.g. DLC, are skipped).</summary>
public static class GogLibrary
{
    const string GamesKey = @"SOFTWARE\WOW6432Node\GOG.com\Games";

    public static IReadOnlyList<GameEntry> Read(IRegistryView registry)
    {
        var games = new List<GameEntry>();
        foreach (var id in registry.SubKeys(RegistryHive.LocalMachine, GamesKey))
        {
            var key = GamesKey + @"\" + id;
            var exe = registry.GetString(RegistryHive.LocalMachine, key, "exe");
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) continue;
            var title = registry.GetString(RegistryHive.LocalMachine, key, "gameName") is { Length: > 0 } name ? name : Path.GetFileNameWithoutExtension(exe);
            var gameId = registry.GetString(RegistryHive.LocalMachine, key, "gameID") is { Length: > 0 } g ? g : id;
            var workingDir = registry.GetString(RegistryHive.LocalMachine, key, "workingDir") is { Length: > 0 } w ? w : Path.GetDirectoryName(exe);
            // DOSBox / ScummVM classics: exe is the emulator and launchParam has the -conf / game arguments.
            var arguments = registry.GetString(RegistryHive.LocalMachine, key, "launchParam") is { Length: > 0 } a ? a.Trim() : null;
            games.Add(new GameEntry($"gog:{gameId}", GameSource.Gog, title, exe, 0, null, exe, workingDir, arguments,
                GamePaths.Full(registry.GetString(RegistryHive.LocalMachine, key, "path") is { Length: > 0 } p ? p : Path.GetDirectoryName(exe))));
        }
        return games;
    }
}

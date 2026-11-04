using System.Text.Json.Serialization;

namespace OverlayUtils.Core.Audio;

/// <summary>A mixer channel: stable id, display name and a glyph key.</summary>
public sealed record AudioChannel(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("icon")] string Icon);

public sealed record MixLevel(
    [property: JsonPropertyName("volume")] double Volume,
    [property: JsonPropertyName("muted")] bool Muted);

/// <summary>Channel and app levels of one output device.</summary>
public sealed record DeviceMix(
    [property: JsonPropertyName("channels")] Dictionary<string, MixLevel> Channels,
    [property: JsonPropertyName("apps")] Dictionary<string, MixLevel> Apps);

/// <summary>Channel volumes and mutes only; applies to the current device.</summary>
public sealed record MixPreset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("channels")] Dictionary<string, MixLevel> Channels);

/// <summary>Pure mixing decisions: channel assignment, effective volume, per-device mixes, presets.</summary>
public static class MixRules
{
    public const string Game = "game", Chat = "chat", Media = "media", Other = "other";

    public static IReadOnlyList<AudioChannel> DefaultChannels { get; } =
    [
        new(Game, "Game", "game"),
        new(Chat, "Chat", "chat"),
        new(Media, "Media", "music"),
        new(Other, "Other", "other"),
    ];

    private static readonly HashSet<string> ChatApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "discord.exe", "discordptb.exe", "discordcanary.exe",
        "teamspeak.exe", "ts3client_win64.exe", "ts3client_win32.exe", "teamspeak3.exe",
        "mumble.exe", "ms-teams.exe", "teams.exe", "zoom.exe", "skype.exe", "slack.exe",
    };

    private static readonly HashSet<string> MediaApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "brave.exe", "vivaldi.exe",
        "spotify.exe", "youtube music.exe", "youtube-music.exe", "ytmdesktop.exe",
        "vlc.exe", "mpv.exe", "foobar2000.exe", "musicbee.exe",
    };

    private static readonly string[] GameFolders =
    [
        @"\steamapps\common\", @"\epic games\", @"\gog galaxy\games\",
    ];

    /// <summary>Default channel for a program that has no stored choice.</summary>
    public static string Classify(string exePath, bool fullscreenForeground)
    {
        var normalized = (exePath ?? "").Replace('/', '\\');
        var file = normalized[(normalized.LastIndexOf('\\') + 1)..];
        if (ChatApps.Contains(file)) return Chat;
        if (MediaApps.Contains(file)) return Media;
        var lower = normalized.ToLowerInvariant();
        if (GameFolders.Any(lower.Contains)) return Game;
        return fullscreenForeground ? Game : Other;
    }

    /// <summary>The stored channel (any casing of the path) wins; otherwise the automatic rule.</summary>
    public static string ChannelOf(string exePath, IReadOnlyDictionary<string, string> programs, bool fullscreenForeground)
    {
        if (programs.TryGetValue(exePath, out var stored)) return stored;
        foreach (var (key, value) in programs)
            if (string.Equals(key, exePath, StringComparison.OrdinalIgnoreCase)) return value;
        return Classify(exePath, fullscreenForeground);
    }

    /// <summary>
    /// <see cref="ChannelOf"/> checked against the channel list (empty = the default channels):
    /// a stored id of a deleted channel falls back to "other".
    /// </summary>
    public static string ChannelFor(string exePath, IReadOnlyDictionary<string, string> programs, IReadOnlyList<AudioChannel> channels)
    {
        var id = ChannelOf(exePath, programs, fullscreenForeground: false);
        var list = channels.Count > 0 ? channels : DefaultChannels;
        return list.Any(c => c.Id == id) ? id : Other;
    }

    /// <summary>
    /// What a Windows session gets: volume = channel x app (clamped to 0..1) even when muted, and the mute flag
    /// when either is muted. Windows remembers per-app levels, so a mute never leaves the app at volume 0.
    /// The audible result equals <see cref="Effective"/>.
    /// </summary>
    public static (double Volume, bool Muted) SessionLevel(MixLevel channel, MixLevel app) =>
        (Math.Clamp(channel.Volume * app.Volume, 0.0, 1.0), channel.Muted || app.Muted);

    public static double Effective(MixLevel channel, MixLevel app) =>
        channel.Muted || app.Muted ? 0.0 : channel.Volume * app.Volume;

    private static Dictionary<string, MixLevel> CopyApps(Dictionary<string, MixLevel>? apps)
    {
        var copy = new Dictionary<string, MixLevel>(StringComparer.OrdinalIgnoreCase);
        foreach (var (exe, level) in apps ?? []) copy[exe] = level;
        return copy;
    }

    /// <summary>App level by exe path, case-insensitive; 1.0 unmuted when the app has none.</summary>
    public static MixLevel AppLevel(DeviceMix mix, string exe) => FindApp(mix, exe) ?? new MixLevel(1.0, false);

    private static MixLevel? FindApp(DeviceMix mix, string exe)
    {
        if (mix.Apps is not { } apps) return null;
        if (apps.TryGetValue(exe, out var level)) return level;
        foreach (var (key, value) in apps)
            if (string.Equals(key, exe, StringComparison.OrdinalIgnoreCase)) return value;
        return null;
    }

    /// <summary>
    /// Whether a session of <paramref name="exe"/> on channel <paramref name="channelId"/> is written at all: only
    /// when the user stored a level for the app or for its channel on this device. Nothing stored means hands off,
    /// so the per-app volumes and mutes set in Windows stay until the user changes the mix in the overlay. A level
    /// set back to 100 % unmuted is still an entry, so undoing a mute is written.
    /// </summary>
    public static bool ShouldWrite(DeviceMix mix, string channelId, string exe) =>
        mix.Channels?.ContainsKey(channelId) == true || FindApp(mix, exe) is not null;

    /// <summary>
    /// The device's own mix; a device seen for the first time starts from a copy of the current device's (only the
    /// entries it has), else empty: no entries, so nothing is written until the user sets a level.
    /// </summary>
    public static DeviceMix MixFor(IReadOnlyDictionary<string, DeviceMix> mixes, string deviceId, string? currentDeviceId)
    {
        if (mixes.TryGetValue(deviceId, out var own)) return own;
        if (currentDeviceId is not null && mixes.TryGetValue(currentDeviceId, out var current))
            return new DeviceMix(new(current.Channels ?? []), CopyApps(current.Apps));
        return new DeviceMix([], []);
    }

    /// <summary>Removes a custom channel and moves its programs to "other". Default channels stay.</summary>
    public static (List<AudioChannel> Channels, Dictionary<string, string> Programs) DeleteChannel(
        IReadOnlyList<AudioChannel> channels, IReadOnlyDictionary<string, string> programs, string id)
    {
        var isDefault = DefaultChannels.Any(c => c.Id == id);
        var remaining = isDefault ? [.. channels] : channels.Where(c => c.Id != id).ToList();
        var moved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (exe, channel) in programs)
            moved[exe] = !isDefault && channel == id ? Other : channel;
        return (remaining, moved);
    }

    /// <summary>"c1", "c2", ... : one past the highest custom number in use.</summary>
    public static string NextCustomId(IReadOnlyList<AudioChannel> channels)
    {
        var max = 0;
        foreach (var channel in channels)
            if (channel.Id.Length > 1 && channel.Id[0] == 'c' && int.TryParse(channel.Id.AsSpan(1), out var n) && n > max)
                max = n;
        return "c" + (max + 1);
    }

    /// <summary>A copy of the mix with the preset's channel levels applied; unknown channel ids are ignored.</summary>
    public static DeviceMix ApplyPreset(DeviceMix mix, MixPreset preset, IReadOnlyList<AudioChannel> channels)
    {
        var result = new DeviceMix(new(mix.Channels ?? []), CopyApps(mix.Apps));
        foreach (var (id, level) in preset.Channels)
            if (channels.Any(c => c.Id == id)) result.Channels[id] = level;
        return result;
    }

    /// <summary>Groups sessions by program (case-insensitive), in order of first appearance.</summary>
    public static IReadOnlyList<(string Exe, IReadOnlyList<T> Sessions)> GroupByProgram<T>(IEnumerable<T> sessions, Func<T, string> exe)
    {
        var order = new List<(string Exe, List<T> Sessions)>();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in sessions)
        {
            var name = exe(session);
            if (!index.TryGetValue(name, out var i))
            {
                i = order.Count;
                index[name] = i;
                order.Add((name, []));
            }
            order[i].Sessions.Add(session);
        }
        return order.Select(g => (g.Exe, (IReadOnlyList<T>)g.Sessions)).ToList();
    }
}

using OverlayUtils.Core.Config;
using OverlayUtils.Core.Diagnostics;
using OverlayUtils.Core.Overlay;

namespace OverlayUtils.Core.Audio;

/// <summary>
/// Every mix edit the overlay makes: channel and app levels and mutes of the current output device, presets,
/// channel definitions and program assignments. Each edit is one <see cref="ConfigStore.Update"/> followed by
/// <see cref="IAudioView.ApplyMix"/>; failures are logged, never thrown (the callers are overlay rows).
/// Reads use <see cref="ConfigStore.Current"/> snapshots.
/// </summary>
public sealed class MixController(ConfigStore config, IAudioView audio)
{
    /// <summary>Left/Right step of channel and app volumes (5 %).</summary>
    public const int StepPercent = 5;

    static readonly MixLevel Full = new(1.0, false);

    /// <summary>The channels in display order (the defaults while none are stored).</summary>
    public IReadOnlyList<AudioChannel> Channels => ChannelsOf(config.Current);

    static List<AudioChannel> ChannelsOf(AppConfig c) => c.AudioChannels.Count > 0 ? c.AudioChannels : [.. MixRules.DefaultChannels];

    string? Device => audio.Default(AudioFlow.Output)?.Id;

    /// <summary>Mix of the current output device (defaults when there is none or it has none stored yet).</summary>
    public DeviceMix CurrentMix()
    {
        var c = config.Current;
        return Device is { } device && c.AudioMix.TryGetValue(device, out var own) ? Copy(own) : MixRules.MixFor(new Dictionary<string, DeviceMix>(), "", null);
    }

    public MixLevel ChannelLevel(string id) => CurrentMix().Channels.TryGetValue(id, out var level) ? level : Full;

    public MixLevel AppLevel(string exe) => MixRules.AppLevel(CurrentMix(), exe);

    /// <summary>The channel id a program plays on (stored choice, else the automatic rule, checked against the list).</summary>
    public string ChannelOf(string exe)
    {
        var c = config.Current;
        return MixRules.ChannelFor(exe, c.AudioPrograms, c.AudioChannels);
    }

    // --- levels of the current device ------------------------------------------

    public void StepChannel(string id, int direction) =>
        EditChannel(id, level => level with { Volume = Stepped(level.Volume, direction) }, "Changing a channel volume");

    public void SetChannelLevel(string id, double volume) =>
        EditChannel(id, level => level with { Volume = Math.Clamp(volume, 0.0, 1.0) }, "Setting a channel volume");

    public void ToggleChannelMute(string id) =>
        EditChannel(id, level => level with { Muted = !level.Muted }, "Muting a channel");

    public void StepApp(string exe, int direction) =>
        EditApp(exe, level => level with { Volume = Stepped(level.Volume, direction) }, "Changing an app volume");

    public void SetAppLevel(string exe, double volume) =>
        EditApp(exe, level => level with { Volume = Math.Clamp(volume, 0.0, 1.0) }, "Setting an app volume");

    public void ToggleAppMute(string exe) =>
        EditApp(exe, level => level with { Muted = !level.Muted }, "Muting an app");

    /// <summary>Snapped to the 5 % grid, then one step; 0..1.</summary>
    static double Stepped(double volume, int direction)
    {
        var steps = 100 / StepPercent;
        var snapped = (int)Math.Round(volume * steps);
        return Math.Clamp(snapped + Math.Sign(direction), 0, steps) / (double)steps;
    }

    void EditChannel(string id, Func<MixLevel, MixLevel> change, string context) => EditMix(mix =>
    {
        mix.Channels[id] = change(mix.Channels.TryGetValue(id, out var level) ? level : Full);
    }, context);

    void EditApp(string exe, Func<MixLevel, MixLevel> change, string context) => EditMix(mix =>
    {
        var level = MixRules.AppLevel(mix, exe);
        RemoveCaseVariants(mix.Apps, exe);
        mix.Apps[exe] = change(level);
    }, context);

    /// <summary>
    /// Edits a copy of the current device's mix (a device seen for the first time starts from MixFor's mix) and
    /// stores it. The copy's Apps are case-insensitive with one key per exe.
    /// </summary>
    void EditMix(Action<DeviceMix> edit, string context)
    {
        if (Device is not { } device) return;
        Run(context, c =>
        {
            var mix = Copy(MixRules.MixFor(c.AudioMix, device, null));
            edit(mix);
            c.AudioMix[device] = mix;
        });
    }

    static DeviceMix Copy(DeviceMix mix)
    {
        var apps = new Dictionary<string, MixLevel>(StringComparer.OrdinalIgnoreCase);
        foreach (var (exe, level) in mix.Apps ?? []) apps[exe] = level;
        return new DeviceMix(new(mix.Channels ?? []), apps);
    }

    static void RemoveCaseVariants<T>(Dictionary<string, T> map, string key)
    {
        foreach (var existing in map.Keys.Where(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)).ToList())
            map.Remove(existing);
    }

    // --- presets -----------------------------------------------------------------

    /// <summary>Stores the current device's channel levels under that name (same name, any casing: replaced).</summary>
    public void SavePreset(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return;
        // Every channel, so applying the preset also restores channels that were never touched (100 %).
        var mix = CurrentMix();
        var channels = Channels.ToDictionary(ch => ch.Id, ch => mix.Channels.TryGetValue(ch.Id, out var level) ? level : Full);
        Run("Saving a mix preset", c =>
        {
            var preset = new MixPreset(name, channels);
            var index = c.AudioPresets.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) c.AudioPresets[index] = preset;
            else c.AudioPresets.Add(preset);
        });
    }

    /// <summary>Applies the preset's channel levels to the current device.</summary>
    public void ApplyPreset(string name)
    {
        if (config.Current.AudioPresets.Find(p => p.Name == name) is not { } preset) return;
        if (Device is not { } device) return;
        Run("Applying a mix preset", c =>
        {
            var mix = Copy(MixRules.MixFor(c.AudioMix, device, null));
            c.AudioMix[device] = Copy(MixRules.ApplyPreset(mix, preset, ChannelsOf(c)));
        });
    }

    // --- channel definitions -----------------------------------------------------

    /// <summary>Appends a custom channel ("c1", "c2", ...) with the "star" icon; a blank name adds nothing.</summary>
    public void AddChannel(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return;
        Run("Adding a channel", c =>
        {
            var list = ChannelsOf(c);
            list.Add(new AudioChannel(MixRules.NextCustomId(list), name, "star"));
            c.AudioChannels = list;
        });
    }

    public void RenameChannel(string id, string name)
    {
        name = name.Trim();
        if (name.Length == 0) return;
        EditChannels("Renaming a channel", list =>
        {
            var i = list.FindIndex(ch => ch.Id == id);
            if (i >= 0) list[i] = list[i] with { Name = name };
        });
    }

    /// <summary>Next (+1) or previous (-1) icon of <see cref="Glyphs.ChannelIconNames"/>, wrapping.</summary>
    public void CycleIcon(string id, int direction) => EditChannels("Changing a channel icon", list =>
    {
        var i = list.FindIndex(ch => ch.Id == id);
        if (i < 0) return;
        var names = Glyphs.ChannelIconNames;
        var at = Math.Max(names.ToList().IndexOf(list[i].Icon), 0);
        var count = names.Count;
        list[i] = list[i] with { Icon = names[(((at + Math.Sign(direction)) % count) + count) % count] };
    });

    /// <summary>
    /// Saves the channel order: the ids given first (unknown ones skipped), channels missing from it keep their
    /// relative order at the end.
    /// </summary>
    public void SetOrder(IReadOnlyList<string> ids) => EditChannels("Reordering channels", list =>
    {
        var ordered = ids.Select(id => list.Find(ch => ch.Id == id)).OfType<AudioChannel>().Distinct().ToList();
        ordered.AddRange(list.Where(ch => !ordered.Contains(ch)));
        list.Clear();
        list.AddRange(ordered);
    });

    /// <summary>
    /// Deletes a custom channel: its programs move to "other", its levels leave every device mix and preset
    /// (so a later channel reusing the id starts clean). Default channels are kept.
    /// </summary>
    public void DeleteChannel(string id)
    {
        if (MixRules.DefaultChannels.Any(ch => ch.Id == id)) return;
        Run("Deleting a channel", c =>
        {
            var (channels, programs) = MixRules.DeleteChannel(ChannelsOf(c), c.AudioPrograms, id);
            c.AudioChannels = channels;
            c.AudioPrograms = programs;
            foreach (var mix in c.AudioMix.Values) mix.Channels?.Remove(id);
            foreach (var preset in c.AudioPresets) preset.Channels?.Remove(id);
        });
    }

    void EditChannels(string context, Action<List<AudioChannel>> edit) => Run(context, c =>
    {
        var list = ChannelsOf(c);
        edit(list);
        c.AudioChannels = list;
    });

    // --- programs ----------------------------------------------------------------

    /// <summary>Moves a program to the next (+1) or previous (-1) channel of the list, wrapping.</summary>
    public void CycleProgram(string exe, int direction)
    {
        var c = config.Current;
        var channels = ChannelsOf(c);
        var at = Math.Max(channels.FindIndex(ch => ch.Id == MixRules.ChannelFor(exe, c.AudioPrograms, c.AudioChannels)), 0);
        var count = channels.Count;
        SetProgramChannel(exe, channels[(((at + Math.Sign(direction)) % count) + count) % count].Id);
    }

    public void SetProgramChannel(string exe, string channelId) => Run("Changing a program's channel", c =>
    {
        var programs = Programs(c);
        RemoveCaseVariants(programs, exe);
        programs[exe] = channelId;
        c.AudioPrograms = programs;
    });

    /// <summary>A program picked before it plays: stored with its automatic channel; a known one keeps its choice.</summary>
    public void AddProgram(string exe)
    {
        if (string.IsNullOrWhiteSpace(exe)) return;
        Run("Adding a program", c =>
        {
            var programs = Programs(c);
            if (!programs.ContainsKey(exe)) programs[exe] = MixRules.Classify(exe, fullscreenForeground: false);
            c.AudioPrograms = programs;
        });
    }

    /// <summary>A case-insensitive copy of the stored programs (the first casing of an exe wins).</summary>
    static Dictionary<string, string> Programs(AppConfig c)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (exe, channel) in c.AudioPrograms) map.TryAdd(exe, channel);
        return map;
    }

    // -----------------------------------------------------------------------------

    void Run(string context, Action<AppConfig> change)
    {
        try
        {
            config.Update(change);
            audio.ApplyMix();
        }
        catch (Exception e)
        {
            Log.Error(context, e);
        }
    }
}

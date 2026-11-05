using OverlayUtils.Core.Audio;
using OverlayUtils.Core.Config;
using OverlayUtils.Core.Games;
using OverlayUtils.Core.Input;
using OverlayUtils.Core.Localization;
using OverlayUtils.Core.Monitors;
using OverlayUtils.Core.Platform;

namespace OverlayUtils.Core.Overlay;

/// <summary>
/// The rows of the overlay: Controls and Audio tabs, Settings page and Info page (Python OVERLAY_ITEMS,
/// SETTINGS_ITEMS and _info_panels). Only features that exist in the .NET app are listed. Lists are rebuilt on every
/// call, so row kinds, icons and labels follow the current profiles, devices, config and language; the only state
/// kept here is the monitor, preset and rename picks, the open Info panel and a list being reordered, all cleared by
/// <see cref="ResetTransient"/>.
/// </summary>
public sealed class OverlayContent
{
    public const string HomeTabId = "Home";
    public const string ControlsTabId = "Controlli"; // Python tab name, kept as the stored id
    public const string AudioTabId = "Audio";
    public const string GamesTabId = "Giochi"; // Python tab name, kept as the stored id
    public const int MonitorMaxPills = 3;
    public const int MinVolumeCap = 20;
    public const int MaxVolumeCap = 100;
    const int CapStep = 5;

    const string ShortcutsPanel = "shortcuts";
    const string MonitorPanel = "monitor";
    const string SystemPanel = "system";

    readonly ConfigStore _config;
    readonly MonitorProfiles _monitors;
    readonly MonitorSwitcherLocator _locator;
    readonly IOverlayHost _host;
    readonly Action _sendAltF4;
    readonly IAudioView _audio;
    readonly IGameView? _games;
    // Manual apps: path -> title (the file description is read once, not on every list rebuild).
    readonly Dictionary<string, string> _appTitles = new(StringComparer.OrdinalIgnoreCase);

    string? _monitorPick;
    string _openPanel = ShortcutsPanel;
    // A priority list while an entry is picked up: moves change only this copy; drop saves it, cancel drops it.
    List<AudioDeviceRef>? _outputDraft;
    List<AudioDeviceRef>? _inputDraft;
    // The channel order while a channel is picked up (ids), as the device drafts.
    List<string>? _channelDraft;
    string? _presetPick;   // Presets row: the preset Left/Right landed on (applied by A)
    string? _renamePick;   // Channels page, Rename row: the channel id to rename
    GameSource? _gamesFilter; // Games tab: the source shown (null: all); kept between openings

    public OverlayContent(ConfigStore config, MonitorProfiles monitors, MonitorSwitcherLocator locator,
        IOverlayHost host, IAudioView audio, IMediaControl? media = null, Action? sendAltF4 = null,
        IGameView? games = null)
    {
        _config = config;
        _monitors = monitors;
        _locator = locator;
        _host = host;
        _audio = audio;
        _games = games;
        Mix = new MixController(config, audio);
        Media = media;
        _sendAltF4 = sendAltF4 ?? KeySender.AltF4;
        Tabs =
        [
            new OverlayTab(HomeTabId, () => Loc.T("Overlay.Tab.Home"), Home, Tiles: true),
            new OverlayTab(ControlsTabId, () => Loc.T("Overlay.Tab.Controls"), Controls),
            new OverlayTab(AudioTabId, () => Loc.T("Overlay.Tab.Audio"), Audio),
            new OverlayTab(GamesTabId, () => Loc.T("Overlay.Tab.Games"), Games),
        ];
    }

    public IReadOnlyList<OverlayTab> Tabs { get; }

    /// <summary>All mix edits of the Channels rows and the Channels / Programs pages.</summary>
    public MixController Mix { get; }

    /// <summary>The media session the Now playing row shows; the navigator takes it for LB / RB / X.</summary>
    public IMediaControl? Media { get; }

    /// <summary>
    /// Called when the overlay opens: monitor, preset and rename picks cleared, Info shows Shortcuts, no list being
    /// reordered.
    /// </summary>
    public void ResetTransient()
    {
        _monitorPick = null;
        _openPanel = ShortcutsPanel;
        _outputDraft = null;
        _inputDraft = null;
        _channelDraft = null;
        _presetPick = null;
        _renamePick = null;
    }

    // --- Games ----------------------------------------------------------------

    /// <summary>Columns of the Games grid; the UI sets it from the frame's cover columns.</summary>
    public int GameColumns { get; set; } = 3;

    public IReadOnlyList<OverlayItem> Games()
    {
        var games = _games?.Games ?? [];
        var filter = GameLibrary.Valid(_gamesFilter, games);
        if (games.Count > 0) _gamesFilter = filter; // a filter that fell back to All stays All
        var tiles = GameLibrary.Filter(games, filter);
        var loading = _games?.Loading == true;
        return
        [
            new OverlayItem
            {
                Kind = ItemKind.Grid, Icon = Glyphs.Game, Key = "games.grid",
                Visible = () => games.Count > 0,
                Label = () => filter is { } source ? SourceLabel(source) : Loc.T("Overlay.Games.All"),
                Description = () => Loc.T("Overlay.Games.Desc"),
                Tiles = () => tiles, Columns = GameColumns,
                Launch = game => _games?.Launch(game),
                AltAction = () =>
                {
                    var current = _games?.Games ?? [];
                    _gamesFilter = GameLibrary.NextFilter(GameLibrary.Valid(_gamesFilter, current), current);
                },
                AltLabel = () => Loc.T("Overlay.Footer.Filter"),
            },
            new OverlayItem
            {
                Kind = ItemKind.Info, Icon = Glyphs.Game,
                Visible = () => games.Count == 0,
                Label = () => Loc.T(loading ? "Overlay.Games.Loading" : "Overlay.Games.Empty"),
                Value = () => "",
            },
            new OverlayItem
            {
                Kind = ItemKind.Info, Icon = Glyphs.Info,
                Visible = () => games.Count == 0 && !loading,
                Label = () => Loc.T("Overlay.Games.EmptyHint"),
                Value = () => "",
            },
        ];
    }

    static string SourceName(GameSource source) => source.ToString().ToLowerInvariant();

    static string SourceLabel(GameSource source) => Loc.T("Games.Source." + source);

    IEnumerable<OverlayItem> GamesSettings()
    {
        var first = true;
        foreach (var source in Enum.GetValues<GameSource>())
        {
            var name = SourceName(source);
            var section = first;
            first = false;
            yield return new OverlayItem
            {
                Kind = ItemKind.Toggle, Icon = Glyphs.Game, Key = "settings.games." + name,
                Section = section ? () => Loc.T("Overlay.Section.Games") : null,
                Label = () => SourceLabel(source),
                Description = () => Loc.T("Settings.Games.Source.Desc", SourceLabel(source)),
                IsOn = () => !_config.Current.GamesSourcesHidden.Contains(name, StringComparer.OrdinalIgnoreCase),
                Toggle = () =>
                {
                    _config.Update(c =>
                    {
                        if (c.GamesSourcesHidden.RemoveAll(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase)) == 0)
                            c.GamesSourcesHidden.Add(name);
                    });
                    _games?.Refresh();
                },
            };
        }
        yield return new OverlayItem
        {
            Kind = ItemKind.Action, Icon = Glyphs.List, Key = "settings.games.apps",
            Label = () => Loc.T("Settings.Games.Apps"),
            Description = () => Loc.T("Settings.Games.Apps.Desc"),
            SubPage = GameAppsPage,
        };
    }

    IReadOnlyList<OverlayItem> GameAppsPage()
    {
        var rows = _config.Current.GamesManualApps.Select(path => new OverlayItem
        {
            Kind = ItemKind.Action, Icon = Glyphs.Game, Key = "games.app." + path, Stay = true,
            Label = () => AppTitle(path),
            Description = () => Loc.T("Settings.Games.App.Desc", path),
            ActionLabel = () => Loc.T("Settings.Games.RemoveApp"),
            Action = () => RemoveApp(path),
            RemoveEntry = () => RemoveApp(path),
        }).ToList();
        if (rows.Count == 0)
            rows.Add(new OverlayItem { Kind = ItemKind.Info, Label = () => Loc.T("Settings.Games.NoApps"), Value = () => "" });
        rows.Add(new OverlayItem
        {
            Kind = ItemKind.Action, Icon = Glyphs.Add, Key = "games.apps.add", Stay = true,
            Label = () => Loc.T("Settings.Games.AddApp"),
            Description = () => Loc.T("Settings.Games.AddApp.Desc"),
            Action = () => _host.ChooseProgram(path =>
            {
                _config.Update(c =>
                {
                    if (!c.GamesManualApps.Contains(path, StringComparer.OrdinalIgnoreCase)) c.GamesManualApps.Add(path);
                });
                _games?.Refresh();
            }),
        });
        return rows;
    }

    void RemoveApp(string path)
    {
        _config.Update(c => c.GamesManualApps.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)));
        _games?.Refresh();
    }

    string AppTitle(string path)
    {
        if (!_appTitles.TryGetValue(path, out var title))
            _appTitles[path] = title = File.Exists(path) ? ManualApps.TitleOf(path) : Path.GetFileNameWithoutExtension(path);
        return title;
    }

    // --- Home ---------------------------------------------------------------

    static readonly string[] HomeKeys =
        ["audio.volume", "audio.media", "audio.input", "controls.monitor", "controls.closewindow", "controls.bigpicture"];

    /// <summary>The Home tiles: items of one Audio() and one Controls() call (same instances, same callbacks).</summary>
    public IReadOnlyList<OverlayItem> Home()
    {
        var pool = Audio().Concat(Controls()).ToList();
        return HomeKeys.Select(key => pool.First(i => i.Key == key)).ToList();
    }

    // --- Controls -------------------------------------------------------------

    public IReadOnlyList<OverlayItem> Controls() =>
    [
        MonitorRow(),
        new OverlayItem
        {
            Kind = ItemKind.Segmented, Icon = Glyphs.Game, Key = "controls.bigpicture",
            Section = () => Loc.T("Overlay.Section.Steam"),
            Label = () => Loc.T("Overlay.BigPicture.Label"),
            Description = () => Loc.T("Overlay.BigPicture.Desc"),
            Options = () =>
            [
                new OverlayOption(() => Loc.T("Overlay.BigPicture.Open"), () => ShellOpen.Open("steam://open/bigpicture")),
                new OverlayOption(() => Loc.T("Overlay.BigPicture.Exit"), () => ShellOpen.Open("steam://close/bigpicture")),
            ],
        },
        new OverlayItem
        {
            Kind = ItemKind.Action, Icon = Glyphs.Desktop,
            Section = () => Loc.T("Overlay.Section.Windows"),
            Label = () => Loc.T("Overlay.ShowDesktop.Label"),
            Description = () => Loc.T("Overlay.ShowDesktop.Desc"),
            Action = KeySender.ShowDesktop,
        },
        new OverlayItem
        {
            Kind = ItemKind.Action, Icon = Glyphs.Close, Key = "controls.closewindow",
            Label = () => Loc.T("Overlay.CloseWindow.Label"),
            Description = () => Loc.T("Overlay.CloseWindow.Desc"),
            Hint = () => "Alt+F4",
            Action = () =>
            {
                // Otherwise it would close the taskbar, the desktop or whatever took the foreground meanwhile.
                if (_host.PreviousWindowIsForeground) _sendAltF4();
            },
        },
        new OverlayItem
        {
            Kind = ItemKind.Segmented, Icon = Glyphs.Power, Confirm = true,
            Section = () => Loc.T("Overlay.Section.System"),
            Label = () => Loc.T("Overlay.Power.Label"),
            Description = () => Loc.T("Overlay.Power.Desc"),
            Options = () =>
            [
                new OverlayOption(() => Loc.T("Overlay.Power.Sleep"), PowerActions.Sleep),
                new OverlayOption(() => Loc.T("Overlay.Power.Restart"), PowerActions.Restart),
                new OverlayOption(() => Loc.T("Overlay.Power.ShutDown"), PowerActions.ShutDown),
            ],
        },
    ];

    OverlayItem MonitorRow()
    {
        var profiles = _monitors.List();
        Func<bool> visible = () => _monitors.List().Count > 0;
        Func<string> label = () => Loc.T("Overlay.Monitor.Label");
        Func<string> description = () => Loc.T("Overlay.Monitor.Desc");
        Func<string> section = () => Loc.T("Overlay.Section.Screen");

        if (profiles.Count <= MonitorMaxPills)
        {
            return new OverlayItem
            {
                Kind = ItemKind.Segmented, Icon = Glyphs.Monitor, Key = "controls.monitor", Section = section, Label = label,
                Description = description, Visible = visible,
                Options = () => _monitors.List().Select(p => new OverlayOption(
                    () => p, () => _host.SwitchMonitor(p), Active: () => _monitors.Current == p)).ToList(),
            };
        }

        return new OverlayItem
        {
            Kind = ItemKind.Choice, Icon = Glyphs.Monitor, Key = "controls.monitor", Section = section, Label = label,
            Description = description, Visible = visible,
            Value = () =>
            {
                var name = MonitorPick();
                return name == _monitors.Current ? Loc.T("Overlay.Monitor.Active", name) : name;
            },
            Step = direction =>
            {
                var list = _monitors.List();
                if (list.Count == 0) return;
                var index = list.ToList().IndexOf(MonitorPick());
                _monitorPick = list[(((index + direction) % list.Count) + list.Count) % list.Count];
            },
            Apply = () =>
            {
                var name = MonitorPick();
                if (name.Length > 0) _host.SwitchMonitor(name);
            },
        };
    }

    /// <summary>The picked profile, else the current one, else the first.</summary>
    string MonitorPick()
    {
        var list = _monitors.List();
        if (_monitorPick is { } pick && list.Contains(pick)) return pick;
        var current = _monitors.Current;
        if (list.Contains(current)) return current;
        return list.Count > 0 ? list[0] : "";
    }

    // --- Audio ----------------------------------------------------------------

    public IReadOnlyList<OverlayItem> Audio()
    {
        var output = _audio.Default(AudioFlow.Output);
        var input = _audio.Default(AudioFlow.Input);
        var micMuted = _audio.MicMuted;
        return
        [
            new OverlayItem
            {
                Kind = ItemKind.Choice, Icon = OutputIcon(output?.Name), Key = "audio.output",
                Section = () => Loc.T("Overlay.Section.Devices"),
                Label = () => Loc.T("Overlay.Audio.Output"),
                Description = () => Loc.T("Overlay.Audio.Output.Desc"),
                Value = () => _audio.Default(AudioFlow.Output)?.Name ?? Loc.T("Overlay.Audio.NoDevice"),
                Step = direction => _audio.Cycle(AudioFlow.Output, direction),
            },
            new OverlayItem
            {
                Kind = ItemKind.Choice, Icon = micMuted ? Glyphs.MicOff : Glyphs.Mic, Key = "audio.input",
                Label = () => Loc.T("Overlay.Audio.Input"),
                Description = () => Loc.T("Overlay.Audio.Input.Desc"),
                Value = () => _audio.Default(AudioFlow.Input) is { } device
                    ? _audio.MicMuted ? Loc.T("Overlay.Audio.InputMuted", device.Name) : device.Name
                    : Loc.T("Overlay.Audio.NoDevice"),
                Step = direction => _audio.Cycle(AudioFlow.Input, direction),
                Toggle = _audio.ToggleMicMute,
            },
            new OverlayItem
            {
                // Value is the percent; IsOn is the mute (the view draws the bar from both).
                Kind = ItemKind.Slider, Icon = Glyphs.VolumeIcon(_audio.Volume, _audio.Muted), Key = "audio.volume",
                Section = () => Loc.T("Overlay.Section.Volume"),
                Label = () => Loc.T("Overlay.Audio.Volume"),
                Description = () => Loc.T("Overlay.Audio.Volume.Desc"),
                Value = () => Percent(_audio.Volume),
                IsOn = () => _audio.Muted,
                Step = _audio.StepVolume,
                SetLevel = _audio.SetVolume,
                Toggle = _audio.ToggleMute,
            },
            .. ChannelRows(),
            new OverlayItem
            {
                Kind = ItemKind.Media, Icon = Glyphs.Music, Key = "audio.media",
                Section = () => Loc.T("Overlay.Section.NowPlaying"),
                Visible = () => Media?.Current is not null,
                Label = () => Media?.Current?.Title is { Length: > 0 } title ? title : Loc.T("Overlay.Audio.UnknownTitle"),
                Value = () => Media?.Current is { } now
                    ? string.Join(" · ", new[] { now.Artist, now.App }.Where(p => p.Length > 0))
                    : "",
                IsOn = () => Media?.Current?.Playing == true,
                Description = () => Loc.T("Overlay.Audio.Media.Desc"),
            },
        ];
    }

    /// <summary>Name of the default output device, empty when there is none (the overlay status corner).</summary>
    public string OutputDevice => _audio.Default(AudioFlow.Output)?.Name ?? "";

    /// <summary>Python _audio_icon without Sonar: headphones by name, else a speaker.</summary>
    public static string OutputIcon(string? name)
    {
        var lower = (name ?? "").ToLowerInvariant();
        return new[] { "headphone", "headset", "cuffie", "auricolar" }.Any(lower.Contains) ? Glyphs.Headphone : Glyphs.Speaker;
    }

    static string Percent(double level) => $"{Math.Round(level * 100)}%";

    /// <summary>
    /// Channels section: each channel with at least one app playing (in channel order), its apps indented under
    /// it; then Presets (only with presets) and Save preset (only with channel rows).
    /// </summary>
    IEnumerable<OverlayItem> ChannelRows()
    {
        var config = _config.Current;
        var channels = Mix.Channels;
        var byChannel = _audio.Apps
            .GroupBy(app => MixRules.ChannelFor(app.Exe, config.AudioPrograms, config.AudioChannels))
            .ToDictionary(g => g.Key, g => g.ToList());
        var first = true;
        foreach (var channel in channels)
        {
            if (!byChannel.TryGetValue(channel.Id, out var members)) continue;
            var id = channel.Id;
            yield return new OverlayItem
            {
                Kind = ItemKind.Slider, Icon = Glyphs.ChannelIcon(channel.Icon), Key = "ch." + id,
                Section = first ? () => Loc.T("Overlay.Section.Channels") : null,
                Label = () => ChannelName(channel),
                Description = () => Loc.T("Overlay.Audio.Channel.Desc"),
                Value = () => Percent(Mix.ChannelLevel(id).Volume),
                IsOn = () => Mix.ChannelLevel(id).Muted,
                Step = direction => Mix.StepChannel(id, direction),
                SetLevel = level => Mix.SetChannelLevel(id, level),
                Toggle = () => Mix.ToggleChannelMute(id),
            };
            first = false;
            foreach (var app in members)
            {
                var exe = app.Exe;
                var level = Mix.AppLevel(exe);
                yield return new OverlayItem
                {
                    Kind = ItemKind.Slider, Child = true, Icon = Glyphs.VolumeIcon(level.Volume, level.Muted), Key = "app." + exe,
                    Label = () => app.Name,
                    Description = () => Loc.T("Overlay.Audio.App.Desc"),
                    Value = () => Percent(Mix.AppLevel(exe).Volume),
                    IsOn = () => Mix.AppLevel(exe).Muted,
                    Step = direction => Mix.StepApp(exe, direction),
                    SetLevel = value => Mix.SetAppLevel(exe, value),
                    Toggle = () => Mix.ToggleAppMute(exe),
                };
            }
        }

        var hasPresets = config.AudioPresets.Count > 0;
        if (hasPresets)
        {
            yield return new OverlayItem
            {
                Kind = ItemKind.Choice, Icon = Glyphs.ChannelIcon("star"), Key = "audio.presets",
                Section = () => Loc.T("Overlay.Section.Presets"),
                Label = () => Loc.T("Overlay.Audio.Presets"),
                Description = () => Loc.T("Overlay.Audio.Presets.Desc"),
                Value = PresetPick,
                Step = StepPreset,
                ActionLabel = () => Loc.T("Overlay.Footer.Apply"),
                Toggle = () =>
                {
                    if (PresetPick() is { Length: > 0 } name) Mix.ApplyPreset(name);
                },
            };
        }
        if (!first)
        {
            yield return new OverlayItem
            {
                Kind = ItemKind.Action, Icon = Glyphs.Add, Key = "audio.savepreset", Stay = true,
                Section = hasPresets ? null : () => Loc.T("Overlay.Section.Presets"),
                Label = () => Loc.T("Overlay.Audio.SavePreset"),
                Description = () => Loc.T("Overlay.Audio.SavePreset.Desc"),
                Action = () => _host.AskName(Loc.T("Dialog.Name.SavePreset"), SuggestedPresetName(), name =>
                {
                    Mix.SavePreset(name);
                    _presetPick = name.Trim();
                }),
            };
        }
    }

    /// <summary>
    /// Display name of a channel: a default channel still carrying its stored English default name ("Other")
    /// shows the localized name; a renamed or custom channel shows what the user typed.
    /// </summary>
    public static string ChannelName(AudioChannel channel) =>
        MixRules.DefaultChannels.FirstOrDefault(d => d.Id == channel.Id) is { } standard && channel.Name == standard.Name
            ? Loc.T("Channel." + channel.Id)
            : channel.Name;

    /// <summary>
    /// The name to store for a rename: the localized default name of a default channel (the suggestion accepted
    /// unchanged) is stored as the English default, so it keeps following the language.
    /// </summary>
    static string StoredName(string id, string typed) =>
        MixRules.DefaultChannels.FirstOrDefault(d => d.Id == id) is { } standard && typed.Trim() == Loc.T("Channel." + id)
            ? standard.Name
            : typed;

    /// <summary>The preset Left/Right picked, else the first.</summary>
    string PresetPick()
    {
        var presets = _config.Current.AudioPresets;
        if (_presetPick is { } pick && presets.Find(p => string.Equals(p.Name, pick, StringComparison.OrdinalIgnoreCase)) is { } found)
            return found.Name;
        return presets.Count > 0 ? presets[0].Name : "";
    }

    void StepPreset(int direction)
    {
        var names = _config.Current.AudioPresets.Select(p => p.Name).ToList();
        if (names.Count == 0) return;
        var index = Math.Max(names.IndexOf(PresetPick()), 0);
        _presetPick = names[(((index + Math.Sign(direction)) % names.Count) + names.Count) % names.Count];
    }

    /// <summary>
    /// "Channel N" with N one past the number of channels, skipping names in use (stored or shown, any casing).
    /// </summary>
    public static string SuggestedChannelName(IReadOnlyList<AudioChannel> channels) =>
        FreeName("Settings.Audio.ChannelName", channels.Count + 1, channels.Select(c => c.Name).Concat(channels.Select(ChannelName)));

    /// <summary>"Preset N" with N one past the number of presets, skipping names in use.</summary>
    string SuggestedPresetName()
    {
        var presets = _config.Current.AudioPresets;
        return FreeName("Overlay.Audio.PresetName", presets.Count + 1, presets.Select(p => p.Name));
    }

    /// <summary>The localized "{key} N" for the first N from <paramref name="start"/> that is not in use (any casing).</summary>
    static string FreeName(string key, int start, IEnumerable<string> inUse)
    {
        var names = inUse.ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var n = start; ; n++)
            if (Loc.T(key, n) is var name && !names.Contains(name)) return name;
    }

    /// <summary>Rows of the Channels page: the channels (reorderable, Left/Right = icon), Add channel, Rename.</summary>
    IReadOnlyList<OverlayItem> ChannelsPage()
    {
        var stored = Mix.Channels;
        var list = _channelDraft is { } draft
            ? draft.Select(id => stored.FirstOrDefault(c => c.Id == id)).OfType<AudioChannel>().ToList()
            : [.. stored];
        var rows = list.Select(channel =>
        {
            var id = channel.Id;
            var custom = !MixRules.DefaultChannels.Any(c => c.Id == id);
            return new OverlayItem
            {
                Kind = ItemKind.Reorder, Key = id, Icon = Glyphs.ChannelIcon(channel.Icon),
                Label = () => ChannelName(channel),
                Value = () => Loc.T("Icon." + channel.Icon),
                Description = () => Loc.T(custom ? "Settings.Audio.Channel.CustomDesc" : "Settings.Audio.Channel.Desc"),
                Step = direction => Mix.CycleIcon(id, direction),
                Move = direction => MoveChannel(id, direction),
                Commit = CommitChannels,
                Cancel = () => _channelDraft = null,
                RemoveEntry = custom ? () => Mix.DeleteChannel(id) : null,
                ConfirmRemove = true, // deleting loses its program assignments and levels: press twice
            };
        }).ToList();

        rows.Add(new OverlayItem
        {
            Kind = ItemKind.Action, Icon = Glyphs.Add, Key = "channels.add", Stay = true,
            Label = () => Loc.T("Settings.Audio.AddChannel"),
            Description = () => Loc.T("Settings.Audio.AddChannel.Desc"),
            Action = () => _host.AskName(Loc.T("Dialog.Name.NewChannel"), SuggestedChannelName(Mix.Channels),
                Mix.AddChannel),
        });
        rows.Add(new OverlayItem
        {
            Kind = ItemKind.Choice, Icon = Glyphs.Rename, Key = "channels.rename",
            Label = () => Loc.T("Settings.Audio.Rename"),
            Description = () => Loc.T("Settings.Audio.Rename.Desc"),
            Value = () => RenamePick() is { } pick ? ChannelName(pick) : "",
            ActionLabel = () => Loc.T("Overlay.Footer.Rename"),
            Step = direction =>
            {
                var channels = Mix.Channels;
                var index = Math.Max(channels.ToList().FindIndex(c => c.Id == RenamePick()?.Id), 0);
                _renamePick = channels[(((index + Math.Sign(direction)) % channels.Count) + channels.Count) % channels.Count].Id;
            },
            Toggle = () =>
            {
                if (RenamePick() is not { } channel) return;
                _host.AskName(Loc.T("Dialog.Name.RenameChannel"), ChannelName(channel),
                    name => Mix.RenameChannel(channel.Id, StoredName(channel.Id, name)));
            },
        });
        return rows;
    }

    AudioChannel? RenamePick()
    {
        var channels = Mix.Channels;
        return channels.FirstOrDefault(c => c.Id == _renamePick) ?? channels.FirstOrDefault();
    }

    void MoveChannel(string id, int direction)
    {
        var draft = _channelDraft ??= Mix.Channels.Select(c => c.Id).ToList();
        var from = draft.IndexOf(id);
        var to = from + Math.Sign(direction);
        if (from < 0 || to < 0 || to >= draft.Count) return;
        (draft[from], draft[to]) = (draft[to], draft[from]);
    }

    void CommitChannels()
    {
        if (_channelDraft is not { } draft) return;
        _channelDraft = null;
        Mix.SetOrder(draft);
    }

    /// <summary>Rows of the Programs page: every known program (by name) with its channel, then Add program.</summary>
    IReadOnlyList<OverlayItem> ProgramsPage()
    {
        var config = _config.Current;
        var channels = Mix.Channels;
        var live = _audio.Apps.GroupBy(a => a.Exe, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
        var rows = config.AudioPrograms.Keys
            .Select(exe => (Exe: exe, Name: live.TryGetValue(exe, out var name) ? name : FileName(exe)))
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(program =>
            {
                var exe = program.Exe;
                var channel = channels.FirstOrDefault(c => c.Id == Mix.ChannelOf(exe));
                return new OverlayItem
                {
                    Kind = ItemKind.Choice, Key = "prog." + exe, Icon = Glyphs.ChannelIcon(channel?.Icon),
                    Label = () => program.Name,
                    Description = () => Loc.T("Settings.Audio.Program.Desc", exe),
                    Value = () => Mix.Channels.FirstOrDefault(c => c.Id == Mix.ChannelOf(exe)) is { } current ? ChannelName(current) : "",
                    Step = direction => Mix.CycleProgram(exe, direction),
                };
            }).ToList();
        if (rows.Count == 0)
            rows.Add(new OverlayItem { Kind = ItemKind.Info, Label = () => Loc.T("Settings.Audio.NoPrograms"), Value = () => "" });
        rows.Add(new OverlayItem
        {
            Kind = ItemKind.Action, Icon = Glyphs.Add, Key = "programs.add", Stay = true,
            Label = () => Loc.T("Settings.Audio.AddProgram"),
            Description = () => Loc.T("Settings.Audio.AddProgram.Desc"),
            Action = () => _host.ChooseProgram(Mix.AddProgram),
        });
        return rows;
    }

    /// <summary>File name of a Windows path, on any OS.</summary>
    static string FileName(string path)
    {
        var normalized = path.Replace('/', '\\');
        return normalized[(normalized.LastIndexOf('\\') + 1)..];
    }

    /// <summary>Rows of the Output order / Input order page: the stored list, best first.</summary>
    IReadOnlyList<OverlayItem> OrderPage(AudioFlow flow)
    {
        var list = Draft(flow) ?? Stored(flow);
        var connected = _audio.Available(flow).Select(d => d.Id).ToHashSet();
        var icon = flow == AudioFlow.Output ? Glyphs.Speaker : Glyphs.Mic;
        if (list.Count == 0)
            return [new OverlayItem { Kind = ItemKind.Info, Label = () => Loc.T("Settings.Audio.NoDevices"), Value = () => "" }];

        return list.Select(device =>
        {
            var id = device.Id;
            var isConnected = connected.Contains(id);
            return new OverlayItem
            {
                Kind = ItemKind.Reorder, Key = id, Icon = icon,
                Label = () => device.Name,
                Value = () => Loc.T(isConnected ? "Settings.Audio.Connected" : "Settings.Audio.Disconnected"),
                Description = () => Loc.T(isConnected ? "Settings.Audio.Order.Desc" : "Settings.Audio.Order.RemoveDesc"),
                Move = direction => MoveEntry(flow, id, direction),
                Commit = () => CommitDraft(flow),
                Cancel = () => SetDraft(flow, null),
                RemoveEntry = isConnected ? null : () => RemoveEntry(flow, id),
            };
        }).ToList();
    }

    List<AudioDeviceRef> Stored(AudioFlow flow)
    {
        var config = _config.Current;
        return flow == AudioFlow.Output ? config.AudioOutputOrder : config.AudioInputOrder;
    }

    List<AudioDeviceRef>? Draft(AudioFlow flow) => flow == AudioFlow.Output ? _outputDraft : _inputDraft;

    void SetDraft(AudioFlow flow, List<AudioDeviceRef>? draft)
    {
        if (flow == AudioFlow.Output) _outputDraft = draft;
        else _inputDraft = draft;
    }

    void MoveEntry(AudioFlow flow, string id, int direction)
    {
        var draft = Draft(flow) ?? [.. Stored(flow)];
        SetDraft(flow, draft);
        var from = draft.FindIndex(d => d.Id == id);
        var to = from + Math.Sign(direction);
        if (from < 0 || to < 0 || to >= draft.Count) return;
        (draft[from], draft[to]) = (draft[to], draft[from]);
    }

    /// <summary>Saves the moved order; devices the service added meanwhile keep their place at the bottom.</summary>
    void CommitDraft(AudioFlow flow)
    {
        if (Draft(flow) is not { } draft) return;
        SetDraft(flow, null);
        _config.Update(c =>
        {
            var current = flow == AudioFlow.Output ? c.AudioOutputOrder : c.AudioInputOrder;
            var ids = current.Select(d => d.Id).ToHashSet();
            var merged = AudioRules.Merge(draft.Where(d => ids.Contains(d.Id)).ToList(), current);
            if (flow == AudioFlow.Output) c.AudioOutputOrder = merged;
            else c.AudioInputOrder = merged;
        });
    }

    void RemoveEntry(AudioFlow flow, string id) => _config.Update(c =>
    {
        if (flow == AudioFlow.Output) c.AudioOutputOrder.RemoveAll(d => d.Id == id);
        else c.AudioInputOrder.RemoveAll(d => d.Id == id);
    });

    /// <summary>Cap of the current default output in percent (100 when none is stored).</summary>
    int CurrentCap(string id) =>
        _config.Current.AudioVolumeCaps.TryGetValue(id, out var cap) ? Math.Clamp(cap, MinVolumeCap, MaxVolumeCap) : MaxVolumeCap;

    void StepCap(int direction)
    {
        if (_audio.Default(AudioFlow.Output) is not { } device || direction == 0) return;
        var snapped = (int)Math.Round(CurrentCap(device.Id) / (double)CapStep) * CapStep;
        var next = Math.Clamp(snapped + Math.Sign(direction) * CapStep, MinVolumeCap, MaxVolumeCap);
        _config.Update(c => c.AudioVolumeCaps[device.Id] = next);
    }

    IEnumerable<OverlayItem> AudioSettings()
    {
        OverlayItem Order(AudioFlow flow, string labelKey, string descKey, string? sectionKey) => new()
        {
            Kind = ItemKind.Action, Icon = Glyphs.List, Key = "settings." + labelKey,
            Section = sectionKey is null ? null : () => Loc.T(sectionKey),
            Label = () => Loc.T(labelKey),
            Description = () => Loc.T(descKey),
            SubPage = () => OrderPage(flow),
        };

        yield return Order(AudioFlow.Output, "Settings.Audio.OutputOrder", "Settings.Audio.OutputOrder.Desc", "Overlay.Section.Audio");
        yield return Order(AudioFlow.Input, "Settings.Audio.InputOrder", "Settings.Audio.InputOrder.Desc", null);
        yield return new OverlayItem
        {
            Kind = ItemKind.Toggle, Icon = Glyphs.Speaker,
            Label = () => Loc.T("Settings.Audio.AutoOutput"),
            Description = () => Loc.T("Settings.Audio.AutoOutput.Desc"),
            IsOn = () => _config.Current.AudioAutoOutput,
            Toggle = () => _config.Update(c => c.AudioAutoOutput = !c.AudioAutoOutput),
        };
        yield return new OverlayItem
        {
            Kind = ItemKind.Toggle, Icon = Glyphs.Mic,
            Label = () => Loc.T("Settings.Audio.AutoInput"),
            Description = () => Loc.T("Settings.Audio.AutoInput.Desc"),
            IsOn = () => _config.Current.AudioAutoInput,
            Toggle = () => _config.Update(c => c.AudioAutoInput = !c.AudioAutoInput),
        };
        yield return new OverlayItem
        {
            Kind = ItemKind.Choice, Icon = Glyphs.Volume,
            Visible = () => _audio.Default(AudioFlow.Output) is not null,
            Label = () => Loc.T("Settings.Audio.MaxVolume"),
            Description = () => Loc.T("Settings.Audio.MaxVolume.Desc", _audio.Default(AudioFlow.Output)?.Name ?? ""),
            Value = () => _audio.Default(AudioFlow.Output) is { } device ? $"{CurrentCap(device.Id)}%" : "",
            Step = StepCap,
        };
        yield return new OverlayItem
        {
            Kind = ItemKind.Action, Icon = Glyphs.ChannelIcon("music"), Key = "settings.channels",
            Label = () => Loc.T("Settings.Audio.Channels"),
            Description = () => Loc.T("Settings.Audio.Channels.Desc"),
            SubPage = ChannelsPage,
        };
        yield return new OverlayItem
        {
            Kind = ItemKind.Action, Icon = Glyphs.List, Key = "settings.programs",
            Label = () => Loc.T("Settings.Audio.Programs"),
            Description = () => Loc.T("Settings.Audio.Programs.Desc"),
            SubPage = ProgramsPage,
        };
    }

    OverlayItem TabRow(string tabId, string titleKey, string? sectionKey) => new()
    {
        Kind = ItemKind.Toggle, Icon = tabId switch { AudioTabId => Glyphs.Volume, GamesTabId => Glyphs.Game, _ => Glyphs.List },
        Section = sectionKey is null ? null : () => Loc.T(sectionKey),
        Label = () => Loc.T(titleKey),
        Description = () => Loc.T("Settings.Tab.Desc", Loc.T(titleKey)),
        IsOn = () => tabId == ControlsTabId || !_config.Current.HiddenTabs.Contains(tabId),
        Toggle = () =>
        {
            if (tabId == ControlsTabId) return; // always visible, so one tab stays
            _config.Update(c =>
            {
                if (!c.HiddenTabs.Remove(tabId)) c.HiddenTabs.Add(tabId);
            });
        },
    };

    // --- Settings -------------------------------------------------------------

    public IReadOnlyList<OverlayItem> Settings()
    {
        Func<bool> hasProfiles = () => _monitors.List().Count > 0;
        var cfg = _config.Current;
        var fullMode = _host.InFullMode;

        return
        [
            TabRow(HomeTabId, "Overlay.Tab.Home", "Overlay.Section.Tabs"),
            TabRow(ControlsTabId, "Overlay.Tab.Controls", null),
            TabRow(AudioTabId, "Overlay.Tab.Audio", null),
            TabRow(GamesTabId, "Overlay.Tab.Games", null),
            RoleRow(MonitorRole.Desk, "Settings.Desk.Label", "Settings.Desk.Desc", hasProfiles, Glyphs.Pc, "Overlay.Section.Monitor"),
            RoleRow(MonitorRole.Tv, "Settings.Tv.Label", "Settings.Tv.Desc", hasProfiles, Glyphs.Monitor, null),
            new OverlayItem
            {
                Kind = ItemKind.Segmented, Icon = Glyphs.Folder, Visible = hasProfiles,
                Label = () => Loc.T("Settings.MonitorSwitcher.Label", SwitcherStatus()),
                Description = () => Loc.T("Settings.MonitorSwitcher.Desc"),
                Options = () =>
                [
                    new OverlayOption(() => Loc.T("Settings.MonitorSwitcher.Choose"), _host.ChooseMonitorSwitcher, Stay: true),
                ],
            },
            new OverlayItem
            {
                Kind = ItemKind.Toggle, Icon = cfg.NotificationsEnabled ? Glyphs.Bell : Glyphs.BellOff,
                Section = () => Loc.T("Overlay.Section.General"),
                Label = () => Loc.T("Menu.Notifications"),
                Description = () => Loc.T("Settings.Notifications.Desc"),
                IsOn = () => _config.Current.NotificationsEnabled,
                Toggle = () => _config.Update(c => c.NotificationsEnabled = !c.NotificationsEnabled),
            },
            new OverlayItem
            {
                Kind = ItemKind.Toggle, Icon = Glyphs.Power,
                Label = () => Loc.T("Menu.Startup"),
                Description = () => Loc.T("Settings.Startup.Desc"),
                IsOn = () => StartupRegistration.IsEnabled,
                Toggle = _host.ToggleStartup,
            },
            new OverlayItem
            {
                Kind = ItemKind.Toggle, Icon = Glyphs.Game,
                Label = () => Loc.T("Settings.PadCombos"),
                Description = () => Loc.T("Settings.PadCombos.Desc"),
                IsOn = () => _config.Current.ControllerEnabled,
                Toggle = () => _config.Update(c => c.ControllerEnabled = !c.ControllerEnabled),
            },
            new OverlayItem
            {
                Kind = ItemKind.Segmented, Icon = Glyphs.Home,
                Label = () => Loc.T("Menu.HomeKey"),
                Description = () => Loc.T("Settings.HomeKey.Desc"),
                Options = () =>
                [
                    HomeKeyOption("Settings.HomeKey.Windows", HomeKeyModes.Windows),
                    HomeKeyOption("Menu.HomeKey.AltTab", HomeKeyModes.AltTab),
                    HomeKeyOption("Settings.HomeKey.None", HomeKeyModes.None),
                ],
            },
            new OverlayItem
            {
                Kind = ItemKind.Choice, Icon = Glyphs.Settings,
                Label = () => Loc.T("Menu.Language"),
                Description = () => Loc.T("Settings.Language.Desc"),
                Value = LanguageValue,
                Step = StepLanguage,
            },
            .. AudioSettings(),
            .. GamesSettings(),
            new OverlayItem
            {
                Kind = ItemKind.Segmented, Icon = Glyphs.Monitor,
                Section = () => Loc.T("Overlay.Section.Appearance"),
                Label = () => Loc.T("Settings.Size.Label"),
                Description = () => Loc.T("Settings.Size.Desc"),
                Options = () =>
                [
                    ConfigOption("Settings.Size.Normal", c => c.OverlaySize, (c, v) => c.OverlaySize = v, OverlaySizes.Normal),
                    ConfigOption("Settings.Size.Large", c => c.OverlaySize, (c, v) => c.OverlaySize = v, OverlaySizes.Large),
                    ConfigOption("Settings.Size.Auto", c => c.OverlaySize, (c, v) => c.OverlaySize = v, OverlaySizes.Auto),
                ],
            },
            AppearanceSlider("settings.blur", "Settings.Blur", Glyphs.Monitor, c => c.OverlayBlur, (c, v) => c.OverlayBlur = v, AppConfig.DefaultBlur),
            AppearanceSlider("settings.frost", "Settings.Frost", Glyphs.Monitor, c => c.OverlayFrost, (c, v) => c.OverlayFrost = v, AppConfig.DefaultFrost),
            AppearanceSlider("settings.darkness", "Settings.Darkness", Glyphs.Monitor, c => c.OverlayDim, (c, v) => c.OverlayDim = v, AppConfig.DefaultDim),
            new OverlayItem
            {
                Kind = ItemKind.Segmented, Icon = Glyphs.Game, Confirm = true,
                Label = () => Loc.T("Settings.FullMode.Label", Loc.T(fullMode ? "Settings.FullMode.On" : "Settings.FullMode.Off")),
                Description = () => Loc.T("Settings.FullMode.Desc"),
                Options = () =>
                [
                    new OverlayOption(() => Loc.T(fullMode ? "Settings.FullMode.Disable" : "Settings.FullMode.Enable"),
                        _host.SwitchFullMode),
                ],
            },
            new OverlayItem
            {
                Kind = ItemKind.Action, Icon = Glyphs.Info, OpensInfo = true,
                Section = () => Loc.T("Overlay.Section.Other"),
                Label = () => Loc.T("Settings.Info.Label"),
                Description = () => Loc.T("Settings.Info.Desc"),
            },
            new OverlayItem
            {
                Kind = ItemKind.Segmented, Icon = Glyphs.Close, Confirm = true,
                Label = () => Loc.T("Settings.Exit.Label"),
                Description = () => Loc.T("Settings.Exit.Desc"),
                Options = () => [new OverlayOption(() => Loc.T("Menu.Exit"), _host.Exit)],
            },
        ];
    }

    OverlayItem RoleRow(MonitorRole role, string labelKey, string descKey, Func<bool> visible, string icon, string? sectionKey) => new()
    {
        Kind = ItemKind.Choice, Icon = icon, Visible = visible,
        Section = sectionKey is null ? null : () => Loc.T(sectionKey),
        Label = () => Loc.T(labelKey),
        Description = () => Loc.T(descKey),
        Value = () => _monitors.ForRole(role) is { Length: > 0 } name ? name : Loc.T("Menu.Monitor.RoleNone"),
        Step = direction =>
        {
            var choices = _monitors.List().Append("").ToList();
            var index = choices.IndexOf(_monitors.ForRole(role));
            if (index < 0) index = choices.Count - 1;
            _monitors.SetRole(role, choices[(((index + direction) % choices.Count) + choices.Count) % choices.Count]);
        },
    };

    string SwitcherStatus() => _locator.Path switch
    {
        null => Loc.T("Settings.MonitorSwitcher.Searching"),
        "" => Loc.T("Settings.MonitorSwitcher.Missing"),
        _ => Loc.T("Settings.MonitorSwitcher.Found"),
    };

    OverlayOption HomeKeyOption(string labelKey, string mode) => new(
        () => Loc.T(labelKey), () => _config.Update(c => c.HomeKeyMode = mode), Stay: true,
        Active: () => _config.Current.HomeKeyMode == mode);

    /// <summary>A 0..100 % slider of the Appearance section: arrows step by 5, a click sets, A resets.</summary>
    OverlayItem AppearanceSlider(string key, string labelKey, string icon, Func<AppConfig, int> get, Action<AppConfig, int> set, int fallback)
    {
        void Set(int value) => _config.Update(c => set(c, Math.Clamp(value, 0, 100)));
        return new OverlayItem
        {
            Kind = ItemKind.Slider, Icon = icon, Key = key,
            Label = () => Loc.T(labelKey),
            Description = () => Loc.T(labelKey + ".Desc"),
            Value = () => get(_config.Current) + "%",
            Step = direction => Set(get(_config.Current) + direction * 5),
            SetLevel = level => Set((int)Math.Round(level * 100)),
            Toggle = () => Set(fallback),
            StepLabel = () => Loc.T("Overlay.Footer.Adjust"),
            ActionLabel = () => Loc.T("Overlay.Footer.Reset"),
        };
    }

    OverlayOption ConfigOption(string labelKey, Func<AppConfig, string> get, Action<AppConfig, string> set, string value) => new(
        () => Loc.T(labelKey), () => _config.Update(c => set(c, value)), Stay: true,
        Active: () => get(_config.Current) == value);

    string LanguageValue()
    {
        var language = _config.Current.Language;
        return language is "en" or "it"
            ? Loc.T("Language." + language)
            : Loc.T("Menu.Language.Auto", Loc.T("Language." + Loc.SystemCode));
    }

    void StepLanguage(int direction)
    {
        string[] order = [Loc.Auto, .. Loc.Supported];
        var index = Math.Max(Array.IndexOf(order, _config.Current.Language), 0);
        var next = order[(((index + direction) % order.Length) + order.Length) % order.Length];
        _config.Update(c => c.Language = next);
    }

    // --- Info -----------------------------------------------------------------

    public IReadOnlyList<OverlayItem> Info()
    {
        var items = new List<OverlayItem>();

        void Panel(string id, string icon, string titleKey, params OverlayItem[] rows)
        {
            items.Add(new OverlayItem
            {
                Kind = ItemKind.Expander, Icon = icon,
                Label = () => Loc.T(titleKey),
                IsOn = () => _openPanel == id,
                Toggle = () => _openPanel = _openPanel == id ? "" : id,
            });
            items.AddRange(rows);
        }

        OverlayItem Row(string panel, Func<string> label, Func<string> value, Action? action = null, Func<string>? hint = null) => new()
        {
            Kind = ItemKind.Info, Label = label, Value = value, Action = action, Hint = hint,
            Visible = () => _openPanel == panel,
        };

        OverlayItem Key(string labelKey, string keys) =>
            Row(ShortcutsPanel, () => Loc.T(labelKey), () => keys);

        var desk = _monitors.ForRole(MonitorRole.Desk);
        var tv = _monitors.ForRole(MonitorRole.Tv);

        Panel(ShortcutsPanel, Glyphs.Keyboard, "Overlay.Info.Shortcuts",
            Key("Overlay.Info.Key.Toggle", "Shift+F1"),
            Row(ShortcutsPanel, () => Loc.T("Overlay.Info.Key.Monitor", desk.Length > 0 ? desk : Loc.T("Settings.Desk.Label")),
                () => "Ctrl+Alt+↓"),
            Row(ShortcutsPanel, () => Loc.T("Overlay.Info.Key.Monitor", tv.Length > 0 ? tv : Loc.T("Settings.Tv.Label")),
                () => "Ctrl+Alt+↑"),
            Key("Overlay.Info.Key.Arrows", "↑ ↓ ← →"),
            Key("Overlay.Footer.Confirm", "Enter"),
            Key("Overlay.Info.Key.Back", "Esc"),
            Key("Overlay.Info.Key.Tabs", "Tab / PgUp / PgDn"),
            Key("Overlay.Footer.Settings", "F2"),
            Key("Overlay.Info.Pad.Toggle", "Back + RB"),
            Key("Overlay.Info.Pad.Monitor", "Back + ↑ / ↓"),
            Key("Overlay.Info.Pad.Confirm", "A / B"),
            Key("Overlay.Info.Key.Tabs", "LT / RT"),
            Key("Overlay.Info.Pad.SettingsHold", "Back"),
            Key("Overlay.Info.Pad.Volume", "Back + ← / →"),
            Key("Overlay.Info.Pad.Mic", "Back + Y"),
            Key("Overlay.Info.Pad.Media", "LB / RB / X"));

        var profiles = _monitors.List();
        Panel(MonitorPanel, Glyphs.Monitor, "Overlay.Info.Monitor",
            Row(MonitorPanel, () => Loc.T("Overlay.Info.Switcher"),
                () => _locator.Path switch
                {
                    null => Loc.T("Settings.MonitorSwitcher.Searching"),
                    "" => Loc.T("Settings.MonitorSwitcher.Missing"),
                    var path => path,
                },
                () => OpenSwitcher(),
                () => Loc.T(string.IsNullOrEmpty(_locator.Path) ? "Overlay.Info.Download" : "Overlay.Footer.Open")),
            Row(MonitorPanel, () => Loc.T("Overlay.Info.Profiles"),
                () => profiles.Count > 0 ? string.Join(", ", profiles) : Loc.T("Overlay.Info.NoProfiles")),
            Row(MonitorPanel, () => Loc.T("Overlay.Info.Folder"), () => _monitors.ProfilesDir,
                () => ShellOpen.Open(_monitors.ProfilesDir), () => Loc.T("Overlay.Footer.Open")),
            Row(MonitorPanel, () => Loc.T("Overlay.Info.Roles"),
                () => $"{(desk.Length > 0 ? desk : "—")} · {(tv.Length > 0 ? tv : "—")}"));

        Panel(SystemPanel, Glyphs.Pc, "Overlay.Info.System",
            Row(SystemPanel, () => Loc.T("Overlay.Info.Version", typeof(OverlayContent).Assembly.GetName().Version?.ToString(3) ?? "?"),
                () => AppPaths.AppDir, () => ShellOpen.Open(AppPaths.AppDir), () => Loc.T("Overlay.Footer.Open")),
            Row(SystemPanel, () => Loc.T("Overlay.Info.Mode"),
                () => Loc.T(_host.InFullMode ? "Tooltip.Mode.Full" : "Tooltip.Mode.Standard")),
            Row(SystemPanel, () => Loc.T("Overlay.Info.Config"), () => AppPaths.ConfigDir,
                () => ShellOpen.Open(AppPaths.ConfigDir), () => Loc.T("Overlay.Footer.Open")));

        return items;
    }

    void OpenSwitcher()
    {
        var path = _locator.Path;
        var folder = string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(path);
        ShellOpen.Open(folder ?? MonitorSwitcherLocator.Website);
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace OverlayUtils.Core.Config;

/// <summary>
/// Contents of config.json. Property names match the Python AppUtili
/// config so a migrated file loads as-is. Keys this version does not know
/// yet (e.g. Spotify/Discord settings, ported in later stages) are kept in
/// <see cref="Extra"/> and written back untouched.
/// </summary>
public sealed class AppConfig
{
    /// <summary>"auto" (follow Windows), "en" or "it".</summary>
    [JsonPropertyName("language")] public string Language { get; set; } = "auto";

    /// <summary>"windows", "alttab" or "none".</summary>
    [JsonPropertyName("home_key_mode")] public string HomeKeyMode { get; set; } = HomeKeyModes.None;

    [JsonPropertyName("controller_enabled")] public bool ControllerEnabled { get; set; } = true;
    [JsonPropertyName("notifications_enabled")] public bool NotificationsEnabled { get; set; } = true;

    /// <summary>Last loaded monitor profile ("main"/"tv" in very old configs).</summary>
    [JsonPropertyName("monitor_profile")] public string MonitorProfile { get; set; } = "";

    /// <summary>Profile with the Desk role: "" = guess from names, "-" = none.</summary>
    [JsonPropertyName("monitor_desk")] public string MonitorDesk { get; set; } = "";

    /// <summary>Profile with the TV role: "" = guess from names, "-" = none.</summary>
    [JsonPropertyName("monitor_tv")] public string MonitorTv { get; set; } = "";

    [JsonPropertyName("monitor_switcher_exe")] public string MonitorSwitcherExe { get; set; } = "";
    [JsonPropertyName("hidden_tabs")] public List<string> HiddenTabs { get; set; } = [];

    /// <summary>"normal", "large" or "auto" (large on the TV profile).</summary>
    [JsonPropertyName("overlay_size")] public string OverlaySize { get; set; } = OverlaySizes.Auto;

    /// <summary>"left" or "right" edge of the work area.</summary>
    [JsonPropertyName("overlay_side")] public string OverlaySide { get; set; } = OverlaySides.Left;

    /// <summary>Output devices, best first (Audio tab order).</summary>
    [JsonPropertyName("audio_output_order")] public List<AudioDeviceRef> AudioOutputOrder { get; set; } = [];

    /// <summary>Input devices, best first.</summary>
    [JsonPropertyName("audio_input_order")] public List<AudioDeviceRef> AudioInputOrder { get; set; } = [];

    [JsonPropertyName("audio_auto_output")] public bool AudioAutoOutput { get; set; } = true;
    [JsonPropertyName("audio_auto_input")] public bool AudioAutoInput { get; set; } = true;

    /// <summary>Volume cap per output device id, in percent (missing = 100).</summary>
    [JsonPropertyName("audio_volume_caps")] public Dictionary<string, int> AudioVolumeCaps { get; set; } = [];

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public AppConfig Clone() =>
        JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(this, ConfigStore.JsonOptions),
            ConfigStore.JsonOptions)!;
}

/// <summary>An audio endpoint remembered in the config: stable id plus last known name.</summary>
public sealed record AudioDeviceRef(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name);

public static class HomeKeyModes
{
    public const string Windows = "windows";
    public const string AltTab = "alttab";
    public const string None = "none";
}

public static class OverlaySizes
{
    public const string Normal = "normal";
    public const string Large = "large";
    public const string Auto = "auto";
}

public static class OverlaySides
{
    public const string Left = "left";
    public const string Right = "right";
}

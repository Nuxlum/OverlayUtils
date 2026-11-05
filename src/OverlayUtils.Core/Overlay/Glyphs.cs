namespace OverlayUtils.Core.Overlay;

/// <summary>Segoe Fluent Icons / Segoe MDL2 Assets code points (Python's ICON_* constants).</summary>
public static class Glyphs
{
    /// <summary>Battery levels 0..3 (empty..full), as Python ICON_BATTERY_LEVELS.</summary>
    public static readonly string[] Battery = ["", "", "", ""];
    public const string Monitor = "";
    public const string Game = "";
    public const string Desktop = "";
    public const string Close = "";
    public const string Power = "";
    public const string Bell = "";
    public const string BellOff = "";
    public const string List = "";
    public const string Pc = "";
    public const string Folder = "";
    public const string Home = "";
    public const string Info = "";
    public const string Keyboard = "";
    public const string Link = "";
    public const string ChevronLeft = "";
    public const string ChevronRight = "";
    /// <summary>Controls page in the overlay rail (lightning bolt).</summary>
    public const string Controls = "";
    public const string Settings = "";
    public const string Add = "";
    public const string Rename = "";

    // Audio (Python ICON_SPEAKER, ICON_HEADPHONE, ICON_VOLUME*, ICON_MUTE, ICON_MIC*, ICON_PLAY ... ICON_MUSIC).
    public const string Speaker = "";
    public const string Headphone = "";
    public const string Volume = "";
    /// <summary>Volume levels 0..3 (low..high), as Python ICON_VOLUME_LEVELS.</summary>
    public static readonly string[] VolumeLevels = ["", "", "", ""];
    public const string Mute = "";
    public const string Mic = "";
    public const string MicOff = "";
    public const string Play = "";
    public const string Pause = "";
    public const string Previous = "";
    public const string Next = "";
    public const string Music = "";

    /// <summary>Python _volume_icon: mute, else one of four levels.</summary>
    public static string VolumeIcon(double level, bool muted) =>
        muted ? Mute : VolumeLevels[Math.Clamp((int)(level * 4), 0, 3)];

    /// <summary>
    /// Icons a mixer channel can have, in the order Left/Right cycles them (name = stored key, also "Icon.name").
    /// </summary>
    public static IReadOnlyList<string> ChannelIconNames { get; } =
    [
        "game", "chat", "music", "video", "browser", "mic", "headphones", "speaker",
        "star", "heart", "people", "globe", "bolt", "home", "code", "other",
    ];

    static readonly Dictionary<string, string> ChannelGlyphs = new()
    {
        ["game"] = Game, ["chat"] = "", ["music"] = Music, ["video"] = "",
        ["browser"] = "", ["mic"] = Mic, ["headphones"] = Headphone, ["speaker"] = Speaker,
        ["star"] = "", ["heart"] = "", ["people"] = "", ["globe"] = "",
        ["bolt"] = "", ["home"] = Home, ["code"] = "", ["other"] = "",
    };

    /// <summary>Glyph of a channel icon name; an unknown name shows the "other" glyph.</summary>
    public static string ChannelIcon(string? name) =>
        name is not null && ChannelGlyphs.TryGetValue(name, out var glyph) ? glyph : ChannelGlyphs["other"];
}

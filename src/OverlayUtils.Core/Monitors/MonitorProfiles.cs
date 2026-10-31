using System.Diagnostics;
using OverlayUtils.Core.Config;
using OverlayUtils.Core.Diagnostics;

namespace OverlayUtils.Core.Monitors;

public enum MonitorRole { Desk, Tv }

public enum SwitchResult { Switched, NoProfile, SwitcherMissing, SwitcherFailed }

/// <summary>
/// Monitor setups saved by MonitorSwitcher (monitorprofileswitcher.com) as
/// .xml files, loaded with "MonitorSwitcher.exe -load:&lt;file&gt;".
///
/// Two roles pick which profile Ctrl+Alt+Down (Desk) and Ctrl+Alt+Up (TV)
/// load. Until the user sets them they are guessed from the profile names,
/// otherwise the first and second profile.
/// </summary>
public sealed class MonitorProfiles
{
    public const string NoRole = "-";

    private static readonly Dictionary<MonitorRole, (string[] Words, int Fallback)> RoleRules = new()
    {
        [MonitorRole.Desk] = (["main", "desk", "scrivania"], 0),
        [MonitorRole.Tv] = (["tv"], 1),
    };

    /// <summary>Tray / overlay colours: Desk, TV, then the others in rotation.</summary>
    public static readonly (byte R, byte G, byte B)[] Colors =
        [(0, 120, 215), (136, 23, 152), (16, 137, 62), (202, 80, 16)];
    public static readonly (byte R, byte G, byte B) NoColor = (110, 110, 110);

    private readonly ConfigStore _config;
    private readonly MonitorSwitcherLocator _locator;
    private readonly string _profilesDir;
    private (long? Stamp, IReadOnlyList<string> Names) _cache = (null, []);

    public MonitorProfiles(ConfigStore config, MonitorSwitcherLocator locator, string? profilesDir = null)
    {
        _config = config;
        _locator = locator;
        _profilesDir = profilesDir ?? AppPaths.MonitorProfilesDir;
    }

    public string ProfilesDir => _profilesDir;

    /// <summary>Profile names, alphabetical. Re-read at most every 2 seconds (called on every redraw).</summary>
    public IReadOnlyList<string> List()
    {
        var now = Environment.TickCount64;
        if (_cache.Stamp is not { } stamp || now - stamp > 2000)
        {
            IReadOnlyList<string> names;
            try
            {
                names = Directory.Exists(_profilesDir)
                    ? Directory.EnumerateFiles(_profilesDir, "*.xml")
                        .Select(Path.GetFileNameWithoutExtension)
                        .OfType<string>()
                        .Order(StringComparer.OrdinalIgnoreCase)
                        .ToList()
                    : [];
            }
            catch (IOException) { names = []; }
            catch (UnauthorizedAccessException) { names = []; }
            _cache = (now, names);
        }
        return _cache.Names;
    }

    public void InvalidateCache() => _cache = (null, []);

    /// <summary>Profile holding a role, or "" if none.</summary>
    public string ForRole(MonitorRole role)
    {
        var profiles = List();
        var cfg = _config.Current;
        var chosen = role == MonitorRole.Desk ? cfg.MonitorDesk : cfg.MonitorTv;
        if (chosen == NoRole) return "";
        if (profiles.Contains(chosen)) return chosen;

        var (words, fallback) = RoleRules[role];
        foreach (var p in profiles)
            if (words.Any(w => p.Contains(w, StringComparison.OrdinalIgnoreCase)))
                return p;
        return profiles.Count > fallback ? profiles[fallback] : "";
    }

    /// <summary>Assign a profile to a role ("" or null = no profile).</summary>
    public void SetRole(MonitorRole role, string? name) =>
        _config.Update(c =>
        {
            var value = string.IsNullOrEmpty(name) ? NoRole : name;
            if (role == MonitorRole.Desk) c.MonitorDesk = value; else c.MonitorTv = value;
        });

    /// <summary>Last loaded profile.</summary>
    public string Current
    {
        get
        {
            var name = _config.Current.MonitorProfile;
            return name switch // configs from the version with two fixed profiles
            {
                "main" => ForRole(MonitorRole.Desk),
                "tv" => ForRole(MonitorRole.Tv),
                _ => name,
            };
        }
    }

    /// <summary>(colour, label) for a profile: label is the name if very short ("TV"), else its initial.</summary>
    public ((byte R, byte G, byte B) Color, string Label) Style(string name)
    {
        if (string.IsNullOrEmpty(name)) return (NoColor, "?");
        string[] roles = [ForRole(MonitorRole.Desk), ForRole(MonitorRole.Tv)];
        (byte, byte, byte) color;
        var roleIndex = Array.IndexOf(roles, name);
        if (roleIndex >= 0)
        {
            color = Colors[roleIndex];
        }
        else
        {
            var others = List().Where(p => !roles.Contains(p)).ToList();
            var index = Math.Max(0, others.IndexOf(name));
            color = Colors[2 + index % (Colors.Length - 2)];
        }
        var label = name.Length <= 3 ? name.ToUpperInvariant() : name[..1].ToUpperInvariant();
        return (color, label);
    }

    public SwitchResult SwitchTo(MonitorRole role) => Switch(ForRole(role));

    /// <summary>Loads a profile with MonitorSwitcher and remembers it.</summary>
    public SwitchResult Switch(string name)
    {
        if (string.IsNullOrEmpty(name)) return SwitchResult.NoProfile;
        _config.Update(c => c.MonitorProfile = name);

        var exe = _locator.Path ?? _locator.Find();
        if (string.IsNullOrEmpty(exe)) return SwitchResult.SwitcherMissing;
        try
        {
            var info = new ProcessStartInfo(exe) { UseShellExecute = false };
            info.ArgumentList.Add("-load:" + Path.Combine(_profilesDir, name + ".xml"));
            Process.Start(info)?.Dispose();
            return SwitchResult.Switched;
        }
        catch (Exception e)
        {
            Log.Error("Starting MonitorSwitcher", e);
            return SwitchResult.SwitcherFailed;
        }
    }
}

using System.Text.Json;
using OverlayUtils.Core.Diagnostics;

namespace OverlayUtils.Core.Config;

/// <summary>
/// Thread-safe owner of the configuration. Read with <see cref="Current"/>
/// (a snapshot), change with <see cref="Update"/>, which saves to disk and
/// raises <see cref="Changed"/> on the calling thread.
/// </summary>
public sealed class ConfigStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly object _lock = new();
    private readonly string _path;
    private AppConfig _config;

    public ConfigStore(string path)
    {
        _path = path;
        _config = Load(path);
    }

    public event Action<AppConfig>? Changed;

    /// <summary>A copy of the current settings; safe to read from any thread.</summary>
    public AppConfig Current
    {
        get { lock (_lock) return _config.Clone(); }
    }

    public void Update(Action<AppConfig> change)
    {
        AppConfig snapshot;
        lock (_lock)
        {
            change(_config);
            Save(_path, _config);
            snapshot = _config.Clone();
        }
        Changed?.Invoke(snapshot);
    }

    internal static AppConfig Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOptions);
                if (loaded is not null)
                {
                    loaded.HiddenTabs ??= [];
                    loaded.AudioOutputOrder ??= [];
                    loaded.AudioInputOrder ??= [];
                    loaded.AudioVolumeCaps ??= [];
                    loaded.AudioChannels ??= [];
                    loaded.AudioPrograms ??= [];
                    loaded.AudioMix ??= [];
                    loaded.AudioPresets ??= [];
                    loaded.GamesSourcesHidden ??= [];
                    loaded.GamesManualApps ??= [];
                    return loaded;
                }
            }
        }
        catch (Exception e)
        {
            // A corrupt file must not stop the app: start from defaults and keep a copy.
            Log.Error($"config.json unreadable, using defaults: {e.Message}");
            TryBackup(path);
        }
        return new AppConfig();
    }

    internal static void Save(string path, AppConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, JsonOptions));
        File.Move(temp, path, overwrite: true); // atomic replace: no half-written config
    }

    private static void TryBackup(string path)
    {
        try { File.Copy(path, path + ".bad", overwrite: true); } catch { /* best effort */ }
    }
}

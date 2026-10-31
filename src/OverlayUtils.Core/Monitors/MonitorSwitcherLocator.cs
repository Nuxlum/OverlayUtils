using System.Diagnostics;
using OverlayUtils.Core.Config;
using OverlayUtils.Core.Diagnostics;

namespace OverlayUtils.Core.Monitors;

/// <summary>
/// Finds MonitorSwitcher.exe. It is a portable zip, so it can be anywhere:
/// saved path, running process (it usually sits in the tray), the user's
/// folders, Program Files, then every fixed drive down to two levels.
/// </summary>
public sealed class MonitorSwitcherLocator
{
    public const string ExeName = "MonitorSwitcher.exe";
    public const string Website = "https://monitorprofileswitcher.com/";

    private readonly ConfigStore _config;
    private readonly object _lock = new();
    private string? _path; // null = not searched yet, "" = not found

    public MonitorSwitcherLocator(ConfigStore config) => _config = config;

    /// <summary>Path found so far: null while the search has not finished, "" if missing.</summary>
    public string? Path
    {
        get { lock (_lock) return _path; }
    }

    public void StartSearch() => Task.Run(Find);

    /// <summary>Searches synchronously and saves the result.</summary>
    public string Find()
    {
        var saved = _config.Current.MonitorSwitcherExe;
        var found = !string.IsNullOrEmpty(saved) && File.Exists(saved) ? saved : null;
        found ??= RunningCopy();
        found ??= SearchFolders(CandidateRoots(), ExeName, depth: 2);
        found ??= "";

        if (found != saved) _config.Update(c => c.MonitorSwitcherExe = found);
        lock (_lock) _path = found;
        return found;
    }

    /// <summary>Path picked by hand in the settings.</summary>
    public void Set(string path)
    {
        _config.Update(c => c.MonitorSwitcherExe = path);
        lock (_lock) _path = path;
    }

    private static string? RunningCopy()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName(System.IO.Path.GetFileNameWithoutExtension(ExeName)))
            {
                using (p)
                {
                    try
                    {
                        var file = p.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(file)) return file;
                    }
                    catch (Exception) { /* access denied for some processes: skip */ }
                }
            }
        }
        catch (Exception e)
        {
            Log.Error("Reading processes", e);
        }
        return null;
    }

    private static IEnumerable<string> CandidateRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] roots =
        [
            System.IO.Path.Combine(home, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        ];
        var drives = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed)
            .Select(d => d.RootDirectory.FullName);
        return roots.Concat(drives).Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r));
    }

    /// <summary>Breadth-first search for <paramref name="name"/> down to <paramref name="depth"/> levels.</summary>
    internal static string? SearchFolders(IEnumerable<string> roots, string name, int depth)
    {
        foreach (var root in roots)
        {
            var level = new List<string> { root };
            for (var d = 0; d <= depth && level.Count > 0; d++)
            {
                var deeper = new List<string>();
                foreach (var folder in level)
                {
                    var candidate = System.IO.Path.Combine(folder, name);
                    if (File.Exists(candidate)) return candidate;
                    try
                    {
                        foreach (var dir in new DirectoryInfo(folder).EnumerateDirectories())
                        {
                            if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                            if (dir.Name.StartsWith('$') || dir.Name.StartsWith('.')) continue;
                            if (dir.Name.Equals("Windows", StringComparison.OrdinalIgnoreCase)) continue;
                            deeper.Add(dir.FullName);
                        }
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                level = deeper;
            }
        }
        return null;
    }
}

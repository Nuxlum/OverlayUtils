using System.Diagnostics;

namespace OverlayUtils.Core.Diagnostics;

/// <summary>
/// Appends to %APPDATA%\OverlayUtils\errors.log. The app has no console, so
/// this is where problems become visible. The file restarts once it passes
/// <see cref="MaxBytes"/>.
/// </summary>
public static class Log
{
    public const long MaxBytes = 256 * 1024;
    private static readonly object Lock = new();

    public static void Error(string message) => Write("ERROR", message);
    public static void Error(string context, Exception e) => Write("ERROR", $"{context}: {e}");
    public static void Info(string message) => Write("INFO ", message);

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message.TrimEnd()}";
        Debug.WriteLine(line);
        try
        {
            lock (Lock)
            {
                var path = AppPaths.ErrorLog;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                    File.WriteAllText(path, "");
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

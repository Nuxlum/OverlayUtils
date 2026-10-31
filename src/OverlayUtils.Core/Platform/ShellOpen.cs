using System.Diagnostics;
using OverlayUtils.Core.Diagnostics;

namespace OverlayUtils.Core.Platform;

/// <summary>Opens a URI, folder or file with its default handler (ms-settings:, steam://, ...).</summary>
public static class ShellOpen
{
    public static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e)
        {
            Log.Error($"ShellOpen {target}", e);
        }
    }
}

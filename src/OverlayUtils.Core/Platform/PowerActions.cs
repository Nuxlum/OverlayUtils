using System.Diagnostics;
using OverlayUtils.Core.Diagnostics;
using OverlayUtils.Core.Interop;

namespace OverlayUtils.Core.Platform;

/// <summary>Sleep, restart and shut down; failures are logged, never thrown.</summary>
public static class PowerActions
{
    public static void Sleep()
    {
        try { Native.SetSuspendState(false, false, false); }
        catch (Exception e) { Log.Error("Sleep", e); }
    }

    public static void Restart() => Shutdown("/r");

    public static void ShutDown() => Shutdown("/s");

    private static void Shutdown(string mode)
    {
        try
        {
            Process.Start(new ProcessStartInfo("shutdown", $"{mode} /t 0") { CreateNoWindow = true, UseShellExecute = false })?.Dispose();
        }
        catch (Exception e)
        {
            Log.Error($"shutdown {mode}", e);
        }
    }
}

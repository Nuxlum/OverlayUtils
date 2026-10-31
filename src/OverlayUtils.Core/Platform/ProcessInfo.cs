using OverlayUtils.Core.Interop;

namespace OverlayUtils.Core.Platform;

/// <summary>What the current process is allowed to do.</summary>
public static class ProcessInfo
{
    /// <summary>True when Windows granted UIAccess (overlay above fullscreen games).</summary>
    public static bool HasUIAccess => QueryToken(Native.TokenUIAccess);

    /// <summary>True when running elevated (only the setup helper should be).</summary>
    public static bool IsElevated => QueryToken(Native.TokenElevation);

    private static bool QueryToken(int infoClass)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (!Native.OpenProcessToken(Native.GetCurrentProcess(), Native.TOKEN_QUERY, out var token))
            return false;
        try
        {
            return Native.GetTokenInformation(token, infoClass, out var value, sizeof(uint), out _) && value != 0;
        }
        finally
        {
            Native.CloseHandle(token);
        }
    }
}

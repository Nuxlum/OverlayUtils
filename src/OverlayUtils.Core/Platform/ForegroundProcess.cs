using System.Runtime.InteropServices;
using OverlayUtils.Core.Interop;

namespace OverlayUtils.Core.Platform;

public static class ForegroundProcess
{
    /// <summary>The image path and start time (UTC) of the process owning <paramref name="hwnd"/>; null on any failure.</summary>
    public static (string ExePath, DateTime StartUtc)? Describe(nint hwnd)
    {
        try
        {
            if (hwnd == 0 || Native.GetWindowThreadProcessId(hwnd, out var processId) == 0 || processId == 0) return null;
            var handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (handle == 0) return null;
            try
            {
                Span<ushort> buffer = stackalloc ushort[1024];
                var size = (uint)buffer.Length;
                if (!Native.QueryFullProcessImageName(handle, 0, ref buffer[0], ref size) || size == 0) return null;
                if (!Native.GetProcessTimes(handle, out var created, out _, out _, out _)) return null;
                var ticks = ((long)created.HighDateTime << 32) | created.LowDateTime;
                return (new string(MemoryMarshal.Cast<ushort, char>(buffer[..(int)size])), DateTime.FromFileTimeUtc(ticks));
            }
            finally { Native.CloseHandle(handle); }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return null; }
    }
}

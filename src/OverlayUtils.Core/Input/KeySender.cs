using System.Runtime.InteropServices;
using OverlayUtils.Core.Interop;

namespace OverlayUtils.Core.Input;

/// <summary>
/// Synthesised key presses (SendInput). Without UIAccess or elevation,
/// Windows silently drops input aimed at elevated windows (UIPI).
/// </summary>
public static class KeySender
{
    /// <summary>Presses the keys in order and releases them in reverse order.</summary>
    public static void Chord(params ushort[] virtualKeys)
    {
        var inputs = new List<Native.INPUT>();
        foreach (var vk in virtualKeys) inputs.Add(Key(vk, up: false));
        for (var i = virtualKeys.Length - 1; i >= 0; i--) inputs.Add(Key(virtualKeys[i], up: true));
        Send(inputs);
    }

    /// <summary>
    /// Same as <see cref="Chord"/> but with hardware scan codes: sends physical
    /// keys regardless of layout (e.g. the Italian "è" key for SteelSeries GG).
    /// </summary>
    public static void ScanChord(params ushort[] scanCodes)
    {
        var inputs = new List<Native.INPUT>();
        foreach (var sc in scanCodes) inputs.Add(Scan(sc, up: false));
        for (var i = scanCodes.Length - 1; i >= 0; i--) inputs.Add(Scan(scanCodes[i], up: true));
        Send(inputs);
    }

    public static void WindowsKey() => Chord(Native.VK_LWIN);
    public static void AltTab() => Chord(Native.VK_MENU, Native.VK_TAB);
    public static void ShowDesktop() => Chord(Native.VK_LWIN, Native.VK_D);
    public static void AltF4() => Chord(Native.VK_MENU, Native.VK_F4);

    private static bool IsExtended(ushort vk) => vk is Native.VK_LWIN or Native.VK_UP or Native.VK_DOWN;

    private static Native.INPUT Key(ushort vk, bool up) => new()
    {
        type = Native.INPUT_KEYBOARD,
        u = new Native.InputUnion
        {
            ki = new Native.KEYBDINPUT
            {
                wVk = vk,
                dwFlags = (up ? Native.KEYEVENTF_KEYUP : 0) | (IsExtended(vk) ? Native.KEYEVENTF_EXTENDEDKEY : 0),
            },
        },
    };

    private static Native.INPUT Scan(ushort sc, bool up) => new()
    {
        type = Native.INPUT_KEYBOARD,
        u = new Native.InputUnion
        {
            ki = new Native.KEYBDINPUT
            {
                wScan = sc,
                dwFlags = Native.KEYEVENTF_SCANCODE | (up ? Native.KEYEVENTF_KEYUP : 0),
            },
        },
    };

    private static void Send(List<Native.INPUT> inputs)
    {
        var array = inputs.ToArray();
        Native.SendInput((uint)array.Length, array, Marshal.SizeOf<Native.INPUT>());
    }
}

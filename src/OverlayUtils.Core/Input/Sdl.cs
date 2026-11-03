using System.Reflection;
using System.Runtime.InteropServices;

namespace OverlayUtils.Core.Input;

/// <summary>
/// The few SDL3 functions the pad service needs. SDL3.dll is resolved only
/// from the app folder (never PATH or the current directory): a UIAccess
/// process must not load code from user-writable places.
/// Event layouts checked against SDL 3.4.18 include/SDL3/SDL_events.h.
/// </summary>
internal static partial class Sdl
{
    private const string Lib = "SDL3";

    public const uint INIT_GAMEPAD = 0x00002000;
    public const string HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS = "SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS";

    public const uint EVENT_GAMEPAD_AXIS_MOTION = 0x650;
    public const uint EVENT_GAMEPAD_BUTTON_DOWN = 0x651;
    public const uint EVENT_GAMEPAD_BUTTON_UP = 0x652;
    public const uint EVENT_GAMEPAD_ADDED = 0x653;
    public const uint EVENT_GAMEPAD_REMOVED = 0x654;

    /// <summary>SDL_Event is a 128-byte union; only the gamepad fields are read.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    public struct Event
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(16)] public uint Which;     // SDL_JoystickID
        [FieldOffset(20)] public byte Control;   // button or axis
        [FieldOffset(21)] public byte Down;      // button events
        [FieldOffset(24)] public short Value;    // axis events
    }

    static Sdl()
    {
        NativeLibrary.SetDllImportResolver(typeof(Sdl).Assembly, Resolve);
    }

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Lib) return 0;
        var file = Path.Combine(AppContext.BaseDirectory, "SDL3.dll");
        return NativeLibrary.TryLoad(file, out var handle) ? handle : throw new DllNotFoundException(file);
    }

    [LibraryImport(Lib, EntryPoint = "SDL_SetHint", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetHint(string name, string value);

    [LibraryImport(Lib, EntryPoint = "SDL_Init")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool Init(uint flags);

    [LibraryImport(Lib, EntryPoint = "SDL_Quit")]
    public static partial void Quit();

    [LibraryImport(Lib, EntryPoint = "SDL_GetError")]
    private static partial nint GetErrorPtr();

    public static string GetError() => Marshal.PtrToStringUTF8(GetErrorPtr()) ?? "";

    [LibraryImport(Lib, EntryPoint = "SDL_WaitEventTimeout")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool WaitEventTimeout(out Event e, int timeoutMs);

    [LibraryImport(Lib, EntryPoint = "SDL_OpenGamepad")]
    public static partial nint OpenGamepad(uint instanceId);

    [LibraryImport(Lib, EntryPoint = "SDL_CloseGamepad")]
    public static partial void CloseGamepad(nint gamepad);

    /// <summary>Returns SDL_PowerState (-1 error, 0 unknown, 1 on battery, 2 no battery, 3 charging, 4 charged).</summary>
    [LibraryImport(Lib, EntryPoint = "SDL_GetGamepadPowerInfo")]
    public static partial int GetGamepadPowerInfo(nint gamepad, out int percent);

    [LibraryImport(Lib, EntryPoint = "SDL_GetGamepadName")]
    private static partial nint GetGamepadNamePtr(nint gamepad);

    public static string GetGamepadName(nint gamepad) => Marshal.PtrToStringUTF8(GetGamepadNamePtr(gamepad)) ?? "?";
}

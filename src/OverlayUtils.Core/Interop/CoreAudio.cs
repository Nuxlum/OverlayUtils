using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace OverlayUtils.Core.Interop;

// Windows Core Audio (MMDevice API, endpoint volume) and the undocumented
// IPolicyConfig used to set the default endpoint. Declarations follow the
// Windows SDK headers mmdeviceapi.h, endpointvolume.h, audiopolicy.h,
// audioclient.h, propsys.h and functiondiscoverykeys_devpkey.h; vtable order matters, never reorder.
// Interfaces Windows calls back into return HRESULTs ([PreserveSig]) so an
// exception can never cross into Windows.

internal static partial class CoreAudio
{
    public static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    /// <summary>CPolicyConfigClient (EarTrumpet, SoundSwitch, AudioDeviceCmdlets use the same).</summary>
    public static readonly Guid CLSID_PolicyConfigClient = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");

    public static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    public static readonly Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    public const uint DEVICE_STATE_ACTIVE = 0x1;
    public const uint STGM_READ = 0x0;
    public const ushort VT_LPWSTR = 31;
    public const int E_NOTFOUND = unchecked((int)0x80070490);

    /// <summary>PKEY_Device_FriendlyName.</summary>
    public static readonly PropertyKey PKEY_Device_FriendlyName =
        new() { FormatId = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), PropertyId = 14 };

    public static readonly StrategyBasedComWrappers Wrappers = new();

    public enum EDataFlow { Render = 0, Capture = 1, All = 2 }

    public enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

    public enum AudioSessionState { Inactive = 0, Active = 1, Expired = 2 }

    [StructLayout(LayoutKind.Sequential)]
    public struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    /// <summary>PROPVARIANT: 8-byte header plus a 2-pointer union (16 bytes on x86, 24 on x64).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PropVariant
    {
        public ushort Type;
        public ushort Reserved1, Reserved2, Reserved3;
        public nint Value1;
        public nint Value2;
    }

    /// <summary>AUDIO_VOLUME_NOTIFICATION_DATA header (channel volumes follow; not read).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AudioVolumeNotificationData
    {
        public Guid EventContext;
        public int Muted;
        public float MasterVolume;
        public uint Channels;
    }

    /// <summary>Creates a COM object and wraps it; throws on failure.</summary>
    public static T Create<T>(Guid clsid) where T : class
    {
        var iid = typeof(T).GUID;
        Marshal.ThrowExceptionForHR(Native.CoCreateInstance(clsid, 0, Native.CLSCTX_ALL, iid, out var pointer));
        return Wrap<T>(pointer);
    }

    /// <summary>Wraps a raw interface pointer (taking over its reference) in a unique managed object.</summary>
    public static T Wrap<T>(nint pointer) where T : class
    {
        try { return (T)Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance); }
        finally { Marshal.Release(pointer); }
    }

    /// <summary>Releases the native object behind a wrapper now instead of at finalization.</summary>
    public static void Release(object? wrapper)
    {
        if (wrapper is ComObject com) com.FinalRelease();
    }
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
internal partial interface IMMDeviceEnumerator
{
    IMMDeviceCollection EnumAudioEndpoints(CoreAudio.EDataFlow dataFlow, uint stateMask);
    IMMDevice GetDefaultAudioEndpoint(CoreAudio.EDataFlow dataFlow, CoreAudio.ERole role);
    IMMDevice GetDevice(string id);
    void RegisterEndpointNotificationCallback(IMMNotificationClient client);
    void UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[GeneratedComInterface]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
internal partial interface IMMDeviceCollection
{
    uint GetCount();
    IMMDevice Item(uint index);
}

[GeneratedComInterface]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
internal partial interface IMMDevice
{
    /// <summary>Returns a raw interface pointer; wrap it with <see cref="CoreAudio.Wrap{T}"/>.</summary>
    nint Activate(in Guid iid, uint clsContext, nint activationParams);
    IPropertyStore OpenPropertyStore(uint access);
    /// <summary>CoTaskMem string; free with Marshal.FreeCoTaskMem.</summary>
    nint GetId();
    uint GetState();
}

[GeneratedComInterface]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
internal partial interface IPropertyStore
{
    uint GetCount();
    CoreAudio.PropertyKey GetAt(uint index);
    void GetValue(in CoreAudio.PropertyKey key, out CoreAudio.PropVariant value);
    void SetValue(in CoreAudio.PropertyKey key, in CoreAudio.PropVariant value);
    void Commit();
}

/// <summary>Device events; Windows calls these on its own threads.</summary>
[GeneratedComInterface]
[Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
internal partial interface IMMNotificationClient
{
    [PreserveSig] int OnDeviceStateChanged(nint deviceId, uint newState);
    [PreserveSig] int OnDeviceAdded(nint deviceId);
    [PreserveSig] int OnDeviceRemoved(nint deviceId);
    [PreserveSig] int OnDefaultDeviceChanged(CoreAudio.EDataFlow flow, CoreAudio.ERole role, nint defaultDeviceId);
    [PreserveSig] int OnPropertyValueChanged(nint deviceId, CoreAudio.PropertyKey key);
}

[GeneratedComInterface]
[Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
internal partial interface IAudioEndpointVolume
{
    void RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
    void UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
    uint GetChannelCount();
    void SetMasterVolumeLevel(float levelDb, in Guid eventContext);
    void SetMasterVolumeLevelScalar(float level, in Guid eventContext);
    float GetMasterVolumeLevel();
    float GetMasterVolumeLevelScalar();
    void SetChannelVolumeLevel(uint channel, float levelDb, in Guid eventContext);
    void SetChannelVolumeLevelScalar(uint channel, float level, in Guid eventContext);
    float GetChannelVolumeLevel(uint channel);
    float GetChannelVolumeLevelScalar(uint channel);
    void SetMute(int mute, in Guid eventContext);
    int GetMute();
    void GetVolumeStepInfo(out uint step, out uint stepCount);
    void VolumeStepUp(in Guid eventContext);
    void VolumeStepDown(in Guid eventContext);
    uint QueryHardwareSupport();
    void GetVolumeRange(out float minDb, out float maxDb, out float incrementDb);
}

/// <summary>Volume/mute changes on an endpoint; Windows calls this on its own threads.</summary>
[GeneratedComInterface]
[Guid("657804FA-D6AD-4496-8A60-352752AF4F89")]
internal partial interface IAudioEndpointVolumeCallback
{
    [PreserveSig] int OnNotify(nint data);
}

/// <summary>
/// Undocumented IPolicyConfig (Windows 7, 8 and 10 RS1 onwards; 11 included),
/// IID F8679F50-850A-41CF-9C72-430F290290C8, implemented by CPolicyConfigClient.
/// Only SetDefaultEndpoint is used; the methods before it keep the vtable aligned.
/// </summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
internal partial interface IPolicyConfig
{
    [PreserveSig] int GetMixFormat(string deviceId, out nint format);
    [PreserveSig] int GetDeviceFormat(string deviceId, int useDefault, out nint format);
    [PreserveSig] int ResetDeviceFormat(string deviceId);
    [PreserveSig] int SetDeviceFormat(string deviceId, nint endpointFormat, nint mixFormat);
    [PreserveSig] int GetProcessingPeriod(string deviceId, int useDefault, out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int SetProcessingPeriod(string deviceId, in long period);
    [PreserveSig] int GetShareMode(string deviceId, nint mode);
    [PreserveSig] int SetShareMode(string deviceId, nint mode);
    [PreserveSig] int GetPropertyValue(string deviceId, in CoreAudio.PropertyKey key, out CoreAudio.PropVariant value);
    [PreserveSig] int SetPropertyValue(string deviceId, in CoreAudio.PropertyKey key, in CoreAudio.PropVariant value);
    [PreserveSig] int SetDefaultEndpoint(string deviceId, CoreAudio.ERole role);
    [PreserveSig] int SetEndpointVisibility(string deviceId, int visible);
}

// --- Audio sessions (audiopolicy.h, audioclient.h) ------------------------
// Session controls and enumerators come back as raw pointers wrapped with
// CoreAudio.Wrap (unique wrappers), so releasing one never touches another
// wrapper of the same native session.

/// <summary>IAudioSessionManager2, declared flat (IAudioSessionManager's two methods first).</summary>
[GeneratedComInterface]
[Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
internal partial interface IAudioSessionManager2
{
    // IAudioSessionManager
    nint GetAudioSessionControl(in Guid sessionGuid, uint streamFlags);
    nint GetSimpleAudioVolume(in Guid sessionGuid, uint streamFlags);
    // IAudioSessionManager2
    /// <summary>Raw IAudioSessionEnumerator pointer; wrap it with <see cref="CoreAudio.Wrap{T}"/>.</summary>
    nint GetSessionEnumerator();
    void RegisterSessionNotification(IAudioSessionNotification notification);
    void UnregisterSessionNotification(IAudioSessionNotification notification);
    void RegisterDuckNotification(nint sessionId, nint notification);
    void UnregisterDuckNotification(nint notification);
}

[GeneratedComInterface]
[Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
internal partial interface IAudioSessionEnumerator
{
    int GetCount();
    /// <summary>Raw IAudioSessionControl pointer; wrap it with <see cref="CoreAudio.Wrap{T}"/>.</summary>
    nint GetSession(int index);
}

/// <summary>IAudioSessionControl2, declared flat (IAudioSessionControl's nine methods first).</summary>
[GeneratedComInterface]
[Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
internal partial interface IAudioSessionControl2
{
    // IAudioSessionControl
    CoreAudio.AudioSessionState GetState();
    nint GetDisplayName();
    void SetDisplayName(nint value, in Guid eventContext);
    nint GetIconPath();
    void SetIconPath(nint value, in Guid eventContext);
    Guid GetGroupingParam();
    void SetGroupingParam(in Guid param, in Guid eventContext);
    void RegisterAudioSessionNotification(IAudioSessionEvents events);
    void UnregisterAudioSessionNotification(IAudioSessionEvents events);
    // IAudioSessionControl2
    /// <summary>CoTaskMem string; free with Marshal.FreeCoTaskMem.</summary>
    nint GetSessionIdentifier();
    /// <summary>CoTaskMem string, unique per session; free with Marshal.FreeCoTaskMem.</summary>
    nint GetSessionInstanceIdentifier();
    uint GetProcessId();
    /// <summary>S_OK (0) for the system sounds session, S_FALSE (1) otherwise.</summary>
    [PreserveSig] int IsSystemSoundsSession();
    void SetDuckingPreference(int optOut);
}

[GeneratedComInterface]
[Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
internal partial interface ISimpleAudioVolume
{
    void SetMasterVolume(float level, in Guid eventContext);
    float GetMasterVolume();
    void SetMute(int mute, in Guid eventContext);
    int GetMute();
}

/// <summary>New sessions on a device; Windows calls this on its own threads.</summary>
[GeneratedComInterface]
[Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08")]
internal partial interface IAudioSessionNotification
{
    /// <summary>The session pointer is only borrowed for the call.</summary>
    [PreserveSig] int OnSessionCreated(nint newSession);
}

/// <summary>Events of one session; Windows calls these on its own threads.</summary>
[GeneratedComInterface]
[Guid("24918ACC-64B3-37C1-8CA9-74A66E9957A8")]
internal partial interface IAudioSessionEvents
{
    [PreserveSig] int OnDisplayNameChanged(nint newDisplayName, nint eventContext);
    [PreserveSig] int OnIconPathChanged(nint newIconPath, nint eventContext);
    [PreserveSig] int OnSimpleVolumeChanged(float newVolume, int newMute, nint eventContext);
    [PreserveSig] int OnChannelVolumeChanged(uint channelCount, nint newChannelVolumes, uint changedChannel, nint eventContext);
    [PreserveSig] int OnGroupingParamChanged(nint newGroupingParam, nint eventContext);
    [PreserveSig] int OnStateChanged(CoreAudio.AudioSessionState newState);
    [PreserveSig] int OnSessionDisconnected(int disconnectReason);
}

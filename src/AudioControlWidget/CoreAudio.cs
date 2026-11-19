using System.Runtime.InteropServices;

namespace AudioCtl.Interop;

public enum EDataFlow { Render = 0, Capture = 1, All = 2 }
public enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

[StructLayout(LayoutKind.Sequential)]
public struct PropertyKey
{
    public Guid fmtid;
    public uint pid;
    public PropertyKey(string guid, uint p) { fmtid = new Guid(guid); pid = p; }
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
public struct PropVariant
{
    [FieldOffset(0)] public ushort vt;
    [FieldOffset(8)] public IntPtr ptr;
    [FieldOffset(8)] public uint uintVal;
}

static class Ole
{
    [DllImport("ole32.dll")] public static extern int PropVariantClear(ref PropVariant pv);
}

static class PKey
{
    public static readonly PropertyKey DeviceDesc = new("a45c254e-df1c-4efd-8020-67d146a850e0", 2);
    public static readonly PropertyKey FriendlyName = new("a45c254e-df1c-4efd-8020-67d146a850e0", 14);
    public static readonly PropertyKey InterfaceName = new("026e516e-b814-414b-83cd-856d6fef4822", 2);
    public static readonly PropertyKey FormFactor = new("1da5d803-d492-4edd-8c23-e0c0ffee7f0e", 0);
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
class MMDeviceEnumeratorComObject { }

[ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
class PolicyConfigClient { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    IMMDeviceCollection EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask);
    IMMDevice GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role);
    IMMDevice GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id);
    void RegisterEndpointNotificationCallback(IMMNotificationClient client);
    void UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceCollection
{
    uint GetCount();
    IMMDevice Item(uint index);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    [return: MarshalAs(UnmanagedType.IUnknown)]
    object Activate(ref Guid iid, uint clsCtx, IntPtr activationParams);
    IPropertyStore OpenPropertyStore(uint access);
    [return: MarshalAs(UnmanagedType.LPWStr)]
    string GetId();
    uint GetState();
}

[ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPropertyStore
{
    uint GetCount();
    PropertyKey GetAt(uint index);
    [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioEndpointVolume
{
    void RegisterControlChangeNotify(IAudioEndpointVolumeCallback callback);
    void UnregisterControlChangeNotify(IAudioEndpointVolumeCallback callback);
    uint GetChannelCount();
    void SetMasterVolumeLevel(float levelDb, ref Guid context);
    void SetMasterVolumeLevelScalar(float level, ref Guid context);
    float GetMasterVolumeLevel();
    float GetMasterVolumeLevelScalar();
    void SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
    void SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
    float GetChannelVolumeLevel(uint channel);
    float GetChannelVolumeLevelScalar(uint channel);
    void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
    [return: MarshalAs(UnmanagedType.Bool)]
    bool GetMute();
}

[ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioEndpointVolumeCallback
{
    void OnNotify(IntPtr notifyData);
}

[ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMNotificationClient
{
    void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, uint newState);
    void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
    void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
    void OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string? id);
    void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
}

[ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPolicyConfig
{
    [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr format);
    [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int isDefault, IntPtr format);
    [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
    [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFormat, IntPtr mixFormat);
    [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int isDefault, IntPtr defaultPeriod, IntPtr minPeriod);
    [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr period);
    [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
    [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
    [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
    [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, ERole role);
    [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
}

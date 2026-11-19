using System.Runtime.InteropServices;

namespace RigTune.Core.Native;

internal static class CoreAudio
{
    public static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    public static readonly Guid IAudioClientIid = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

    public const uint DEVICE_STATE_ACTIVE = 0x1;
    public const uint CLSCTX_ALL = 0x17;
    public const uint STGM_READ = 0;
    public const ushort VT_LPWSTR = 31;

    public enum EDataFlow
    {
        Render = 0,
        Capture = 1,
        All = 2,
    }

    public enum ERole
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROPVARIANT
    {
        public ushort vt;
        public ushort reserved1;
        public ushort reserved2;
        public ushort reserved3;
        public IntPtr pointerValue;
        public IntPtr reserved4;
    }

    public static PROPERTYKEY FriendlyNameKey => new()
    {
        fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
        pid = 14,
    };

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int Item(uint index, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object anInterface);

        [PreserveSig]
        int OpenPropertyStore(uint stgmAccess, out IPropertyStore properties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out uint state);
    }

    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int GetAt(uint index, out PROPERTYKEY key);

        [PreserveSig]
        int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }

    /// <summary>Vtable order up to GetDevicePeriod; unused parameters are passed as pointers.</summary>
    [ComImport]
    [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioClient
    {
        [PreserveSig]
        int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format,
            IntPtr audioSessionGuid);

        [PreserveSig]
        int GetBufferSize(out uint numBufferFrames);

        [PreserveSig]
        int GetStreamLatency(out long latency);

        [PreserveSig]
        int GetCurrentPadding(out uint numPaddingFrames);

        [PreserveSig]
        int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);

        [PreserveSig]
        int GetMixFormat(out IntPtr deviceFormat);

        [PreserveSig]
        int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);
    }

    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(ref PROPVARIANT pvar);
}

using System.Runtime.InteropServices;

namespace RigTune.Core.Native;

/// <summary>
/// Minimal NVAPI DRS interop. NVAPI exports a single symbol (<c>nvapi_QueryInterface</c>)
/// that resolves every function by its published interface ID.
/// </summary>
internal static unsafe class NvApi
{
    public const int NVAPI_OK = 0;
    public const int NVAPI_SETTING_NOT_FOUND = -160;

    public const uint NVDRS_DWORD_TYPE = 0;
    public const uint NVDRS_CURRENT_PROFILE_LOCATION = 0;

    // Interface IDs from nvapi_interface.h.
    private const uint IdInitialize = 0x0150E828;
    private const uint IdDrsCreateSession = 0x0694D52E;
    private const uint IdDrsDestroySession = 0xDAD9CFF8;
    private const uint IdDrsLoadSettings = 0x375DBD6B;
    private const uint IdDrsSaveSettings = 0xFCBC7E14;
    private const uint IdDrsGetBaseProfile = 0xDA8466A0;
    private const uint IdDrsGetSetting = 0x73BF8338;
    private const uint IdDrsSetSetting = 0x577DD202;
    private const uint IdDrsDeleteProfileSetting = 0xE4A26362;

    /// <summary>NVDRS_SETTING_V1 (12320 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NVDRS_SETTING
    {
        public uint version;
        public fixed ushort settingName[2048];
        public uint settingId;
        public uint settingType;
        public uint settingLocation;
        public uint isCurrentPredefined;
        public uint isPredefinedValid;
        public fixed byte predefinedValue[4100];
        public fixed byte currentValue[4100];
    }

    public static uint SettingVersion1 => (uint)sizeof(NVDRS_SETTING) | (1u << 16);

    private static readonly object InitLock = new();
    private static bool _initialized;
    private static bool _available;

    private static delegate* unmanaged[Cdecl]<IntPtr*, int> _createSession;
    private static delegate* unmanaged[Cdecl]<IntPtr, int> _destroySession;
    private static delegate* unmanaged[Cdecl]<IntPtr, int> _loadSettings;
    private static delegate* unmanaged[Cdecl]<IntPtr, int> _saveSettings;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr*, int> _getBaseProfile;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, NVDRS_SETTING*, int> _getSetting;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, NVDRS_SETTING*, int> _setSetting;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, int> _deleteProfileSetting;

    public static bool EnsureInitialized()
    {
        lock (InitLock)
        {
            if (_initialized)
            {
                return _available;
            }

            _initialized = true;
            if (!OperatingSystem.IsWindows() || !NativeLibrary.TryLoad("nvapi64.dll", out IntPtr lib) ||
                !NativeLibrary.TryGetExport(lib, "nvapi_QueryInterface", out IntPtr queryExport))
            {
                return _available = false;
            }

            var query = (delegate* unmanaged[Cdecl]<uint, IntPtr>)queryExport;
            IntPtr init = query(IdInitialize);
            if (init == IntPtr.Zero || ((delegate* unmanaged[Cdecl]<int>)init)() != NVAPI_OK)
            {
                return _available = false;
            }

            _createSession = (delegate* unmanaged[Cdecl]<IntPtr*, int>)query(IdDrsCreateSession);
            _destroySession = (delegate* unmanaged[Cdecl]<IntPtr, int>)query(IdDrsDestroySession);
            _loadSettings = (delegate* unmanaged[Cdecl]<IntPtr, int>)query(IdDrsLoadSettings);
            _saveSettings = (delegate* unmanaged[Cdecl]<IntPtr, int>)query(IdDrsSaveSettings);
            _getBaseProfile = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr*, int>)query(IdDrsGetBaseProfile);
            _getSetting = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, NVDRS_SETTING*, int>)query(IdDrsGetSetting);
            _setSetting = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, NVDRS_SETTING*, int>)query(IdDrsSetSetting);
            _deleteProfileSetting = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, int>)query(IdDrsDeleteProfileSetting);

            _available = _createSession != null && _destroySession != null && _loadSettings != null &&
                         _saveSettings != null && _getBaseProfile != null && _getSetting != null &&
                         _setSetting != null && _deleteProfileSetting != null;
            return _available;
        }
    }

    public static int CreateSession(out IntPtr session)
    {
        IntPtr s;
        int status = _createSession(&s);
        session = s;
        return status;
    }

    public static int DestroySession(IntPtr session) => _destroySession(session);
    public static int LoadSettings(IntPtr session) => _loadSettings(session);
    public static int SaveSettings(IntPtr session) => _saveSettings(session);

    public static int GetBaseProfile(IntPtr session, out IntPtr profile)
    {
        IntPtr p;
        int status = _getBaseProfile(session, &p);
        profile = p;
        return status;
    }

    public static int GetSetting(IntPtr session, IntPtr profile, uint settingId, NVDRS_SETTING* setting) =>
        _getSetting(session, profile, settingId, setting);

    public static int SetSetting(IntPtr session, IntPtr profile, NVDRS_SETTING* setting) =>
        _setSetting(session, profile, setting);

    public static int DeleteProfileSetting(IntPtr session, IntPtr profile, uint settingId) =>
        _deleteProfileSetting(session, profile, settingId);
}

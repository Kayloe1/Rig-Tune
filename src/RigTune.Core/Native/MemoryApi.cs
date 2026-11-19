using System.Runtime.InteropServices;

namespace RigTune.Core.Native;

internal static class MemoryApi
{
    public const int SystemMemoryListInformation = 80;
    public const int MemoryPurgeStandbyList = 4;
    public const uint TOKEN_ADJUST_PRIVILEGES = 0x20;
    public const uint TOKEN_QUERY = 0x8;
    public const uint SE_PRIVILEGE_ENABLED = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    [DllImport("ntdll.dll")]
    public static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

    [DllImport("ntdll.dll")]
    public static extern int NtQuerySystemInformation(int infoClass, byte[] info, int length, out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool LookupPrivilegeValue(string? systemName, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AdjustTokenPrivileges(IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TOKEN_PRIVILEGES newState, int bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", EntryPoint = "K32EmptyWorkingSet", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EmptyWorkingSet(IntPtr process);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DnsFlushResolverCache();

    public static bool EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr token))
        {
            return false;
        }

        try
        {
            if (!LookupPrivilegeValue(null, name, out LUID luid))
            {
                return false;
            }

            var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            return AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero) &&
                   Marshal.GetLastWin32Error() == 0;
        }
        finally
        {
            CloseHandle(token);
        }
    }
}

using System.Runtime.InteropServices;

namespace RigTune.Core.Native;

internal static class SrClient
{
    public const int BEGIN_SYSTEM_CHANGE = 100;
    public const int END_SYSTEM_CHANGE = 101;
    public const int MAX_DESC_W = 256;

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
    public struct RESTOREPOINTINFOW
    {
        public int dwEventType;
        public int dwRestorePtType;
        public long llSequenceNumber;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_DESC_W)]
        public string szDescription;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct STATEMGRSTATUS
    {
        public int nStatus;
        public long llSequenceNumber;
    }

    [DllImport("srclient.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SRSetRestorePointW(ref RESTOREPOINTINFOW restorePtSpec, out STATEMGRSTATUS smgrStatus);
}

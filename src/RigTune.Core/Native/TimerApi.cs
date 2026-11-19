using System.Runtime.InteropServices;

namespace RigTune.Core.Native;

internal static class TimerApi
{
    /// <summary>All values in 100-ns units.</summary>
    [DllImport("ntdll.dll")]
    public static extern int NtQueryTimerResolution(out uint minimumResolution, out uint maximumResolution,
        out uint currentResolution);

    [DllImport("ntdll.dll")]
    public static extern int NtSetTimerResolution(uint desiredResolution, [MarshalAs(UnmanagedType.U1)] bool setResolution,
        out uint currentResolution);

    [DllImport("kernel32.dll")]
    public static extern void Sleep(uint milliseconds);
}

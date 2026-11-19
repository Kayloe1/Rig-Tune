using System.ComponentModel;
using System.Runtime.InteropServices;
using RigTune.Core.Native;
using RigTune.Core.Platform;

namespace RigTune.Core.Runners;

/// <summary>powrprof.dll implementation of <see cref="IPowerAccess"/>.</summary>
public sealed class PowerRunner : IPowerAccess
{
    public Guid GetActiveScheme()
    {
        Check(PowrProf.PowerGetActiveScheme(IntPtr.Zero, out IntPtr ptr));
        try
        {
            return Marshal.PtrToStructure<Guid>(ptr);
        }
        finally
        {
            PowrProf.LocalFree(ptr);
        }
    }

    public uint ReadAcValue(Guid scheme, Guid subgroup, Guid setting)
    {
        Check(PowrProf.PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out uint value));
        return value;
    }

    public uint ReadDcValue(Guid scheme, Guid subgroup, Guid setting)
    {
        Check(PowrProf.PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out uint value));
        return value;
    }

    public void WriteAcValue(Guid scheme, Guid subgroup, Guid setting, uint value) =>
        Check(PowrProf.PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, value));

    public void WriteDcValue(Guid scheme, Guid subgroup, Guid setting, uint value) =>
        Check(PowrProf.PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, value));

    public void ApplyScheme(Guid scheme)
    {
        if (GetActiveScheme() == scheme)
        {
            Check(PowrProf.PowerSetActiveScheme(IntPtr.Zero, ref scheme));
        }
    }

    private static void Check(uint win32Error)
    {
        if (win32Error != 0)
        {
            throw new Win32Exception(unchecked((int)win32Error));
        }
    }
}

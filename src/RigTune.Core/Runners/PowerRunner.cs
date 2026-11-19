using System.ComponentModel;
using System.Runtime.InteropServices;
using RigTune.Core.Models;
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

    /// <summary>Well-known power GUIDs (SPEC §3.3.4).</summary>
    public static class Guids
    {
        public static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");
        public static readonly Guid HeteroPolicy = new("7f2f5cfa-f10c-4823-b5e1-e93ae85f46b5");
        public static readonly Guid SchedPolicy = new("93b8b6dc-0698-4d1c-9ee4-06ed4e92f5eb");
        public static readonly Guid ShortSchedPolicy = new("bae08b81-2d5e-4688-ad6a-132433566544");
        public static readonly Guid CpMinCores = new("0cc5b647-c1df-4637-891a-dec35c318583");
        public static readonly Guid SubPciExpress = new("501a4d13-42af-4429-9fd1-a8218c268e20");
        public static readonly Guid Aspm = new("ee12f906-d277-404b-b6da-e5fa1a576df5");
        public static readonly Guid SubUsb = new("2a737441-1930-4402-8d77-b2bebba308a3");
        public static readonly Guid UsbSelectiveSuspend = new("48e6b7a6-50f5-4782-a5d4-53bb8f07e226");
    }

    /// <summary>Registry value that holds a setting's visibility (1 = hidden, 2 = shown), as <c>powercfg -attributes</c> edits it.</summary>
    public static RegistryTarget AttributesTarget(Guid subgroup, Guid setting) => new(
        RegistryHiveName.HKLM,
        RegistryViewName.Registry64,
        $@"SYSTEM\CurrentControlSet\Control\Power\PowerSettings\{subgroup}\{setting}",
        "Attributes");

    /// <summary>Expert view: every setting in a subgroup with live AC/DC values and visibility.</summary>
    public IReadOnlyList<PowerSettingInfo> EnumerateSubgroup(Guid subgroup, Guid? scheme = null)
    {
        Guid schemeGuid = scheme ?? GetActiveScheme();
        var settings = new List<PowerSettingInfo>();
        for (uint i = 0; ; i++)
        {
            uint size = (uint)Marshal.SizeOf<Guid>();
            Guid sub = subgroup;
            if (PowrProfEnum.PowerEnumerate(IntPtr.Zero, ref schemeGuid, ref sub, PowrProfEnum.ACCESS_INDIVIDUAL_SETTING, i,
                    out Guid setting, ref size) != 0)
            {
                break;
            }

            uint ac = 0, dc = 0;
            try
            {
                ac = ReadAcValue(schemeGuid, subgroup, setting);
                dc = ReadDcValue(schemeGuid, subgroup, setting);
            }
            catch (Win32Exception)
            {
            }

            uint attributes = PowrProfEnum.PowerReadSettingAttributes(ref sub, ref setting);
            settings.Add(new PowerSettingInfo(subgroup, setting, ReadFriendlyName(schemeGuid, subgroup, setting), ac, dc,
                (attributes & 1) != 0));
        }

        return settings;
    }

    /// <summary>Desktop sanity checks for the power plan editor (SYSTEM_POWER_CAPABILITIES).</summary>
    public static PowerHardwareChecks GetHardwareChecks()
    {
        var buffer = new byte[128];
        if (PowrProfInfo.CallNtPowerInformation(PowrProfInfo.SystemPowerCapabilities, IntPtr.Zero, 0, buffer,
                (uint)buffer.Length) != 0)
        {
            return new PowerHardwareChecks(null, null, null, null);
        }

        // BOOLEAN offsets: SystemS3 = 5, HiberFilePresent = 8, AoAc = 20, SystemBatteriesPresent = 30.
        return new PowerHardwareChecks(buffer[30] != 0, buffer[20] != 0, buffer[8] != 0, buffer[5] != 0);
    }

    private static string ReadFriendlyName(Guid scheme, Guid subgroup, Guid setting)
    {
        uint size = 0;
        PowrProfEnum.PowerReadFriendlyName(IntPtr.Zero, ref scheme, ref subgroup, ref setting, IntPtr.Zero, ref size);
        if (size == 0)
        {
            return setting.ToString();
        }

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            return PowrProfEnum.PowerReadFriendlyName(IntPtr.Zero, ref scheme, ref subgroup, ref setting, buffer, ref size) == 0
                ? Marshal.PtrToStringUni(buffer) ?? setting.ToString()
                : setting.ToString();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
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

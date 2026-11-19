using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using RigTune.Core.Models;
using RigTune.Core.Native;
using RigTune.Core.Platform;

namespace RigTune.Core.Runners;

/// <summary>
/// SetupAPI device enumeration and enable/disable (Device Manager equivalent). As an
/// <see cref="IStateRunner"/> ("device") every disable is snapshotted and restorable.
/// </summary>
public sealed class DeviceManagerRunner : IStateRunner
{
    public string Kind => "device";

    public IReadOnlyList<DeviceInfo> EnumerateDevices(string? enumerator = null)
    {
        var devices = new List<DeviceInfo>();
        ForEachDevice(enumerator, (set, data) =>
        {
            devices.Add(ReadDevice(set, ref data));
            return false;
        });
        return devices;
    }

    /// <summary>Present devices whose name matches one of the catalog rules.</summary>
    public IReadOnlyList<DeviceCandidate> FindCandidates(IEnumerable<DeviceRule> rules)
    {
        List<DeviceRule> list = rules.ToList();
        return EnumerateDevices()
            .Select(d => (Device: d, Rule: list.FirstOrDefault(r => d.Name.Contains(r.Match, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.Rule is not null)
            .Select(x => new DeviceCandidate(x.Device, x.Rule!.Rating, x.Rule.Note))
            .OrderBy(c => c.Device.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public DeviceInfo? Find(string instanceId)
    {
        DeviceInfo? found = null;
        ForEachDevice(null, (set, data) =>
        {
            if (string.Equals(GetInstanceId(set, ref data), instanceId, StringComparison.OrdinalIgnoreCase))
            {
                found = ReadDevice(set, ref data);
                return true;
            }

            return false;
        });
        return found;
    }

    public string? ReadState(string target) => Find(target) switch
    {
        null => null,
        { IsDisabled: true } => "Disabled",
        _ => "Enabled",
    };

    public void WriteState(string target, string state) =>
        ChangeState(target, string.Equals(state, "Disabled", StringComparison.OrdinalIgnoreCase)
            ? SetupApi.DICS_DISABLE
            : SetupApi.DICS_ENABLE);

    public string Describe(string target) => $"Device: {Find(target)?.Name ?? target}";

    /// <summary>Restarts a device so new driver parameters (MSI, NIC properties) take effect.</summary>
    public void Restart(string instanceId) => ChangeState(instanceId, SetupApi.DICS_PROPCHANGE);

    private static void ChangeState(string instanceId, uint stateChange)
    {
        bool done = false;
        ForEachDevice(null, (set, data) =>
        {
            if (!string.Equals(GetInstanceId(set, ref data), instanceId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var parameters = new SetupApi.SP_PROPCHANGE_PARAMS
            {
                ClassInstallHeader = new SetupApi.SP_CLASSINSTALL_HEADER
                {
                    cbSize = (uint)Marshal.SizeOf<SetupApi.SP_CLASSINSTALL_HEADER>(),
                    InstallFunction = SetupApi.DIF_PROPERTYCHANGE,
                },
                StateChange = stateChange,
                Scope = SetupApi.DICS_FLAG_GLOBAL,
                HwProfile = 0,
            };

            if (!SetupApi.SetupDiSetClassInstallParams(set, ref data, ref parameters,
                    Marshal.SizeOf<SetupApi.SP_PROPCHANGE_PARAMS>()) ||
                !SetupApi.SetupDiCallClassInstaller(SetupApi.DIF_PROPERTYCHANGE, set, ref data))
            {
                throw new Win32Exception();
            }

            done = true;
            return true;
        });

        if (!done)
        {
            throw new InvalidOperationException($"Device {instanceId} not found.");
        }
    }

    private static void ForEachDevice(string? enumerator, Func<IntPtr, SetupApi.SP_DEVINFO_DATA, bool> visit)
    {
        IntPtr set = SetupApi.SetupDiGetClassDevs(IntPtr.Zero, enumerator, IntPtr.Zero,
            SetupApi.DIGCF_ALLCLASSES | SetupApi.DIGCF_PRESENT);
        if (set == SetupApi.InvalidHandle)
        {
            throw new Win32Exception();
        }

        try
        {
            var data = new SetupApi.SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SetupApi.SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupApi.SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                if (visit(set, data))
                {
                    return;
                }
            }
        }
        finally
        {
            SetupApi.SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static DeviceInfo ReadDevice(IntPtr set, ref SetupApi.SP_DEVINFO_DATA data)
    {
        string instanceId = GetInstanceId(set, ref data);
        string name = GetString(set, ref data, SetupApi.SPDRP_FRIENDLYNAME)
                      ?? GetString(set, ref data, SetupApi.SPDRP_DEVICEDESC)
                      ?? instanceId;
        bool disabled = false, problem = false;
        if (SetupApi.CM_Get_DevNode_Status(out uint status, out uint problemNumber, data.DevInst, 0) == SetupApi.CR_SUCCESS)
        {
            problem = (status & SetupApi.DN_HAS_PROBLEM) != 0;
            disabled = problem && problemNumber == SetupApi.CM_PROB_DISABLED;
        }

        return new DeviceInfo(
            instanceId,
            name,
            GetString(set, ref data, SetupApi.SPDRP_CLASS) ?? string.Empty,
            data.ClassGuid,
            GetString(set, ref data, SetupApi.SPDRP_ENUMERATOR_NAME) ?? string.Empty,
            GetString(set, ref data, SetupApi.SPDRP_DRIVER),
            GetString(set, ref data, SetupApi.SPDRP_MFG),
            GetMultiString(set, ref data, SetupApi.SPDRP_HARDWAREID),
            disabled,
            problem);
    }

    private static string GetInstanceId(IntPtr set, ref SetupApi.SP_DEVINFO_DATA data)
    {
        var sb = new StringBuilder(512);
        return SetupApi.SetupDiGetDeviceInstanceId(set, ref data, sb, sb.Capacity, out _) ? sb.ToString() : string.Empty;
    }

    private static byte[]? GetBytes(IntPtr set, ref SetupApi.SP_DEVINFO_DATA data, uint property)
    {
        SetupApi.SetupDiGetDeviceRegistryProperty(set, ref data, property, out _, null, 0, out uint required);
        if (required == 0)
        {
            return null;
        }

        var buffer = new byte[required];
        return SetupApi.SetupDiGetDeviceRegistryProperty(set, ref data, property, out _, buffer, required, out _)
            ? buffer
            : null;
    }

    private static string? GetString(IntPtr set, ref SetupApi.SP_DEVINFO_DATA data, uint property)
    {
        byte[]? bytes = GetBytes(set, ref data, property);
        return bytes is null ? null : Encoding.Unicode.GetString(bytes).TrimEnd('\0');
    }

    private static IReadOnlyList<string> GetMultiString(IntPtr set, ref SetupApi.SP_DEVINFO_DATA data, uint property)
    {
        byte[]? bytes = GetBytes(set, ref data, property);
        return bytes is null
            ? Array.Empty<string>()
            : Encoding.Unicode.GetString(bytes).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }
}

using RigTune.Core.Models;
using RigTune.Core.Platform;

namespace RigTune.Core.Runners;

/// <summary>
/// PCI device enumeration (setupapi.dll) plus the registry locations that control MSI mode,
/// device priority and interrupt affinity. All writes go through <see cref="Services.SnapshotService"/>
/// registry snapshots, so they revert exactly.
/// </summary>
public sealed class PciRunner
{
    public const string InterruptManagementPath = @"Device Parameters\Interrupt Management";

    /// <summary>IrqPolicySpecifiedProcessors.</summary>
    public const uint IrqPolicySpecifiedProcessors = 4;

    private readonly DeviceManagerRunner _devices;
    private readonly IRegistryAccess _registry;

    public PciRunner(DeviceManagerRunner devices, IRegistryAccess registry)
    {
        _devices = devices;
        _registry = registry;
    }

    public IReadOnlyList<PciDeviceInfo> EnumeratePciDevices(InterruptDeviceCategory? filter = null) =>
        _devices.EnumerateDevices("PCI")
            .Select(d =>
            {
                InterruptDeviceCategory category = Categorize(d);
                return new PciDeviceInfo(
                    d,
                    category,
                    ReadDword(MsiTarget(d.InstanceId)) is { } msi ? msi != 0 : null,
                    (int?)ReadDword(AffinityTarget(d.InstanceId, "DevicePolicy")),
                    ReadAffinityMask(d.InstanceId),
                    (int?)ReadDword(AffinityTarget(d.InstanceId, "DevicePriority")));
            })
            .Where(p => filter is null || p.Category == filter)
            .OrderBy(p => p.Category)
            .ThenBy(p => p.Device.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static InterruptDeviceCategory Categorize(DeviceInfo device)
    {
        if (device.Class.Equals("Display", StringComparison.OrdinalIgnoreCase))
        {
            return InterruptDeviceCategory.Gpu;
        }

        if (device.Class.Equals("Net", StringComparison.OrdinalIgnoreCase))
        {
            return InterruptDeviceCategory.Network;
        }

        if (device.Class.Equals("USB", StringComparison.OrdinalIgnoreCase) ||
            device.Name.Contains("xHCI", StringComparison.OrdinalIgnoreCase) ||
            device.Name.Contains("USB", StringComparison.OrdinalIgnoreCase))
        {
            return InterruptDeviceCategory.Usb;
        }

        if (device.Class.Equals("MEDIA", StringComparison.OrdinalIgnoreCase) ||
            device.Name.Contains("Audio", StringComparison.OrdinalIgnoreCase))
        {
            return InterruptDeviceCategory.Audio;
        }

        return InterruptDeviceCategory.Other;
    }

    public static InterruptDeviceCategory? ParseCategory(string? deviceClass) => deviceClass?.Trim().ToUpperInvariant() switch
    {
        "DISPLAY" or "GPU" => InterruptDeviceCategory.Gpu,
        "USB" => InterruptDeviceCategory.Usb,
        "NET" or "NETWORK" => InterruptDeviceCategory.Network,
        "AUDIO" or "MEDIA" => InterruptDeviceCategory.Audio,
        _ => null,
    };

    public static string EnumKey(string instanceId) => $@"SYSTEM\CurrentControlSet\Enum\{instanceId}";

    public static RegistryTarget MsiTarget(string instanceId) => new(RegistryHiveName.HKLM, RegistryViewName.Registry64,
        $@"{EnumKey(instanceId)}\{InterruptManagementPath}\MessageSignaledInterruptProperties", "MSISupported");

    public static RegistryTarget AffinityTarget(string instanceId, string valueName) => new(RegistryHiveName.HKLM,
        RegistryViewName.Registry64, $@"{EnumKey(instanceId)}\{InterruptManagementPath}\Affinity Policy", valueName);

    /// <summary>KAFFINITY as REG_BINARY (8 bytes, little-endian).</summary>
    public static RegistryValueData AffinityMaskValue(ulong mask) => RegistryValueData.FromBinary(BitConverter.GetBytes(mask));

    private ulong? ReadAffinityMask(string instanceId)
    {
        RegistryValueData? data = _registry.ReadValue(AffinityTarget(instanceId, "AssignmentSetOverride"));
        byte[] bytes = data?.GetBytes() ?? Array.Empty<byte>();
        if (bytes.Length == 0)
        {
            return null;
        }

        var padded = new byte[8];
        Array.Copy(bytes, padded, Math.Min(8, bytes.Length));
        return BitConverter.ToUInt64(padded, 0);
    }

    private uint? ReadDword(RegistryTarget target) =>
        _registry.ReadValue(target) is { Kind: Microsoft.Win32.RegistryValueKind.DWord } v ? v.DWord : null;
}

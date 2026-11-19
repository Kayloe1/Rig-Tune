using RigTune.Core.Models;

namespace RigTune.Core.Services.SystemCare;

/// <summary>Healthy-state expectation for a service the repair scanner watches.</summary>
public sealed record ServiceExpectation(string ServiceName, string DisplayName, ServiceStartType DefaultStartType, bool MustBeRunning);

/// <summary>
/// Pure, status-first decision logic for Repair &amp; Recovery: each rule inspects live state and only
/// proposes a fix when something is actually broken. Never proposes disk/filesystem repair tools.
/// </summary>
public static class RepairRules
{
    public static readonly IReadOnlyList<ServiceExpectation> WatchedServices = new[]
    {
        new ServiceExpectation("wuauserv", "Windows Update", ServiceStartType.Manual, false),
        new ServiceExpectation("Spooler", "Print Spooler", ServiceStartType.Automatic, true),
        new ServiceExpectation("BITS", "Background Intelligent Transfer Service", ServiceStartType.Manual, false),
    };

    public static readonly ServiceExpectation SearchIndexing =
        new("WSearch", "Windows Search indexing", ServiceStartType.AutomaticDelayed, true);

    /// <summary>Device categories watched for disabled/problem devices: label → Device Manager classes.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> DeviceCategories = new Dictionary<string, string[]>
    {
        ["Bluetooth"] = new[] { "Bluetooth" },
        ["Audio"] = new[] { "MEDIA", "AudioEndpoint" },
        ["Network"] = new[] { "Net" },
        ["USB"] = new[] { "USB" },
    };

    private static readonly HashSet<string> VirtualEnumerators = new(StringComparer.OrdinalIgnoreCase) { "ROOT", "SWD", "VMBUS" };

    public static string? CategoryOf(DeviceInfo device)
    {
        if (VirtualEnumerators.Contains(device.Enumerator))
        {
            return null; // virtual adapters (VPN, Hyper-V switches) are not hardware faults
        }

        foreach ((string category, string[] classes) in DeviceCategories)
        {
            if (classes.Any(c => string.Equals(c, device.Class, StringComparison.OrdinalIgnoreCase)))
            {
                return category;
            }
        }

        return null;
    }

    public static RepairCheck EvaluateDevice(DeviceInfo device, string category)
    {
        string id = $"device|{device.InstanceId}";
        if (device.IsDisabled)
        {
            return new RepairCheck(id, category, device.Name, RepairStatus.Broken, "Device is disabled in Device Manager.",
                RepairFixKind.EnableDevice, "Enable the device", device.InstanceId);
        }

        if (device.HasProblem)
        {
            return new RepairCheck(id, category, device.Name, RepairStatus.Broken, "Device reports a problem (driver did not start cleanly).",
                RepairFixKind.RestartDevice, "Restart the device", device.InstanceId);
        }

        return new RepairCheck(id, category, device.Name, RepairStatus.Healthy, "Working normally.");
    }

    public static RepairCheck EvaluateService(ServiceExpectation expected, ServiceState? state, string category = "Services")
    {
        string id = $"service|{expected.ServiceName}";
        if (state is null)
        {
            return new RepairCheck(id, category, expected.DisplayName, RepairStatus.NotApplicable, "Service is not installed.");
        }

        if (state.StartType == ServiceStartType.Disabled)
        {
            return new RepairCheck(id, category, expected.DisplayName, RepairStatus.Broken,
                $"Startup type is Disabled (Windows default: {expected.DefaultStartType}).",
                RepairFixKind.SetServiceStartType, $"Restore startup type to {expected.DefaultStartType}{(expected.MustBeRunning ? " and start it" : string.Empty)}",
                expected.ServiceName, expected.DefaultStartType.ToString());
        }

        if (state.IsPending)
        {
            return new RepairCheck(id, category, expected.DisplayName, RepairStatus.Broken, "Service is stuck starting or stopping.",
                RepairFixKind.RestartService, "Restart the service", expected.ServiceName);
        }

        if (expected.MustBeRunning && !state.IsRunning)
        {
            return new RepairCheck(id, category, expected.DisplayName, RepairStatus.Broken,
                $"Startup type {state.StartType}, but the service is stopped.",
                RepairFixKind.StartService, "Start the service", expected.ServiceName);
        }

        return new RepairCheck(id, category, expected.DisplayName, RepairStatus.Healthy,
            $"{state.StartType}, {(state.IsRunning ? "running" : "stopped (starts on demand)")}.");
    }

    /// <summary><c>HKLM\SOFTWARE\Policies\Microsoft\Windows\StorageSense\AllowStorageSenseGlobal</c> = 0 blocks Storage Sense.</summary>
    public static RepairCheck EvaluateStorageSensePolicy(RegistryValueData? allowStorageSenseGlobal, RegistryTarget target)
    {
        const string id = "policy|storagesense";
        return allowStorageSenseGlobal?.DWord == 0
            ? new RepairCheck(id, "Policies", "Storage Sense", RepairStatus.Broken, "Blocked by policy (AllowStorageSenseGlobal = 0).",
                RepairFixKind.DeleteRegistryValue, "Remove the blocking policy value", target.Display)
            : new RepairCheck(id, "Policies", "Storage Sense", RepairStatus.Healthy,
                allowStorageSenseGlobal is null ? "Not restricted by policy." : "Allowed by policy.");
    }

    /// <summary>
    /// Hyper-V / WSL2 consistency: WSL2 and Virtual Machine Platform need the hypervisor to launch at boot.
    /// </summary>
    public static RepairCheck EvaluateHypervisor(string? hypervisorLaunchType, string? virtualMachinePlatform, string? wsl)
    {
        const string id = "policy|hyperv";
        bool vmpOn = string.Equals(virtualMachinePlatform, "Enabled", StringComparison.OrdinalIgnoreCase);
        bool wslOn = string.Equals(wsl, "Enabled", StringComparison.OrdinalIgnoreCase);
        if (!vmpOn && !wslOn)
        {
            return new RepairCheck(id, "Policies", "Hyper-V / WSL2", RepairStatus.NotApplicable,
                "WSL and Virtual Machine Platform are not enabled.");
        }

        if (string.Equals(hypervisorLaunchType, "Off", StringComparison.OrdinalIgnoreCase))
        {
            return new RepairCheck(id, "Policies", "Hyper-V / WSL2", RepairStatus.Broken,
                "The boot hypervisor is turned off (hypervisorlaunchtype Off), so WSL2 cannot start.",
                RepairFixKind.SetBootHypervisor, "Set hypervisorlaunchtype to Auto", "hypervisorlaunchtype", "Auto",
                RequiresRestart: true);
        }

        if (wslOn && !vmpOn)
        {
            return new RepairCheck(id, "Policies", "Hyper-V / WSL2", RepairStatus.Broken,
                "WSL is enabled but the Virtual Machine Platform feature is off (WSL2 distros will not start).",
                RepairFixKind.EnableFeature, "Enable Virtual Machine Platform", "VirtualMachinePlatform", "Enabled",
                RequiresRestart: true);
        }

        return new RepairCheck(id, "Policies", "Hyper-V / WSL2", RepairStatus.Healthy, "Hypervisor launches at boot; WSL2 can run.");
    }

    /// <summary>
    /// If RigTune itself caused the "broken" state (an active snapshot), the right fix is to revert that
    /// tweak's snapshot — restoring the exact original — instead of guessing a default.
    /// </summary>
    public static RepairCheck PreferSnapshotRevert(RepairCheck check, SnapshotEntry? activeSnapshot)
    {
        if (check.Status != RepairStatus.Broken || activeSnapshot is null)
        {
            return check;
        }

        return check with
        {
            Detail = $"{check.Detail} Changed by RigTune tweak \"{activeSnapshot.TweakId}\".",
            FixKind = RepairFixKind.RevertSnapshot,
            FixDescription = "Revert RigTune's change (restores the exact original)",
            SnapshotId = activeSnapshot.Id,
        };
    }
}

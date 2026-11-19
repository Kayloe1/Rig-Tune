using RigTune.Core.Models;
using RigTune.Core.Platform;
using RigTune.Core.Runners;

namespace RigTune.Core.Services.SystemCare;

/// <summary>
/// Status-first Repair &amp; Recovery: scans devices (Bluetooth, audio, network, USB), stalled services
/// (Windows Update, Print Spooler, BITS) and flipped policies (Search indexing, Storage Sense, Hyper-V),
/// and fixes only items that are broken. Every fix is snapshotted where possible and audit-logged.
/// Deliberately never runs chkdsk / sfc / DISM repair.
/// </summary>
public sealed class RepairScanner
{
    public const string TweakId = "systemcare.repair";

    public static readonly RegistryTarget StorageSensePolicy = new(RegistryHiveName.HKLM, RegistryViewName.Registry64,
        @"SOFTWARE\Policies\Microsoft\Windows\StorageSense", "AllowStorageSenseGlobal");

    private static readonly TimeSpan ServiceTimeout = TimeSpan.FromSeconds(30);

    private readonly DeviceManagerRunner _devices;
    private readonly IServiceAccess _services;
    private readonly IRegistryAccess _registry;
    private readonly DismRunner _dism;
    private readonly BcdStateRunner _bcd;
    private readonly SnapshotService _snapshots;
    private readonly AuditLogService _audit;

    public RepairScanner(DeviceManagerRunner devices, IServiceAccess services, IRegistryAccess registry, DismRunner dism,
        BcdStateRunner bcd, SnapshotService snapshots, AuditLogService audit)
    {
        _devices = devices;
        _services = services;
        _registry = registry;
        _dism = dism;
        _bcd = bcd;
        _snapshots = snapshots;
        _audit = audit;
    }

    public async Task<IReadOnlyList<RepairCheck>> ScanAsync(CancellationToken cancellationToken = default)
    {
        var checks = new List<RepairCheck>();

        IReadOnlyList<DeviceInfo> devices = await Task.Run(() => _devices.EnumerateDevices(), cancellationToken).ConfigureAwait(false);
        foreach (DeviceInfo device in devices)
        {
            if (RepairRules.CategoryOf(device) is { } category)
            {
                RepairCheck check = RepairRules.EvaluateDevice(device, category);
                checks.Add(await WithSnapshot(check, $"state|device|{device.InstanceId.ToUpperInvariant()}", cancellationToken).ConfigureAwait(false));
            }
        }

        foreach (ServiceExpectation expected in RepairRules.WatchedServices.Append(RepairRules.SearchIndexing))
        {
            ServiceState? state = await Task.Run(() => _services.Query(expected.ServiceName), cancellationToken).ConfigureAwait(false);
            RepairCheck check = RepairRules.EvaluateService(expected, state,
                expected == RepairRules.SearchIndexing ? "Policies" : "Services");
            checks.Add(await WithSnapshot(check, $"svc|{expected.ServiceName.ToUpperInvariant()}", cancellationToken).ConfigureAwait(false));
        }

        RepairCheck storage = RepairRules.EvaluateStorageSensePolicy(_registry.ReadValue(StorageSensePolicy), StorageSensePolicy);
        checks.Add(await WithSnapshot(storage, StorageSensePolicy.IdentityKey, cancellationToken).ConfigureAwait(false));

        RepairCheck hyperv = await Task.Run(() =>
        {
            try
            {
                IReadOnlyDictionary<string, string> features = _dism.QueryFeatures();
                return RepairRules.EvaluateHypervisor(_bcd.ReadState("hypervisorlaunchtype"),
                    features.GetValueOrDefault("VirtualMachinePlatform"),
                    features.GetValueOrDefault("Microsoft-Windows-Subsystem-Linux"));
            }
            catch (Exception ex) when (ex is System.Management.ManagementException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return new RepairCheck("policy|hyperv", "Policies", "Hyper-V / WSL2", RepairStatus.Unknown, ex.Message);
            }
        }, cancellationToken).ConfigureAwait(false);
        checks.Add(await WithSnapshot(hyperv, "state|bcd|HYPERVISORLAUNCHTYPE", cancellationToken).ConfigureAwait(false));

        return checks
            .OrderBy(c => c.Status == RepairStatus.Broken ? 0 : c.Status == RepairStatus.Unknown ? 1 : 2)
            .ThenBy(c => c.Category)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<RepairResult> FixAsync(RepairCheck check, CancellationToken cancellationToken = default)
    {
        if (!check.CanFix)
        {
            return new RepairResult(check.Id, false, "Nothing to fix — this item is not broken.", false);
        }

        RepairResult result;
        try
        {
            string message = await Task.Run(() => ApplyFixAsync(check, cancellationToken), cancellationToken).ConfigureAwait(false);
            result = new RepairResult(check.Id, true, message, check.RequiresRestart);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = new RepairResult(check.Id, false, ex.Message, false);
        }

        await _audit.LogAsync(AuditAction.Repaired, $"{(result.Success ? "Repaired" : "Repair failed")}: {check.Name}", TweakId,
            check.Target, $"{check.Detail} → {check.FixDescription}: {result.Message}", result.Success, result.RequiresRestart,
            CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    private async Task<string> ApplyFixAsync(RepairCheck check, CancellationToken cancellationToken)
    {
        string target = check.Target ?? string.Empty;
        switch (check.FixKind)
        {
            case RepairFixKind.RevertSnapshot:
                RevertResult revert = await _snapshots.RevertAsync(check.SnapshotId!.Value, cancellationToken).ConfigureAwait(false);
                return revert.Success ? revert.Message : throw new InvalidOperationException(revert.Message);

            case RepairFixKind.EnableDevice:
                await _snapshots.ApplyStateAsync(TweakId, _devices.Kind, target, "Enabled", $"Repair: enable {check.Name}",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                return "Device enabled.";

            case RepairFixKind.RestartDevice:
                _devices.Restart(target);
                return "Device restarted.";

            case RepairFixKind.SetServiceStartType:
                ServiceStartType startType = Enum.Parse<ServiceStartType>(check.FixValue!);
                await _snapshots.ApplyServiceAsync(TweakId, target, startType, description: $"Repair: {check.Name}",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                ServiceExpectation? expectation = RepairRules.WatchedServices.Append(RepairRules.SearchIndexing)
                    .FirstOrDefault(e => e.ServiceName.Equals(target, StringComparison.OrdinalIgnoreCase));
                if (expectation?.MustBeRunning == true)
                {
                    _services.Start(target, ServiceTimeout);
                }

                return $"Startup type restored to {startType}.";

            case RepairFixKind.StartService:
                _services.Start(target, ServiceTimeout);
                return "Service started.";

            case RepairFixKind.RestartService:
                _services.Stop(target, ServiceTimeout);
                _services.Start(target, ServiceTimeout);
                return "Service restarted.";

            case RepairFixKind.DeleteRegistryValue:
                await _snapshots.ApplyRegistryAsync(TweakId, StorageSensePolicy, null, "Repair: remove Storage Sense block",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                return "Blocking policy removed.";

            case RepairFixKind.SetBootHypervisor:
                await _snapshots.ApplyStateAsync(TweakId, _bcd.Kind, target, check.FixValue!, "Repair: hypervisor launch",
                    true, cancellationToken).ConfigureAwait(false);
                return "Hypervisor will launch at the next boot. Restart to apply.";

            case RepairFixKind.EnableFeature:
                await _snapshots.ApplyStateAsync(TweakId, "dism_feature", target, "Enabled", $"Repair: enable {target}",
                    true, cancellationToken).ConfigureAwait(false);
                return $"{target} enabled. Restart to apply.";

            default:
                throw new NotSupportedException($"Unsupported fix {check.FixKind}.");
        }
    }

    private async Task<RepairCheck> WithSnapshot(RepairCheck check, string targetKey, CancellationToken cancellationToken) =>
        check.Status == RepairStatus.Broken
            ? RepairRules.PreferSnapshotRevert(check, await _snapshots.FindActiveAsync(targetKey, cancellationToken).ConfigureAwait(false))
            : check;
}

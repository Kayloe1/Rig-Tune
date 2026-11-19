using RigTune.Core.Models;
using RigTune.Core.Platform;

namespace RigTune.Core.Services;

/// <summary>
/// Windows Update Drift Detector (SPEC §3.1): compares every active snapshot's applied value with live
/// registry / service / power / NVAPI DRS / generic state, and re-applies drifted items through
/// <see cref="SnapshotService"/> — which keeps the snapshot's true original, so Revert still restores the
/// pre-RigTune state after a re-apply.
/// </summary>
public sealed class DriftDetectorService
{
    private readonly SnapshotService _snapshots;
    private readonly IRegistryAccess _registry;
    private readonly IServiceAccess _services;
    private readonly IPowerAccess _power;
    private readonly INvApiDrsAccess _nvapi;
    private readonly AuditLogService _audit;
    private readonly Func<string, string?> _tweakName;
    private readonly TimeProvider _time;

    public DriftDetectorService(SnapshotService snapshots, IRegistryAccess registry, IServiceAccess services, IPowerAccess power,
        INvApiDrsAccess nvapi, AuditLogService audit, Func<string, string?>? tweakName = null, TimeProvider? time = null)
    {
        _snapshots = snapshots;
        _registry = registry;
        _services = services;
        _power = power;
        _nvapi = nvapi;
        _audit = audit;
        _tweakName = tweakName ?? (_ => null);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised after every scan (from a background thread).</summary>
    public event EventHandler<DriftScanResult>? ScanCompleted;

    public DriftScanResult? LastResult { get; private set; }

    public async Task<DriftScanResult> ScanAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SnapshotEntry> active = await _snapshots.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        List<DriftItem> items = await Task.Run(() => active.Select(Compare).ToList(), cancellationToken).ConfigureAwait(false);
        var result = new DriftScanResult(items, _time.GetUtcNow());
        LastResult = result;

        if (result.Drifted.Count > 0)
        {
            await _audit.LogAsync(AuditAction.DriftDetected,
                $"Drift detected: {result.Drifted.Count} of {items.Count} active change(s) were reverted outside RigTune",
                "drift.scan", details: string.Join("; ", result.Drifted.Select(d => $"{d.TweakName}: {d.Target} expected {d.Expected}, now {d.Current}")),
                success: false, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }

        ScanCompleted?.Invoke(this, result);
        return result;
    }

    /// <summary>Compares one snapshot's applied value with the live system.</summary>
    public DriftItem Compare(SnapshotEntry entry)
    {
        string name = _tweakName(entry.TweakId) ?? entry.Description ?? entry.TweakId;
        DriftItem Item(DriftStatus status, string current, string? note = null) =>
            new(entry.Id, entry.TweakId, name, entry.TargetDisplay, entry.AppliedDisplay, current, status, entry.RequiresRestart, note);

        try
        {
            switch (entry)
            {
                case RegistrySnapshotEntry reg:
                {
                    RegistryValueData? live = _registry.ReadValue(reg.Target);
                    bool same = reg.Applied is null ? live is null : reg.Applied.Equals(live);
                    return Item(same ? DriftStatus.InPlace : DriftStatus.Drifted, live?.ToString() ?? "(value missing)");
                }

                case ServiceSnapshotEntry svc:
                {
                    ServiceState? live = _services.Query(svc.ServiceName);
                    return live is null
                        ? Item(DriftStatus.Unavailable, "(service not installed)")
                        : Item(live.StartType == svc.AppliedStartType ? DriftStatus.InPlace : DriftStatus.Drifted,
                            $"{live.StartType}{(live.IsRunning ? ", running" : ", stopped")}");
                }

                case PowerSnapshotEntry pwr:
                {
                    uint ac = _power.ReadAcValue(pwr.SchemeGuid, pwr.SubgroupGuid, pwr.SettingGuid);
                    uint dc = _power.ReadDcValue(pwr.SchemeGuid, pwr.SubgroupGuid, pwr.SettingGuid);
                    bool same = (pwr.AppliedAcValue is null || pwr.AppliedAcValue == ac) && (pwr.AppliedDcValue is null || pwr.AppliedDcValue == dc);
                    return Item(same ? DriftStatus.InPlace : DriftStatus.Drifted, $"AC={ac}, DC={dc}");
                }

                case NvApiDrsSnapshotEntry nv:
                {
                    if (!_nvapi.IsAvailable)
                    {
                        return Item(DriftStatus.Unavailable, "(NVIDIA driver / NVAPI unavailable)");
                    }

                    NvDrsValue live = _nvapi.ReadGlobal(nv.SettingId);
                    bool same = nv.AppliedDwordValue is { } v ? live.ExistsInBaseProfile && live.Value == v : !live.ExistsInBaseProfile;
                    return Item(same ? DriftStatus.InPlace : DriftStatus.Drifted,
                        live.ExistsInBaseProfile ? $"0x{live.Value:X8}" : "(driver default)",
                        same ? null : "NVIDIA driver updates often reset DRS profile settings.");
                }

                case StateSnapshotEntry st:
                {
                    IStateRunner? runner = _snapshots.GetStateRunner(st.Kind);
                    string? live = runner?.ReadState(st.Target);
                    return live is null
                        ? Item(DriftStatus.Unavailable, runner is null ? $"(no runner for {st.Kind})" : "(not present)")
                        : Item(string.Equals(live, st.AppliedState, StringComparison.OrdinalIgnoreCase) ? DriftStatus.InPlace : DriftStatus.Drifted, live);
                }

                default:
                    return Item(DriftStatus.Unavailable, "(unknown snapshot type)");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Item(DriftStatus.Unavailable, $"(could not read: {ex.Message})");
        }
    }

    /// <summary>Re-applies one drifted item, keeping the snapshot's original for a later true revert.</summary>
    public async Task<ReapplyResult> ReapplyAsync(Guid snapshotId, CancellationToken cancellationToken = default)
    {
        SnapshotEntry? entry = (await _snapshots.GetActiveAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(e => e.Id == snapshotId);
        if (entry is null)
        {
            return new ReapplyResult(snapshotId, false, "That change is no longer active.");
        }

        ReapplyResult result;
        try
        {
            switch (entry)
            {
                case RegistrySnapshotEntry reg:
                    await _snapshots.ApplyRegistryAsync(reg.TweakId, reg.Target, reg.Applied, reg.Description, reg.RequiresRestart, cancellationToken).ConfigureAwait(false);
                    break;
                case ServiceSnapshotEntry svc:
                    await _snapshots.ApplyServiceAsync(svc.TweakId, svc.ServiceName, svc.AppliedStartType,
                        stopService: svc.AppliedStartType == ServiceStartType.Disabled, svc.Description, svc.RequiresRestart, cancellationToken).ConfigureAwait(false);
                    break;
                case PowerSnapshotEntry pwr:
                    await _snapshots.ApplyPowerAsync(pwr.TweakId, pwr.SubgroupGuid, pwr.SettingGuid, pwr.AppliedAcValue, pwr.AppliedDcValue,
                        pwr.SchemeGuid, pwr.Description, pwr.RequiresRestart, cancellationToken).ConfigureAwait(false);
                    break;
                case NvApiDrsSnapshotEntry nv:
                    await _snapshots.ApplyNvApiDrsAsync(nv.TweakId, nv.SettingId, nv.AppliedDwordValue, nv.Description, nv.RequiresRestart, cancellationToken).ConfigureAwait(false);
                    break;
                case StateSnapshotEntry st:
                    await _snapshots.ApplyStateAsync(st.TweakId, st.Kind, st.Target, st.AppliedState, st.Description, st.RequiresRestart, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    return new ReapplyResult(snapshotId, false, "Unknown snapshot type.");
            }

            DriftItem after = Compare((await _snapshots.GetActiveAsync(cancellationToken).ConfigureAwait(false)).First(e => e.Id == snapshotId));
            result = after.Status == DriftStatus.InPlace
                ? new ReapplyResult(snapshotId, true, $"Re-applied {after.Expected}{(entry.RequiresRestart ? " (restart required)" : string.Empty)}.")
                : new ReapplyResult(snapshotId, false, $"Re-applied, but the live value is still {after.Current}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = new ReapplyResult(snapshotId, false, ex.Message);
        }

        await _audit.LogAsync(result.Success ? AuditAction.Reapplied : AuditAction.ApplyFailed,
            $"{(result.Success ? "Re-applied drifted" : "Could not re-apply")} {entry.TargetDisplay}",
            entry.TweakId, entry.TargetDisplay, result.Message, result.Success, entry.RequiresRestart, CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    /// <summary>"Re-apply All Drifted": scans, then re-applies every drifted item.</summary>
    public async Task<IReadOnlyList<ReapplyResult>> ReapplyAllDriftedAsync(CancellationToken cancellationToken = default)
    {
        DriftScanResult scan = await ScanAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<ReapplyResult>();
        foreach (DriftItem item in scan.Drifted)
        {
            results.Add(await ReapplyAsync(item.SnapshotId, cancellationToken).ConfigureAwait(false));
        }

        await ScanAsync(cancellationToken).ConfigureAwait(false);
        return results;
    }
}

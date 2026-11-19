using RigTune.Core.Models;
using RigTune.Core.Platform;
using RigTune.Core.Storage;

namespace RigTune.Core.Services;

/// <summary>
/// Snapshot &amp; true-rollback engine (SPEC §3.1).
/// <para>
/// Every <c>Apply*</c> call captures the exact pre-change state of its target and persists it to
/// <c>snapshots.json</c> <em>before</em> touching the system. If a target already has an active
/// snapshot, the original captured value is preserved (re-applying never overwrites the true original).
/// Reverting restores exactly what was there: values that did not exist are deleted (never replaced
/// by a guessed default), otherwise the original kind and payload are written back.
/// </para>
/// </summary>
public sealed class SnapshotService
{
    private static readonly TimeSpan ServiceTimeout = TimeSpan.FromSeconds(30);

    private readonly JsonDocumentStore<SnapshotDocument> _store;
    private readonly IRegistryAccess _registry;
    private readonly IServiceAccess _services;
    private readonly IPowerAccess _power;
    private readonly INvApiDrsAccess _nvapi;
    private readonly AuditLogService _audit;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _opLock = new(1, 1);
    private readonly Dictionary<string, IStateRunner> _stateRunners = new(StringComparer.OrdinalIgnoreCase);

    public SnapshotService(RigTunePaths paths, IRegistryAccess registry, IServiceAccess services, IPowerAccess power,
        INvApiDrsAccess nvapi, AuditLogService audit, TimeProvider? time = null,
        IEnumerable<IStateRunner>? stateRunners = null)
        : this(new JsonDocumentStore<SnapshotDocument>(paths.SnapshotsFile), registry, services, power, nvapi, audit,
            time, stateRunners)
    {
    }

    public SnapshotService(JsonDocumentStore<SnapshotDocument> store, IRegistryAccess registry, IServiceAccess services,
        IPowerAccess power, INvApiDrsAccess nvapi, AuditLogService audit, TimeProvider? time = null,
        IEnumerable<IStateRunner>? stateRunners = null)
    {
        foreach (IStateRunner runner in stateRunners ?? Enumerable.Empty<IStateRunner>())
        {
            _stateRunners[runner.Kind] = runner;
        }

        _store = store;
        _registry = registry;
        _services = services;
        _power = power;
        _nvapi = nvapi;
        _audit = audit;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised after any snapshot is created, updated or reverted.</summary>
    public event EventHandler? SnapshotsChanged;

    public async Task<IReadOnlyList<SnapshotEntry>> GetAllAsync(CancellationToken cancellationToken = default) =>
        (await _store.ReadAsync(cancellationToken).ConfigureAwait(false)).Entries;

    /// <summary>Active snapshots, newest first.</summary>
    public async Task<IReadOnlyList<SnapshotEntry>> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        SnapshotDocument doc = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        return doc.Entries.Where(e => e.Status == SnapshotStatus.Active).Reverse().ToList();
    }

    public async Task<SnapshotEntry?> FindActiveAsync(string targetKey, CancellationToken cancellationToken = default)
    {
        SnapshotDocument doc = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        return doc.Entries.FirstOrDefault(e => e.Status == SnapshotStatus.Active && e.TargetKey == targetKey);
    }

    /// <summary>Active snapshots created by one tweak, newest first.</summary>
    public async Task<IReadOnlyList<SnapshotEntry>> GetActiveForTweakAsync(string tweakId,
        CancellationToken cancellationToken = default) =>
        (await GetActiveAsync(cancellationToken).ConfigureAwait(false))
        .Where(e => string.Equals(e.TweakId, tweakId, StringComparison.OrdinalIgnoreCase))
        .ToList();

    public IStateRunner? GetStateRunner(string kind) => _stateRunners.GetValueOrDefault(kind);

    // ------------------------------------------------------------------ Apply

    /// <summary>Snapshots the current state of a generic target and then writes <paramref name="desiredState"/>.</summary>
    public Task<StateSnapshotEntry> ApplyStateAsync(string tweakId, string kind, string target, string desiredState,
        string? description = null, bool requiresRestart = false, CancellationToken cancellationToken = default)
    {
        IStateRunner runner = _stateRunners.GetValueOrDefault(kind)
                              ?? throw new InvalidOperationException($"No state runner registered for '{kind}'.");
        return ApplyCoreAsync(
            tweakId,
            description,
            requiresRestart,
            $"state|{kind}|{target.ToUpperInvariant()}",
            capture: () => new StateSnapshotEntry
            {
                Kind = kind,
                Target = target,
                Label = runner.Describe(target),
                OriginalState = runner.ReadState(target)
                                ?? throw new InvalidOperationException($"{runner.Describe(target)} was not found on this PC."),
            },
            setApplied: e => e.AppliedState = desiredState,
            write: _ => runner.WriteState(target, desiredState),
            cancellationToken);
    }

    /// <summary>
    /// Snapshots and then writes a registry value. Pass <paramref name="value"/> = null to delete the value.
    /// </summary>
    public Task<RegistrySnapshotEntry> ApplyRegistryAsync(string tweakId, RegistryTarget target, RegistryValueData? value,
        string? description = null, bool requiresRestart = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        return ApplyCoreAsync(
            tweakId,
            description,
            requiresRestart,
            target.IdentityKey,
            capture: () =>
            {
                RegistryValueData? original = _registry.ReadValue(target);
                return new RegistrySnapshotEntry
                {
                    Hive = target.Hive,
                    View = target.View,
                    KeyPath = target.KeyPath.Trim('\\'),
                    ValueName = target.ValueName,
                    Existed = original is not null,
                    Original = original,
                    FirstCreatedKeyPath = FindFirstMissingKey(target.Hive, target.View, target.KeyPath),
                };
            },
            setApplied: e => e.Applied = value,
            write: _ =>
            {
                if (value is null)
                {
                    _registry.DeleteValue(target);
                }
                else
                {
                    _registry.WriteValue(target, value);
                }
            },
            cancellationToken);
    }

    /// <summary>Snapshots start type + running state, then sets a new start type (and optionally stops it).</summary>
    public Task<ServiceSnapshotEntry> ApplyServiceAsync(string tweakId, string serviceName, ServiceStartType startType,
        bool stopService = false, string? description = null, bool requiresRestart = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        return ApplyCoreAsync(
            tweakId,
            description,
            requiresRestart,
            $"svc|{serviceName.ToUpperInvariant()}",
            capture: () =>
            {
                ServiceState state = _services.Query(serviceName)
                                     ?? throw new InvalidOperationException($"Service '{serviceName}' is not installed.");
                return new ServiceSnapshotEntry
                {
                    ServiceName = state.ServiceName,
                    OriginalStartType = state.StartType,
                    WasRunning = state.IsRunning,
                };
            },
            setApplied: e => e.AppliedStartType = startType,
            write: e =>
            {
                _services.SetStartType(e.ServiceName, startType);
                if (stopService)
                {
                    _services.Stop(e.ServiceName, ServiceTimeout);
                }
            },
            cancellationToken);
    }

    /// <summary>
    /// Snapshots the AC/DC indices of a power setting, then writes the requested ones.
    /// <paramref name="scheme"/> defaults to the active scheme.
    /// </summary>
    public Task<PowerSnapshotEntry> ApplyPowerAsync(string tweakId, Guid subgroup, Guid setting, uint? acValue,
        uint? dcValue, Guid? scheme = null, string? description = null, bool requiresRestart = false,
        CancellationToken cancellationToken = default)
    {
        if (acValue is null && dcValue is null)
        {
            throw new ArgumentException("At least one of acValue / dcValue must be provided.");
        }

        Guid schemeGuid = scheme ?? _power.GetActiveScheme();
        return ApplyCoreAsync(
            tweakId,
            description,
            requiresRestart,
            $"pwr|{schemeGuid:N}|{subgroup:N}|{setting:N}",
            capture: () => new PowerSnapshotEntry
            {
                SchemeGuid = schemeGuid,
                SubgroupGuid = subgroup,
                SettingGuid = setting,
                OriginalAcValue = _power.ReadAcValue(schemeGuid, subgroup, setting),
                OriginalDcValue = _power.ReadDcValue(schemeGuid, subgroup, setting),
            },
            setApplied: e =>
            {
                e.AppliedAcValue = acValue ?? e.AppliedAcValue;
                e.AppliedDcValue = dcValue ?? e.AppliedDcValue;
            },
            write: _ =>
            {
                if (acValue is { } ac)
                {
                    _power.WriteAcValue(schemeGuid, subgroup, setting, ac);
                }

                if (dcValue is { } dc)
                {
                    _power.WriteDcValue(schemeGuid, subgroup, setting, dc);
                }

                _power.ApplyScheme(schemeGuid);
            },
            cancellationToken);
    }

    /// <summary>
    /// Snapshots then writes a DWORD setting on the NVIDIA global (base) DRS profile.
    /// <paramref name="value"/> = null removes the override (driver default).
    /// </summary>
    public Task<NvApiDrsSnapshotEntry> ApplyNvApiDrsAsync(string tweakId, uint settingId, uint? value,
        string? description = null, bool requiresRestart = false, CancellationToken cancellationToken = default) =>
        ApplyCoreAsync(
            tweakId,
            description,
            requiresRestart,
            $"nvdrs|base|{settingId:X8}",
            capture: () =>
            {
                if (!_nvapi.IsAvailable)
                {
                    throw new InvalidOperationException("NVIDIA NVAPI is not available.");
                }

                NvDrsValue current = _nvapi.ReadGlobal(settingId);
                return new NvApiDrsSnapshotEntry
                {
                    SettingId = settingId,
                    ExistedInBaseProfile = current.ExistsInBaseProfile,
                    OriginalDwordValue = current.ExistsInBaseProfile ? current.Value : null,
                };
            },
            setApplied: e => e.AppliedDwordValue = value,
            write: _ =>
            {
                if (value is { } v)
                {
                    _nvapi.WriteGlobal(settingId, v);
                }
                else
                {
                    _nvapi.DeleteGlobal(settingId);
                }
            },
            cancellationToken);

    private async Task<TEntry> ApplyCoreAsync<TEntry>(string tweakId, string? description, bool requiresRestart,
        string targetKey, Func<TEntry> capture, Action<TEntry> setApplied, Action<TEntry> write,
        CancellationToken cancellationToken)
        where TEntry : SnapshotEntry
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tweakId);
        await _opLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SnapshotDocument doc = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
            TEntry? existing = doc.Entries.OfType<TEntry>()
                .FirstOrDefault(e => e.Status == SnapshotStatus.Active && e.TargetKey == targetKey);

            DateTimeOffset now = _time.GetUtcNow();
            bool isNew = existing is null;
            TEntry entry = existing ?? capture();
            if (isNew)
            {
                entry.TweakId = tweakId;
                entry.Description = description;
                entry.CapturedAtUtc = now;
            }

            setApplied(entry);
            entry.LastAppliedAtUtc = now;
            entry.RequiresRestart |= requiresRestart;
            entry.LastError = null;

            // Persist the original BEFORE writing so a crash mid-write can still be rolled back.
            await _store.UpdateAsync(d => Upsert(d, entry), cancellationToken).ConfigureAwait(false);

            try
            {
                write(entry);
            }
            catch (Exception ex)
            {
                await HandleApplyFailureAsync(entry, isNew, ex, cancellationToken).ConfigureAwait(false);
                throw;
            }

            await _audit.LogAsync(AuditAction.Applied, $"Applied {entry.TargetDisplay}", tweakId, entry.TargetDisplay,
                $"Original: {entry.OriginalDisplay} → Applied: {entry.AppliedDisplay}", success: true,
                requiresRestart: entry.RequiresRestart, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            SnapshotsChanged?.Invoke(this, EventArgs.Empty);
            return entry;
        }
        finally
        {
            _opLock.Release();
        }
    }

    private async Task HandleApplyFailureAsync(SnapshotEntry entry, bool isNew, Exception error,
        CancellationToken cancellationToken)
    {
        string details = error.Message;
        if (isNew)
        {
            // Roll back any partial write; drop the snapshot only once the original is confirmed in place.
            try
            {
                if (!MatchesOriginal(entry))
                {
                    RestoreOriginal(entry);
                }

                await _store.UpdateAsync(d => d.Entries.RemoveAll(e => e.Id == entry.Id), CancellationToken.None)
                    .ConfigureAwait(false);
                details += " (original state restored)";
            }
            catch (Exception rollbackError)
            {
                entry.LastError = $"Apply failed: {error.Message}; rollback failed: {rollbackError.Message}";
                await _store.UpdateAsync(d => Upsert(d, entry), CancellationToken.None).ConfigureAwait(false);
                details = entry.LastError;
            }
        }
        else
        {
            entry.LastError = $"Re-apply failed: {error.Message}";
            await _store.UpdateAsync(d => Upsert(d, entry), CancellationToken.None).ConfigureAwait(false);
        }

        await _audit.LogAsync(AuditAction.ApplyFailed, $"Failed to apply {entry.TargetDisplay}", entry.TweakId,
            entry.TargetDisplay, details, success: false, cancellationToken: cancellationToken).ConfigureAwait(false);
        SnapshotsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ Revert

    public async Task<RevertResult> RevertAsync(Guid snapshotId, CancellationToken cancellationToken = default)
    {
        await _opLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RevertResult result = await RevertCoreAsync(snapshotId, cancellationToken).ConfigureAwait(false);
            SnapshotsChanged?.Invoke(this, EventArgs.Empty);
            return result;
        }
        finally
        {
            _opLock.Release();
        }
    }

    /// <summary>
    /// Reverts every active snapshot in reverse chronological order (newest first), continuing past
    /// individual failures, and reports which restored items need a reboot.
    /// </summary>
    public async Task<RevertAllResult> RevertAllAsync(CancellationToken cancellationToken = default)
    {
        await _opLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SnapshotDocument doc = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
            List<Guid> ordered = doc.Entries
                .Select((entry, index) => (entry, index))
                .Where(x => x.entry.Status == SnapshotStatus.Active)
                .OrderByDescending(x => x.entry.CapturedAtUtc)
                .ThenByDescending(x => x.index)
                .Select(x => x.entry.Id)
                .ToList();

            var results = new List<RevertResult>(ordered.Count);
            foreach (Guid id in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(await RevertCoreAsync(id, cancellationToken).ConfigureAwait(false));
            }

            var all = new RevertAllResult(results);
            await _audit.LogAsync(AuditAction.RevertAll,
                $"Revert All: {all.Succeeded} restored, {all.Failed} failed, {all.NeedingRestart.Count} need a restart",
                success: all.Failed == 0, requiresRestart: all.NeedingRestart.Count > 0,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            SnapshotsChanged?.Invoke(this, EventArgs.Empty);
            return all;
        }
        finally
        {
            _opLock.Release();
        }
    }

    /// <summary>Reverts every active snapshot belonging to one tweak (newest first).</summary>
    public async Task<RevertAllResult> RevertTweakAsync(string tweakId, CancellationToken cancellationToken = default)
    {
        await _opLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SnapshotDocument doc = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
            List<Guid> ids = doc.Entries
                .Select((entry, index) => (entry, index))
                .Where(x => x.entry.Status == SnapshotStatus.Active &&
                            string.Equals(x.entry.TweakId, tweakId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.entry.CapturedAtUtc)
                .ThenByDescending(x => x.index)
                .Select(x => x.entry.Id)
                .ToList();

            var results = new List<RevertResult>(ids.Count);
            foreach (Guid id in ids)
            {
                results.Add(await RevertCoreAsync(id, cancellationToken).ConfigureAwait(false));
            }

            SnapshotsChanged?.Invoke(this, EventArgs.Empty);
            return new RevertAllResult(results);
        }
        finally
        {
            _opLock.Release();
        }
    }

    private async Task<RevertResult> RevertCoreAsync(Guid snapshotId, CancellationToken cancellationToken)
    {
        SnapshotDocument doc = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        SnapshotEntry? entry = doc.Entries.FirstOrDefault(e => e.Id == snapshotId);
        if (entry is null)
        {
            return new RevertResult(snapshotId, string.Empty, string.Empty, false, false, "Snapshot not found.");
        }

        if (entry.Status != SnapshotStatus.Active)
        {
            return new RevertResult(entry.Id, entry.TweakId, entry.TargetDisplay, true, false, "Already reverted.");
        }

        try
        {
            RestoreOriginal(entry);
        }
        catch (Exception ex)
        {
            entry.LastError = $"Revert failed: {ex.Message}";
            await _store.UpdateAsync(d => Upsert(d, entry), CancellationToken.None).ConfigureAwait(false);
            await _audit.LogAsync(AuditAction.RevertFailed, $"Failed to revert {entry.TargetDisplay}", entry.TweakId,
                entry.TargetDisplay, ex.Message, success: false, cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
            return new RevertResult(entry.Id, entry.TweakId, entry.TargetDisplay, false, false, ex.Message);
        }

        entry.Status = SnapshotStatus.Reverted;
        entry.RevertedAtUtc = _time.GetUtcNow();
        entry.LastError = null;
        await _store.UpdateAsync(d => Upsert(d, entry), CancellationToken.None).ConfigureAwait(false);
        await _audit.LogAsync(AuditAction.Reverted, $"Reverted {entry.TargetDisplay}", entry.TweakId,
            entry.TargetDisplay, $"Restored: {entry.OriginalDisplay}", success: true,
            requiresRestart: entry.RequiresRestart, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        return new RevertResult(entry.Id, entry.TweakId, entry.TargetDisplay, true, entry.RequiresRestart,
            $"Restored {entry.OriginalDisplay}");
    }

    private void RestoreOriginal(SnapshotEntry entry)
    {
        switch (entry)
        {
            case RegistrySnapshotEntry reg:
                RestoreRegistry(reg);
                break;

            case ServiceSnapshotEntry svc:
                _services.SetStartType(svc.ServiceName, svc.OriginalStartType);
                if (svc.WasRunning)
                {
                    _services.Start(svc.ServiceName, ServiceTimeout);
                }

                break;

            case PowerSnapshotEntry pwr:
                _power.WriteAcValue(pwr.SchemeGuid, pwr.SubgroupGuid, pwr.SettingGuid, pwr.OriginalAcValue);
                _power.WriteDcValue(pwr.SchemeGuid, pwr.SubgroupGuid, pwr.SettingGuid, pwr.OriginalDcValue);
                _power.ApplyScheme(pwr.SchemeGuid);
                break;

            case NvApiDrsSnapshotEntry nv:
                if (nv.ExistedInBaseProfile && nv.OriginalDwordValue is { } original)
                {
                    _nvapi.WriteGlobal(nv.SettingId, original);
                }
                else
                {
                    _nvapi.DeleteGlobal(nv.SettingId);
                }

                break;

            case StateSnapshotEntry st:
                (_stateRunners.GetValueOrDefault(st.Kind)
                 ?? throw new InvalidOperationException($"No state runner registered for '{st.Kind}'."))
                    .WriteState(st.Target, st.OriginalState);
                break;

            default:
                throw new NotSupportedException($"Unknown snapshot type {entry.GetType().Name}.");
        }
    }

    /// <summary>True when the live system state still equals the captured original.</summary>
    private bool MatchesOriginal(SnapshotEntry entry)
    {
        switch (entry)
        {
            case RegistrySnapshotEntry reg:
                RegistryValueData? live = _registry.ReadValue(reg.Target);
                return reg.Existed ? reg.Original is not null && reg.Original.Equals(live) : live is null;

            case ServiceSnapshotEntry svc:
                ServiceState? state = _services.Query(svc.ServiceName);
                return state is not null && state.StartType == svc.OriginalStartType && state.IsRunning == svc.WasRunning;

            case PowerSnapshotEntry pwr:
                return _power.ReadAcValue(pwr.SchemeGuid, pwr.SubgroupGuid, pwr.SettingGuid) == pwr.OriginalAcValue &&
                       _power.ReadDcValue(pwr.SchemeGuid, pwr.SubgroupGuid, pwr.SettingGuid) == pwr.OriginalDcValue;

            case NvApiDrsSnapshotEntry nv:
                NvDrsValue value = _nvapi.ReadGlobal(nv.SettingId);
                return value.ExistsInBaseProfile == nv.ExistedInBaseProfile &&
                       (!nv.ExistedInBaseProfile || value.Value == nv.OriginalDwordValue);

            case StateSnapshotEntry st:
                return string.Equals(_stateRunners.GetValueOrDefault(st.Kind)?.ReadState(st.Target), st.OriginalState,
                    StringComparison.OrdinalIgnoreCase);

            default:
                return false;
        }
    }

    private void RestoreRegistry(RegistrySnapshotEntry reg)
    {
        RegistryTarget target = reg.Target;
        if (reg.Existed && reg.Original is not null)
        {
            _registry.WriteValue(target, reg.Original);
            return;
        }

        // The value did not exist before: delete it — never write a guessed default.
        _registry.DeleteValue(target);

        if (reg.FirstCreatedKeyPath is null)
        {
            return;
        }

        // Remove keys RigTune created (leaf → first created ancestor), but only while they are empty.
        string stop = reg.FirstCreatedKeyPath.Trim('\\');
        string current = reg.KeyPath.Trim('\\');
        while (current.Length >= stop.Length)
        {
            if (!_registry.DeleteKeyIfEmpty(reg.Hive, reg.View, current) && _registry.KeyExists(reg.Hive, reg.View, current))
            {
                break;
            }

            if (string.Equals(current, stop, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            int slash = current.LastIndexOf('\\');
            if (slash <= 0)
            {
                break;
            }

            current = current[..slash];
        }
    }

    private string? FindFirstMissingKey(RegistryHiveName hive, RegistryViewName view, string keyPath)
    {
        string[] parts = keyPath.Trim('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 1; i <= parts.Length; i++)
        {
            string prefix = string.Join('\\', parts, 0, i);
            if (!_registry.KeyExists(hive, view, prefix))
            {
                return prefix;
            }
        }

        return null;
    }

    private static void Upsert(SnapshotDocument doc, SnapshotEntry entry)
    {
        int index = doc.Entries.FindIndex(e => e.Id == entry.Id);
        if (index >= 0)
        {
            doc.Entries[index] = entry;
        }
        else
        {
            doc.Entries.Add(entry);
        }
    }
}

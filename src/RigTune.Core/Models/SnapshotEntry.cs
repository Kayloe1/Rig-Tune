using System.Text.Json.Serialization;

namespace RigTune.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SnapshotStatus
{
    /// <summary>The change is applied; the snapshot holds the pre-change original.</summary>
    Active,

    /// <summary>The original state has been restored.</summary>
    Reverted,
}

/// <summary>
/// Exact pre-change state of one target, captured before RigTune writes anything.
/// Persisted polymorphically in <c>snapshots.json</c>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(RegistrySnapshotEntry), "registry")]
[JsonDerivedType(typeof(ServiceSnapshotEntry), "service")]
[JsonDerivedType(typeof(PowerSnapshotEntry), "power")]
[JsonDerivedType(typeof(NvApiDrsSnapshotEntry), "nvapi_drs")]
public abstract class SnapshotEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TweakId { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTimeOffset CapturedAtUtc { get; set; }
    public DateTimeOffset? LastAppliedAtUtc { get; set; }
    public SnapshotStatus Status { get; set; } = SnapshotStatus.Active;
    public DateTimeOffset? RevertedAtUtc { get; set; }
    public bool RequiresRestart { get; set; }
    public string? LastError { get; set; }

    /// <summary>Stable identity of the target (used so re-applying never overwrites the true original).</summary>
    [JsonIgnore]
    public abstract string TargetKey { get; }

    [JsonIgnore]
    public abstract string TargetDisplay { get; }

    [JsonIgnore]
    public abstract string OriginalDisplay { get; }

    [JsonIgnore]
    public abstract string AppliedDisplay { get; }
}

public sealed class RegistrySnapshotEntry : SnapshotEntry
{
    public RegistryHiveName Hive { get; set; }
    public RegistryViewName View { get; set; } = RegistryViewName.Registry64;
    public string KeyPath { get; set; } = string.Empty;
    public string ValueName { get; set; } = string.Empty;

    /// <summary>Whether the value existed before the change. If false, revert deletes it.</summary>
    public bool Existed { get; set; }

    /// <summary>
    /// Shallowest key on <see cref="KeyPath"/> that did not exist before the change (null if the
    /// full key already existed). On revert, keys RigTune created are removed again if left empty.
    /// </summary>
    public string? FirstCreatedKeyPath { get; set; }

    /// <summary>Exact original kind/value; null when <see cref="Existed"/> is false.</summary>
    public RegistryValueData? Original { get; set; }

    public RegistryValueData? Applied { get; set; }

    [JsonIgnore]
    public RegistryTarget Target => new(Hive, View, KeyPath, ValueName);

    public override string TargetKey => Target.IdentityKey;
    public override string TargetDisplay => Target.Display;
    public override string OriginalDisplay => Existed && Original is not null ? Original.ToString() : "(value did not exist)";
    public override string AppliedDisplay => Applied?.ToString() ?? "(deleted)";
}

public sealed class ServiceSnapshotEntry : SnapshotEntry
{
    public string ServiceName { get; set; } = string.Empty;
    public ServiceStartType OriginalStartType { get; set; }
    public bool WasRunning { get; set; }
    public ServiceStartType AppliedStartType { get; set; }

    public override string TargetKey => $"svc|{ServiceName.ToUpperInvariant()}";
    public override string TargetDisplay => $"Service: {ServiceName}";
    public override string OriginalDisplay => $"{OriginalStartType}{(WasRunning ? ", running" : ", stopped")}";
    public override string AppliedDisplay => AppliedStartType.ToString();
}

public sealed class PowerSnapshotEntry : SnapshotEntry
{
    public Guid SchemeGuid { get; set; }
    public Guid SubgroupGuid { get; set; }
    public Guid SettingGuid { get; set; }
    public uint OriginalAcValue { get; set; }
    public uint OriginalDcValue { get; set; }
    public uint? AppliedAcValue { get; set; }
    public uint? AppliedDcValue { get; set; }

    public override string TargetKey => $"pwr|{SchemeGuid:N}|{SubgroupGuid:N}|{SettingGuid:N}";
    public override string TargetDisplay => $"Power: scheme {SchemeGuid} / {SubgroupGuid} / {SettingGuid}";
    public override string OriginalDisplay => $"AC={OriginalAcValue}, DC={OriginalDcValue}";
    public override string AppliedDisplay => $"AC={AppliedAcValue?.ToString() ?? "-"}, DC={AppliedDcValue?.ToString() ?? "-"}";
}

public sealed class NvApiDrsSnapshotEntry : SnapshotEntry
{
    public uint SettingId { get; set; }

    /// <summary>Whether the setting was explicitly set in the global (base) profile. If false, revert deletes it.</summary>
    public bool ExistedInBaseProfile { get; set; }

    public uint? OriginalDwordValue { get; set; }
    public uint AppliedDwordValue { get; set; }

    public override string TargetKey => $"nvdrs|base|{SettingId:X8}";
    public override string TargetDisplay => $"NVIDIA DRS global setting 0x{SettingId:X8}";
    public override string OriginalDisplay => ExistedInBaseProfile ? $"0x{OriginalDwordValue:X8}" : "(driver default)";
    public override string AppliedDisplay => $"0x{AppliedDwordValue:X8}";
}

public sealed class SnapshotDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<SnapshotEntry> Entries { get; set; } = new();
}

public sealed record RevertResult(Guid SnapshotId, string TweakId, string Target, bool Success, bool RequiresRestart, string Message);

public sealed record RevertAllResult(IReadOnlyList<RevertResult> Results)
{
    public int Succeeded => Results.Count(r => r.Success);
    public int Failed => Results.Count(r => !r.Success);
    public IReadOnlyList<RevertResult> NeedingRestart => Results.Where(r => r.Success && r.RequiresRestart).ToList();
}

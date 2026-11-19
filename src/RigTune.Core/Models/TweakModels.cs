using System.Text.Json;
using System.Text.Json.Serialization;

namespace RigTune.Core.Models;

/// <summary>Root of <c>Catalogs\tweaks.json</c> (SPEC §4.1).</summary>
public sealed class TweakCatalog
{
    public int SchemaVersion { get; set; } = 1;
    public List<TweakDefinition> Tweaks { get; set; } = new();
}

public static class TweakRiskTiers
{
    public const string Standard = "Standard";
    public const string Moderate = "Moderate";
    public const string DangerZone = "DangerZone";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { Standard, Moderate, DangerZone };
}

public static class TweakUndoTypes
{
    /// <summary>Reversible on/off change (snapshot + revert).</summary>
    public const string Toggle = "Toggle";

    /// <summary>(1x) Runs once; reversible through its snapshots where it changes state.</summary>
    public const string RunOnce = "RunOnce";

    /// <summary>(Perm) Permanent / no automatic undo.</summary>
    public const string Permanent = "Permanent";

    /// <summary>Read-only inspection; changes nothing.</summary>
    public const string ReadOnly = "ReadOnly";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { Toggle, RunOnce, Permanent, ReadOnly };
}

/// <summary>The action types allowed by SPEC §4.1.</summary>
public static class TweakActionTypes
{
    public const string Registry = "registry";
    public const string Service = "service";
    public const string Power = "power";
    public const string NvApiDrs = "nvapi_drs";
    public const string Dism = "dism";
    public const string ScheduledTask = "schtask";
    public const string Cleanup = "cleanup";
    public const string PciMsi = "pci_msi";
    public const string InterruptAffinity = "interrupt_affinity";
    public const string DeviceManager = "device_manager";
    public const string WasapiInspect = "wasapi_inspect";
    public const string CpuMicrocodeInspect = "cpu_microcode_inspect";
    public const string TimerResolution = "timer_resolution";
    public const string Ntfs = "ntfs";
    public const string TcpProfile = "tcp_profile";

    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        Registry, Service, Power, NvApiDrs, Dism, ScheduledTask, Cleanup, PciMsi, InterruptAffinity,
        DeviceManager, WasapiInspect, CpuMicrocodeInspect, TimerResolution, Ntfs, TcpProfile,
    };
}

public sealed class TweakDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Subcategory { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Impact { get; set; } = "Low";
    public string RiskTier { get; set; } = TweakRiskTiers.Standard;
    public bool InRecommendedPreset { get; set; }
    public bool RequiresRestart { get; set; }
    public string UndoType { get; set; } = TweakUndoTypes.Toggle;
    public string TargetSummary { get; set; } = string.Empty;

    /// <summary>Extra caution shown in the inspector (e.g. "breaks WSL2").</summary>
    public string? Warning { get; set; }

    /// <summary>Set on "Apply Recommended …" meta-tweaks: the area whose recommended tweaks it applies.</summary>
    public PresetScope? PresetScope { get; set; }

    /// <summary>Name of a custom inspector panel (power_plan, pci_msi, interrupt_affinity, network_adapters, devices).</summary>
    public string? Interactive { get; set; }

    /// <summary>Selectable presets (e.g. NVIDIA shader cache sizes); values keyed by action <see cref="TweakAction.Key"/>.</summary>
    public List<TweakOption>? Options { get; set; }

    public string? DefaultOption { get; set; }

    public List<TweakAction> Actions { get; set; } = new();

    [JsonIgnore]
    public bool IsDangerZone => RiskTier == TweakRiskTiers.DangerZone;

    [JsonIgnore]
    public bool IsPreset => PresetScope is not null;

    [JsonIgnore]
    public bool IsReadOnly => UndoType == TweakUndoTypes.ReadOnly;

    [JsonIgnore]
    public bool IsPermanent => UndoType == TweakUndoTypes.Permanent;
}

public sealed class PresetScope
{
    public string Category { get; set; } = string.Empty;

    /// <summary>Null = the whole category.</summary>
    public string? Subcategory { get; set; }
}

public sealed class TweakOption
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Per-action values keyed by <see cref="TweakAction.Key"/>. The string "delete" restores the default.</summary>
    public Dictionary<string, JsonElement> Values { get; set; } = new();
}

public sealed class ValueRange
{
    public double Min { get; set; }
    public double Max { get; set; }
    public double Step { get; set; } = 1;
    public string? Unit { get; set; }
}

public sealed class DeviceRule
{
    public string Match { get; set; } = string.Empty;
    public string Rating { get; set; } = "Moderate";
    public string? Note { get; set; }
}

/// <summary>
/// One step of a tweak. A single flexible shape keeps the JSON readable; each runner reads the
/// fields relevant to its <see cref="Type"/>.
/// </summary>
public sealed class TweakAction
{
    public string Type { get; set; } = string.Empty;

    /// <summary>Optional stable key used by options/sliders and parameters.</summary>
    public string? Key { get; set; }

    /// <summary>Sub-operation for multi-purpose runners (e.g. registry "clearBits", dism "feature").</summary>
    public string? Operation { get; set; }

    // ---- registry
    public string? Hive { get; set; }
    public string? View { get; set; }
    public string? Path { get; set; }
    public string? ValueName { get; set; }
    public string? ValueKind { get; set; }
    public JsonElement? ApplyValue { get; set; }
    public uint? BitMask { get; set; }

    // ---- service
    public string? ServiceName { get; set; }
    public string? StartType { get; set; }
    public bool StopService { get; set; }

    // ---- power
    public string? Subgroup { get; set; }
    public string? Setting { get; set; }
    public uint? AcValue { get; set; }
    public uint? DcValue { get; set; }
    public ValueRange? Range { get; set; }

    // ---- nvapi_drs
    public string? SettingId { get; set; }

    // ---- dism / schtask / device / tcp state
    public string? Feature { get; set; }
    public string? PackageName { get; set; }
    public string? TaskPath { get; set; }
    public string? ApplyState { get; set; }
    public string? Keyword { get; set; }
    public string? DeviceClass { get; set; }
    public List<DeviceRule>? Devices { get; set; }

    // ---- timer_resolution
    public double? TargetMs { get; set; }
}

// ------------------------------------------------------------------ runtime status

public enum ActionStatus
{
    Applied,
    NotApplied,
    NotApplicable,
    Informational,
    Unsupported,
    Unknown,
}

public sealed record ActionState(ActionStatus Status, string Target, string? Current = null, string? Expected = null,
    string? Note = null)
{
    public static ActionState Info(string target, string? current, string? note = null) =>
        new(ActionStatus.Informational, target, current, null, note);
}

public enum TweakStatus
{
    Active,
    Partial,
    Inactive,
    NotApplicable,
    Informational,
    Unknown,
}

public sealed record TweakStatusReport(string TweakId, TweakStatus Status, IReadOnlyList<ActionState> Actions,
    bool HasActiveSnapshots)
{
    public string Summary => Status switch
    {
        TweakStatus.Active => "Applied",
        TweakStatus.Partial => "Partially applied",
        TweakStatus.Inactive => "Not applied",
        TweakStatus.NotApplicable => "Not applicable on this PC",
        TweakStatus.Informational => "Inspection",
        _ => "Unknown",
    };
}

/// <summary>Caller-provided inputs: selected option, slider values, device selections, …</summary>
public sealed class TweakParameters
{
    public static TweakParameters None { get; } = new();

    public string? OptionId { get; set; }

    /// <summary>Slider value per action key.</summary>
    public Dictionary<string, double> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Selected device instance IDs / adapter IDs / interface IDs.</summary>
    public List<string> Selections { get; } = new();

    /// <summary>CPU affinity mask (logical processor bits) for interrupt affinity.</summary>
    public ulong? AffinityMask { get; set; }

    /// <summary>Device priority for interrupt affinity (0 undefined, 1 low, 2 normal, 3 high).</summary>
    public int? DevicePriority { get; set; }

    /// <summary>Free-form state value (e.g. adapter property "0"/"1").</summary>
    public string? StateValue { get; set; }
}

public sealed record TweakApplyResult(string TweakId, bool Success, string Message, bool RequiresRestart,
    int ActionsApplied = 0);

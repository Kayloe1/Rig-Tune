namespace RigTune.Core.Models;

// ------------------------------------------------------------------ displays

public sealed record DisplayInfo(
    string DeviceName,
    string MonitorId,
    string FriendlyName,
    bool IsPrimary,
    int Width,
    int Height,
    int BitsPerPixel,
    int CurrentHz,
    IReadOnlyList<int> SupportedHz)
{
    /// <summary>Stable key for settings: the monitor's device interface id, else the GDI device name.</summary>
    public string Key => string.IsNullOrEmpty(MonitorId) ? DeviceName : MonitorId;

    public int MaxHz => SupportedHz.Count > 0 ? SupportedHz.Max() : CurrentHz;
}

public sealed record RefreshDrift(DisplayInfo Display, int TargetHz)
{
    public int CurrentHz => Display.CurrentHz;
}

public sealed record DisplayStatus(DisplayInfo Display, int? TargetHz, bool IsDrifted);

// ------------------------------------------------------------------ games

public enum GameSource
{
    Steam,
    EA,
    BattleNet,
    Xbox,
    Epic,
    Custom,
}

public sealed record DiscoveredGame(string Name, GameSource Source, string InstallDir, IReadOnlyList<string> Executables,
    string? AppId = null, DateTimeOffset? LastPlayed = null);

// ------------------------------------------------------------------ sessions

/// <summary>Windows EcoQoS / power-throttling state (PROCESS_POWER_THROTTLING_STATE).</summary>
public sealed record ThrottleState(uint ControlMask, uint StateMask)
{
    public const uint ExecutionSpeed = 0x1;

    public static ThrottleState EcoQoS { get; } = new(ExecutionSpeed, ExecutionSpeed);

    /// <summary>"Let Windows decide" — no explicit control.</summary>
    public static ThrottleState Default { get; } = new(0, 0);
}

public sealed record RunningProcess(int Pid, string Name, string? Path, DateTime? StartTimeUtc);

/// <summary>Original state of one background process RigTune throttled.</summary>
public sealed class ThrottledProcess
{
    public int Pid { get; set; }
    public string Name { get; set; } = string.Empty;
    public string AppId { get; set; } = string.Empty;
    public DateTime? StartTimeUtc { get; set; }
    public int OriginalPriorityClass { get; set; }
    public ThrottleState? OriginalThrottle { get; set; }
    public int? OriginalIoPriority { get; set; }
    public bool EcoQosApplied { get; set; }
    public bool IoPriorityLowered { get; set; }
    public bool Trimmed { get; set; }
}

/// <summary>Everything a session changed, persisted to gamemode.json before any change is made.</summary>
public sealed class GameSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset StartedAtUtc { get; set; }
    public bool IsManual { get; set; }
    public int? GamePid { get; set; }
    public string? GameExe { get; set; }
    public string? GameName { get; set; }
    public DateTime? GameStartTimeUtc { get; set; }
    public int? GameOriginalPriorityClass { get; set; }
    public Guid? OriginalPowerPlan { get; set; }
    public Guid? AppliedPowerPlan { get; set; }
    public List<ThrottledProcess> Throttled { get; set; } = new();
}

public enum GameModeState
{
    Idle,
    Active,
}

public sealed record GameSessionResult(bool Success, string Message, int Restored = 0, int Failed = 0);

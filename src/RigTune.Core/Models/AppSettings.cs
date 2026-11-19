using System.Text.Json.Serialization;

namespace RigTune.Core.Models;

/// <summary>Persisted to <c>settings.json</c>.</summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Danger Zone tweaks are hidden/locked unless explicitly enabled. Off by default.</summary>
    public bool DangerZoneEnabled { get; set; }

    /// <summary>Start-with-Windows via an elevated Task Scheduler logon task.</summary>
    public bool StartWithWindows { get; set; }

    public bool MinimizeToTrayOnClose { get; set; } = true;

    public bool CheckDriftOnLaunch { get; set; }

    /// <summary>Per-monitor pinned refresh rate (key = display device name, e.g. <c>\\.\DISPLAY1</c>).</summary>
    /// <summary>Restore drifted refresh rates automatically instead of asking.</summary>
    public bool AutoRestoreRefreshRate { get; set; }

    /// <summary>Watch process launches and start Game Mode automatically for detected games.</summary>
    public bool GameModeAutoDetect { get; set; } = true;

    /// <summary>Trim background working sets only when memory load reaches this percentage.</summary>
    public int RamTrimThresholdPercent { get; set; } = 80;

    public bool RamTrimEnabled { get; set; } = true;

    /// <summary>Switch power plan during a game session (off unless the user enables it).</summary>
    public bool SwitchPowerPlanDuringGames { get; set; }

    /// <summary>Plan used while gaming (default: High performance).</summary>
    public Guid GamingPowerPlan { get; set; } = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    /// <remarks>Populated in place on load so the case-insensitive comparer survives deserialization.</remarks>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, int> RefreshRateTargets { get; } = new(StringComparer.OrdinalIgnoreCase);
}

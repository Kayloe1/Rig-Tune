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
    /// <remarks>Populated in place on load so the case-insensitive comparer survives deserialization.</remarks>
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, int> RefreshRateTargets { get; } = new(StringComparer.OrdinalIgnoreCase);
}

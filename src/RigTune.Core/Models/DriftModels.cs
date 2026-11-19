namespace RigTune.Core.Models;

public enum DriftStatus
{
    /// <summary>Live state still equals what RigTune applied.</summary>
    InPlace,

    /// <summary>Something (usually a Windows or driver update) changed it back or elsewhere.</summary>
    Drifted,

    /// <summary>The target can't be read right now (e.g. NVIDIA driver unavailable, service removed).</summary>
    Unavailable,
}

/// <summary>One active snapshot compared with live state (SPEC §3.1 Drift Detector).</summary>
public sealed record DriftItem(
    Guid SnapshotId,
    string TweakId,
    string TweakName,
    string Target,
    string Expected,
    string Current,
    DriftStatus Status,
    bool RequiresRestart,
    string? Note = null)
{
    public bool IsDrifted => Status == DriftStatus.Drifted;
}

public sealed record DriftScanResult(IReadOnlyList<DriftItem> Items, DateTimeOffset ScannedAt)
{
    public IReadOnlyList<DriftItem> Drifted => Items.Where(i => i.IsDrifted).ToList();
    public int ActiveCount => Items.Count;
}

public sealed record ReapplyResult(Guid SnapshotId, bool Success, string Message);

// ------------------------------------------------------------------ profiles

/// <summary>Exportable personal tweak profile (JSON).</summary>
public sealed class TweakProfile
{
    public int SchemaVersion { get; set; } = 1;
    public string Application { get; set; } = "RigTune";
    public DateTimeOffset ExportedAt { get; set; }
    public string? MachineName { get; set; }
    public List<ProfileTweak> Tweaks { get; set; } = new();
}

public sealed class ProfileTweak
{
    public string Id { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? OptionId { get; set; }
}

public sealed record ProfileImportItem(string Id, string Name, bool Success, string Message);

public sealed record ProfileImportResult(IReadOnlyList<ProfileImportItem> Items)
{
    public int Applied => Items.Count(i => i.Success);
    public int Skipped => Items.Count(i => !i.Success);
}

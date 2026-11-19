namespace RigTune.Core.Models;

public enum AuditAction
{
    Applied,
    ApplyFailed,
    Reverted,
    RevertFailed,
    RevertAll,
    DriftDetected,
    Reapplied,
    Cleaned,
    Repaired,
    RestorePointCreated,
    RestorePointFailed,
    StartupChanged,
    Uninstalled,
    Updated,
    Info,
}

/// <summary>One line in <c>history.json</c>.</summary>
public sealed class AuditEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset TimestampUtc { get; set; }
    public AuditAction Action { get; set; }
    public string? TweakId { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string? Target { get; set; }
    public string? Details { get; set; }
    public bool Success { get; set; } = true;
    public bool RequiresRestart { get; set; }
}

public sealed class HistoryDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<AuditEntry> Entries { get; set; } = new();
}

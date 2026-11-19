using System.Text.Json.Serialization;

namespace RigTune.Core.Models;

public enum DiskCategory
{
    Games,
    Media,
    Apps,
    System,
    Other,
}

/// <summary>A folder (or a large file) in a scanned tree. Small files are aggregated per folder.</summary>
public sealed class DiskNode
{
    public DiskNode(string name, string fullPath, bool isDirectory, DiskCategory category, DiskNode? parent = null)
    {
        Name = name;
        FullPath = fullPath;
        IsDirectory = isDirectory;
        Category = category;
        Parent = parent;
    }

    public string Name { get; }
    public string FullPath { get; }
    public bool IsDirectory { get; }
    public DiskCategory Category { get; }

    [JsonIgnore]
    public DiskNode? Parent { get; internal set; }

    public long Size { get; internal set; }
    public long FileCount { get; internal set; }

    /// <summary>Bytes in files below the large-file threshold (not listed individually).</summary>
    public long SmallFilesBytes { get; internal set; }

    public int SmallFileCount { get; internal set; }

    /// <summary>True when the folder could not be read (access denied).</summary>
    public bool IsInaccessible { get; internal set; }

    public List<DiskNode> Children { get; } = new();
}

public sealed record ScanProgress(long Files, long Directories, long Bytes, long Skipped, string CurrentPath);

public sealed record DriveScanResult(
    string DriveRoot,
    DiskNode Root,
    DateTimeOffset ScannedAt,
    TimeSpan Duration,
    long SkippedFolders,
    IReadOnlyDictionary<DiskCategory, long> CategoryBytes);

public sealed class DriveSummary
{
    public string Drive { get; set; } = string.Empty;
    public long ScannedBytes { get; set; }
    public long Files { get; set; }
    public DateTimeOffset ScannedAt { get; set; }
    public Dictionary<DiskCategory, long> CategoryBytes { get; set; } = new();
}

/// <summary>Persisted to <c>disk_locks.json</c>: user "Important" locks + per-drive cached scan summaries.</summary>
public sealed class DiskLocksDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<string> LockedPaths { get; set; } = new();
    public Dictionary<string, DriveSummary> CachedSummaries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record DeletionCheck(bool Allowed, string Reason)
{
    public static DeletionCheck Ok { get; } = new(true, "OK");
}

public sealed record DeletionResult(string Path, bool Success, long BytesRecycled, string Message);

// ------------------------------------------------------------------ Space Advisor

public enum AdvisorGroup
{
    SafeToRemove,
    CheckFirst,
    Keep,
}

public enum AdvisorAction
{
    None,

    /// <summary>Send the folder's contents (not the folder) to the Recycle Bin.</summary>
    RecycleContents,

    /// <summary>Send the file/folder itself to the Recycle Bin.</summary>
    RecycleItem,

    /// <summary>Run a System Care cleanup (Windows-owned temp zones are hard-protected from Disk Analyzer deletes).</summary>
    RunCleanup,

    EmptyRecycleBin,

    /// <summary>Open Steam's own uninstaller (keeps Steam's library consistent).</summary>
    UninstallSteamGame,

    /// <summary>Windows.old is removed with Disk Cleanup (it is ACL-protected and too large for the Recycle Bin).</summary>
    OpenDiskCleanup,
}

public sealed record SpaceFinding(
    AdvisorGroup Group,
    string Title,
    string Path,
    long Bytes,
    string Reason,
    AdvisorAction Action = AdvisorAction.None,
    string? ActionArgument = null,
    IReadOnlyList<string>? ExtraPaths = null)
{
    /// <summary>Every path the action applies to.</summary>
    public IReadOnlyList<string> AllPaths => new[] { Path }.Concat(ExtraPaths ?? Array.Empty<string>()).ToList();

    public bool CanAct => Action != AdvisorAction.None;
}

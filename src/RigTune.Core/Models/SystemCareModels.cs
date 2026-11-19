namespace RigTune.Core.Models;

// ------------------------------------------------------------------ startup apps

public enum StartupLocation
{
    /// <summary>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</summary>
    CurrentUserRun,

    /// <summary>HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run (64-bit view)</summary>
    LocalMachineRun,

    /// <summary>HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run (32-bit view)</summary>
    LocalMachineRun32,

    /// <summary>%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup</summary>
    CurrentUserStartupFolder,

    /// <summary>%PROGRAMDATA%\Microsoft\Windows\Start Menu\Programs\StartUp</summary>
    AllUsersStartupFolder,
}

public sealed record StartupEntry(
    StartupLocation Location,
    string Name,
    string Command,
    string? Publisher,
    bool IsEnabled,
    DateTimeOffset? DisabledAt)
{
    public string Id => $"{Location}|{Name}";

    public string LocationDisplay => Location switch
    {
        StartupLocation.CurrentUserRun => @"HKCU\…\Run",
        StartupLocation.LocalMachineRun => @"HKLM\…\Run",
        StartupLocation.LocalMachineRun32 => @"HKLM\…\Run (32-bit)",
        StartupLocation.CurrentUserStartupFolder => "Startup folder (you)",
        _ => "Startup folder (all users)",
    };
}

// ------------------------------------------------------------------ cleanup

public enum CleanupKind
{
    Files,
    DnsCache,
    Memory,
}

/// <summary>A folder to clean: files matching <see cref="Pattern"/> older than <see cref="MinAge"/>.</summary>
public sealed record CleanupLocation(string Root, string Pattern = "*", bool Recursive = true, TimeSpan? MinAge = null);

public sealed record CleanupDefinition(
    string Id,
    string Name,
    string Description,
    CleanupKind Kind,
    IReadOnlyList<CleanupLocation> Locations,
    bool IsPermanent);

public sealed record CleanupEstimate(string Id, long Bytes, int Files, string? Note = null);

public sealed record CleanupResult(string Id, bool Success, long BytesFreed, int FilesDeleted, int FilesSkipped, string Message);

// ------------------------------------------------------------------ appx / winget

public enum AppxCategory
{
    SafeToRemove,
    Optional,
    Protected,
    Other,
}

public sealed record AppxPackageInfo(
    string Name,
    string FullName,
    string FamilyName,
    string DisplayName,
    string Publisher,
    string Version,
    string SignatureKind,
    bool IsFramework);

public sealed record AppxPackageClassified(AppxPackageInfo Package, AppxCategory Category, string Reason)
{
    public bool IsProtected => Category == AppxCategory.Protected;
}

public sealed record AppxRemovalResult(string PackageName, bool Success, bool Deprovisioned, string Message);

public sealed record WingetUpgrade(string Name, string Id, string Version, string Available, string Source,
    bool RequiresExplicitTargeting);

public sealed record WingetResult(string Id, bool Success, int ExitCode, string Message);

// ------------------------------------------------------------------ repair

public enum RepairStatus
{
    Healthy,
    Broken,
    NotApplicable,
    Unknown,
}

public enum RepairFixKind
{
    None,
    EnableDevice,
    RestartDevice,
    SetServiceStartType,
    StartService,
    RestartService,
    RevertSnapshot,
    DeleteRegistryValue,
    SetBootHypervisor,
    EnableFeature,
}

public sealed record RepairCheck(
    string Id,
    string Category,
    string Name,
    RepairStatus Status,
    string Detail,
    RepairFixKind FixKind = RepairFixKind.None,
    string? FixDescription = null,
    string? Target = null,
    string? FixValue = null,
    bool RequiresRestart = false,
    Guid? SnapshotId = null)
{
    public bool CanFix => Status == RepairStatus.Broken && FixKind != RepairFixKind.None;
}

public sealed record RepairResult(string CheckId, bool Success, string Message, bool RequiresRestart);

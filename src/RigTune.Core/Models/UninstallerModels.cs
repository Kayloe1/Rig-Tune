namespace RigTune.Core.Models;

/// <summary>One "Programs and Features" entry read from an Uninstall registry key.</summary>
public sealed record InstalledProgram
{
    public required RegistryHiveName Hive { get; init; }
    public required RegistryViewName View { get; init; }

    /// <summary>Full key path, e.g. <c>SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{GUID}</c>.</summary>
    public required string KeyPath { get; init; }

    public required string KeyName { get; init; }
    public required string DisplayName { get; init; }
    public string? Publisher { get; init; }
    public string? Version { get; init; }
    public DateOnly? InstallDate { get; init; }
    public long? EstimatedSizeBytes { get; init; }
    public string? InstallLocation { get; init; }
    public string? UninstallString { get; init; }
    public string? QuietUninstallString { get; init; }
    public string? DisplayIcon { get; init; }

    /// <summary>Windows Installer product code when this is an MSI install.</summary>
    public string? ProductCode { get; init; }

    /// <summary>True when Inno Setup wrote the entry (supports /VERYSILENT).</summary>
    public bool IsInnoSetup { get; init; }

    public bool IsMsi => ProductCode is not null;
    public bool IsPerUser => Hive == RegistryHiveName.HKCU;

    public string Id => $"{Hive}|{View}|{KeyName}";

    public string ScopeLabel => IsPerUser ? "Current user" : View == RegistryViewName.Registry32 ? "32-bit" : "64-bit";
}

public enum UninstallOutcome
{
    /// <summary>The uninstaller finished and the program's entry is gone.</summary>
    Removed,

    /// <summary>The uninstaller finished but the program is still registered (cancelled, or needs a restart).</summary>
    StillInstalled,

    /// <summary>The entry points to an uninstaller that no longer exists (broken entry).</summary>
    UninstallerMissing,

    /// <summary>The uninstaller could not be started or reported an error.</summary>
    Failed,

    /// <summary>Stopped waiting / skipped because the batch was cancelled.</summary>
    Cancelled,
}

public sealed record UninstallResult(InstalledProgram Program, UninstallOutcome Outcome, int? ExitCode, string Message, bool RestartRequired = false)
{
    /// <summary>Leftovers are only scanned once the program is really gone (or its entry is orphaned).</summary>
    public bool CanScanLeftovers => Outcome is UninstallOutcome.Removed or UninstallOutcome.UninstallerMissing;
}

public enum LeftoverKind
{
    Folder,
    File,
    RegistryKey,
}

public enum LeftoverConfidence
{
    /// <summary>Exact match on the install folder or the program's own name — pre-selected.</summary>
    High,

    /// <summary>Publisher-level match — shown for review, never pre-selected.</summary>
    Medium,
}

/// <summary>A folder or registry key that probably belonged to an uninstalled program.</summary>
public sealed record Leftover
{
    public required string ProgramName { get; init; }
    public required LeftoverKind Kind { get; init; }

    /// <summary>Folder path, or <c>HKLM\SOFTWARE\...</c> display path for registry keys.</summary>
    public required string Path { get; init; }

    public required string Reason { get; init; }
    public required LeftoverConfidence Confidence { get; init; }
    public long SizeBytes { get; init; }

    public RegistryHiveName Hive { get; init; }
    public RegistryViewName View { get; init; }
    public string? KeyPath { get; init; }

    public bool DefaultSelected => Confidence == LeftoverConfidence.High;

    public string Identity => Kind != LeftoverKind.RegistryKey
        ? "fs|" + Path.ToUpperInvariant()
        : $"reg|{Hive}|{(View == RegistryViewName.Registry32 ? "32" : "64")}|{KeyPath?.ToUpperInvariant()}";
}

public sealed record LeftoverRemovalResult(Leftover Leftover, bool Success, string Message, string? BackupFile = null);

/// <summary>A resolved command line for an uninstaller.</summary>
public sealed record UninstallCommand(string FileName, string Arguments, bool IsQuiet, string Description);

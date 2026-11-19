namespace RigTune.Core.Models;

/// <summary>Values of <c>RestorePointType</c> (srrestoreptapi.h / WMI SystemRestore).</summary>
public enum RestorePointType
{
    ApplicationInstall = 0,
    ApplicationUninstall = 1,
    DeviceDriverInstall = 10,
    ModifySettings = 12,
    CancelledOperation = 13,
    SystemCheckpoint = 16,
}

public sealed record RestorePointInfo(uint SequenceNumber, string Description, int RestorePointType,
    DateTimeOffset? CreatedAt)
{
    public string OriginTag => RestorePointOrigin.Describe(RestorePointType);
}

public sealed record RestorePointCreateResult(bool Success, string Message, long? SequenceNumber, string Method);

public static class RestorePointOrigin
{
    /// <summary>Maps <c>RestorePointType</c> to a human-readable origin tag (SPEC §3.3.1).</summary>
    public static string Describe(int restorePointType) => restorePointType switch
    {
        0 => "Application Install",
        1 => "Application Uninstall",
        10 => "Device Driver Install",
        12 => "Modify Settings",
        13 => "Cancelled Operation",
        16 => "System Checkpoint / Update",
        _ => $"Other ({restorePointType})",
    };
}

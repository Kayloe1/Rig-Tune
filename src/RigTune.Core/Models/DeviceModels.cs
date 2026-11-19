namespace RigTune.Core.Models;

public sealed record DeviceInfo(
    string InstanceId,
    string Name,
    string Class,
    Guid ClassGuid,
    string Enumerator,
    string? DriverKey,
    string? Manufacturer,
    IReadOnlyList<string> HardwareIds,
    bool IsDisabled,
    bool HasProblem);

public sealed record DeviceCandidate(DeviceInfo Device, string Rating, string? Note);

public enum InterruptDeviceCategory
{
    Gpu,
    Usb,
    Network,
    Audio,
    Other,
}

public sealed record PciDeviceInfo(
    DeviceInfo Device,
    InterruptDeviceCategory Category,
    bool? MsiSupported,
    int? DevicePolicy,
    ulong? AssignmentSetOverride,
    int? DevicePriority);

public sealed record NetworkAdapterInfo(
    DeviceInfo Device,
    string ClassKeyPath,
    string? NetCfgInstanceId,
    string? InterruptModeration,
    bool SupportsInterruptModeration,
    string? Rss,
    bool SupportsRss);

public sealed record PowerSettingInfo(Guid Subgroup, Guid Setting, string FriendlyName, uint AcValue, uint DcValue,
    bool IsHidden);

public sealed record AudioEndpointInfo(
    string Id,
    string Name,
    string Flow,
    bool IsDefault,
    int SampleRate,
    int Channels,
    int BitsPerSample,
    double DefaultPeriodMs,
    double MinimumPeriodMs);

public sealed record TimerResolutionInfo(double MinimumMs, double MaximumMs, double CurrentMs);

public sealed record TimerResolutionResult(TimerResolutionInfo Before, TimerResolutionInfo After, double Sleep1BeforeMs,
    double Sleep1AfterMs, bool Active);

public sealed record PowerHardwareChecks(bool? BatteryPresent, bool? ModernStandby, bool? HibernationFilePresent, bool? S3Supported)
{
    public string Summary =>
        $"Battery: {(BatteryPresent switch { true => "present", false => "none (desktop)", _ => "unknown" })} · " +
        $"Sleep: {(ModernStandby == true ? "S0 Low Power Idle" : S3Supported == true ? "S3" : "unknown")} · " +
        $"Hibernation: {(HibernationFilePresent switch { true => "on", false => "off", _ => "unknown" })}";
}

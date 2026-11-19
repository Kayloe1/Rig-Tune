using RigTune.Core.Models;

namespace RigTune.Core.Runners;

/// <summary>NTFS behaviour settings (registry-backed, equivalent to <c>fsutil behavior</c>) and SSD re-TRIM.</summary>
public static class NtfsRunner
{
    private const string FileSystemKey = @"SYSTEM\CurrentControlSet\Control\FileSystem";

    /// <summary>Maps an NTFS setting name to its registry value.</summary>
    public static RegistryTarget? SettingTarget(string setting) => setting.ToLowerInvariant() switch
    {
        "disable8dot3" => Target("NtfsDisable8dot3NameCreation"),
        "disablelastaccess" => Target("NtfsDisableLastAccessUpdate"),
        "memoryusage" => Target("NtfsMemoryUsage"),
        _ => null,
    };

    /// <summary>Runs a ReTrim (never a defrag) on every fixed NTFS volume.</summary>
    public static IReadOnlyList<string> RetrimAllVolumes()
    {
        var log = new List<string>();
        foreach (DriveInfo drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string volume = drive.Name.TrimEnd('\\');
            CommandResult result = ProcessRunner.Run("defrag", $"{volume} /L /U", TimeSpan.FromMinutes(15));
            log.Add($"{volume}: {(result.Succeeded() ? "ReTrim completed" : result.Describe())}");
        }

        return log;
    }

    private static RegistryTarget Target(string valueName) =>
        new(RegistryHiveName.HKLM, RegistryViewName.Registry64, FileSystemKey, valueName);
}

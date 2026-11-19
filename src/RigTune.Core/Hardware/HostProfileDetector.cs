using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using RigTune.Core.Models;
using RigTune.Core.Native;

namespace RigTune.Core.Hardware;

/// <summary>
/// Detects the host profile (SPEC §1 / §3.3.4): OS build, Intel hybrid P-core/E-core topology,
/// active microcode revision, GPUs, RAM, volumes and physical disks. Every probe is best-effort
/// so one failing source never hides the rest.
/// </summary>
public sealed class HostProfileDetector
{
    private const string DisplayClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    private readonly object _cpuSampleLock = new();
    private ulong _lastIdle, _lastKernel, _lastUser;

    public Task<HostProfile> DetectAsync(CancellationToken cancellationToken = default) =>
        Task.Run(Detect, cancellationToken);

    public HostProfile Detect() => new(
        Environment.MachineName,
        DetectOs(),
        DetectCpu(),
        DetectGpus(),
        DetectMemory(),
        DetectVolumes(),
        DetectPhysicalDisks(),
        DetectHypervisor(),
        DateTimeOffset.Now);

    public HostLiveStats SampleLiveStats() => new(
        SampleCpuUsage(),
        DetectMemory(),
        TimeSpan.FromMilliseconds(Environment.TickCount64),
        DetectVolumes(),
        DateTimeOffset.Now);

    // ---------------------------------------------------------------- OS

    public static OsInfo DetectOs()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            int build = int.TryParse(key?.GetValue("CurrentBuildNumber") as string, out int b) ? b : Environment.OSVersion.Version.Build;
            int ubr = key?.GetValue("UBR") is int u ? u : 0;
            string product = key?.GetValue("ProductName") as string ?? "Windows";
            // ProductName still says "Windows 10" on Windows 11 builds.
            if (build >= 22000)
            {
                product = product.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
            }

            return new OsInfo(
                product,
                key?.GetValue("EditionID") as string ?? string.Empty,
                key?.GetValue("DisplayVersion") as string ?? string.Empty,
                build,
                ubr,
                RuntimeInformation.OSArchitecture.ToString());
        }
        catch (Exception ex) when (IsProbeFailure(ex))
        {
            Version v = Environment.OSVersion.Version;
            return new OsInfo("Windows", string.Empty, string.Empty, v.Build, 0, RuntimeInformation.OSArchitecture.ToString());
        }
    }

    // ---------------------------------------------------------------- CPU

    public static CpuInfo DetectCpu()
    {
        string name = "Unknown CPU";
        uint? microcode = null, previous = null;
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            name = (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? name;
            microcode = MicrocodeParser.Parse(key?.GetValue("Update Revision") as byte[]);
            previous = MicrocodeParser.Parse(key?.GetValue("Previous Update Revision") as byte[]);
        }
        catch (Exception ex) when (IsProbeFailure(ex))
        {
        }

        return new CpuInfo(name, DetectTopology(), microcode, previous);
    }

    public static CpuTopology DetectTopology()
    {
        if (!OperatingSystem.IsWindows())
        {
            return CpuTopology.Empty;
        }

        uint length = 0;
        Kernel32.GetLogicalProcessorInformationEx(Kernel32.RelationProcessorCore, IntPtr.Zero, ref length);
        if (length == 0)
        {
            return CpuTopology.Empty;
        }

        IntPtr buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!Kernel32.GetLogicalProcessorInformationEx(Kernel32.RelationProcessorCore, buffer, ref length))
            {
                throw new Win32Exception();
            }

            var managed = new byte[length];
            Marshal.Copy(buffer, managed, 0, (int)length);
            return CpuTopologyParser.Parse(managed);
        }
        catch (Win32Exception)
        {
            return CpuTopology.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // ---------------------------------------------------------------- GPU

    public static IReadOnlyList<GpuInfo> DetectGpus()
    {
        var gpus = new List<GpuInfo>();
        try
        {
            using RegistryKey? cls = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            if (cls is null)
            {
                return gpus;
            }

            foreach (string sub in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
            {
                using RegistryKey? dev = cls.OpenSubKey(sub);
                if (dev?.GetValue("DriverDesc") is not string desc || string.IsNullOrWhiteSpace(desc))
                {
                    continue;
                }

                ulong? vram = dev.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long q => unchecked((ulong)q),
                    int d => unchecked((uint)d),
                    byte[] bytes when bytes.Length >= 8 => BitConverter.ToUInt64(bytes, 0),
                    byte[] bytes when bytes.Length >= 4 => BitConverter.ToUInt32(bytes, 0),
                    _ => dev.GetValue("HardwareInformation.MemorySize") switch
                    {
                        int d => unchecked((uint)d),
                        byte[] bytes when bytes.Length >= 4 => BitConverter.ToUInt32(bytes, 0),
                        _ => null,
                    },
                };

                string provider = dev.GetValue("ProviderName") as string ?? string.Empty;
                string driverVersion = dev.GetValue("DriverVersion") as string ?? string.Empty;
                DateTimeOffset? driverDate = DateTime.TryParse(dev.GetValue("DriverDate") as string,
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTime dd)
                    ? new DateTimeOffset(dd)
                    : null;
                string vendor = desc.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                                provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                    ? "NVIDIA"
                    : provider;
                gpus.Add(new GpuInfo(desc, vendor, vram, driverVersion,
                    vendor == "NVIDIA" ? ToNvidiaDriverVersion(driverVersion) : null, driverDate));
            }
        }
        catch (Exception ex) when (IsProbeFailure(ex))
        {
        }

        // Physical NVIDIA adapter first; de-duplicate identical descriptors.
        return gpus
            .GroupBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(g => g.Vendor == "NVIDIA")
            .ThenByDescending(g => g.DedicatedMemoryBytes ?? 0)
            .ToList();
    }

    /// <summary>Converts a Windows driver version (e.g. 32.0.15.6094) to NVIDIA's (560.94).</summary>
    public static string? ToNvidiaDriverVersion(string windowsDriverVersion)
    {
        string[] parts = windowsDriverVersion.Split('.');
        if (parts.Length < 4)
        {
            return null;
        }

        string digits = parts[2] + parts[3].PadLeft(4, '0');
        if (digits.Length < 5 || !digits.All(char.IsDigit))
        {
            return null;
        }

        string last5 = digits[^5..];
        return $"{int.Parse(last5[..3], System.Globalization.CultureInfo.InvariantCulture)}.{last5[3..]}";
    }

    // ---------------------------------------------------------------- Memory / storage

    public static MemoryInfo DetectMemory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new MemoryInfo(0, 0, 0);
        }

        var status = new Kernel32.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Kernel32.MEMORYSTATUSEX>() };
        return Kernel32.GlobalMemoryStatusEx(ref status)
            ? new MemoryInfo(status.ullTotalPhys, status.ullAvailPhys, status.dwMemoryLoad)
            : new MemoryInfo(0, 0, 0);
    }

    public static IReadOnlyList<VolumeInfo> DetectVolumes()
    {
        var volumes = new List<VolumeInfo>();
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                {
                    continue;
                }

                volumes.Add(new VolumeInfo(drive.Name.TrimEnd('\\'), drive.VolumeLabel, drive.DriveFormat,
                    drive.TotalSize, drive.TotalFreeSpace));
            }
            catch (Exception ex) when (IsProbeFailure(ex))
            {
            }
        }

        return volumes;
    }

    public static IReadOnlyList<PhysicalDiskInfo> DetectPhysicalDisks()
    {
        var disks = new List<PhysicalDiskInfo>();
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT FriendlyName, BusType, MediaType, Size FROM MSFT_PhysicalDisk"));
            using ManagementObjectCollection results = searcher.Get();
            foreach (ManagementBaseObject disk in results)
            {
                using (disk)
                {
                    disks.Add(new PhysicalDiskInfo(
                        (disk["FriendlyName"] as string)?.Trim() ?? "Disk",
                        MapBusType(Convert.ToInt32(disk["BusType"] ?? 0)),
                        MapMediaType(Convert.ToInt32(disk["MediaType"] ?? 0)),
                        Convert.ToUInt64(disk["Size"] ?? 0UL)));
                }
            }
        }
        catch (Exception ex) when (IsProbeFailure(ex))
        {
        }

        return disks;
    }

    public static bool? DetectHypervisor()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT HypervisorPresent FROM Win32_ComputerSystem");
            using ManagementObjectCollection results = searcher.Get();
            foreach (ManagementBaseObject cs in results)
            {
                using (cs)
                {
                    return cs["HypervisorPresent"] as bool?;
                }
            }
        }
        catch (Exception ex) when (IsProbeFailure(ex))
        {
        }

        return null;
    }

    private static string MapBusType(int busType) => busType switch
    {
        7 => "USB",
        8 => "RAID",
        11 => "SATA",
        17 => "NVMe",
        _ => $"Bus {busType}",
    };

    private static string MapMediaType(int mediaType) => mediaType switch
    {
        3 => "HDD",
        4 => "SSD",
        5 => "SCM",
        _ => "Unspecified",
    };

    // ---------------------------------------------------------------- CPU usage

    private double SampleCpuUsage()
    {
        if (!OperatingSystem.IsWindows() ||
            !Kernel32.GetSystemTimes(out Kernel32.FILETIME idle, out Kernel32.FILETIME kernel, out Kernel32.FILETIME user))
        {
            return 0;
        }

        lock (_cpuSampleLock)
        {
            ulong idleDelta = idle.Value - _lastIdle;
            ulong kernelDelta = kernel.Value - _lastKernel;
            ulong userDelta = user.Value - _lastUser;
            bool first = _lastKernel == 0;
            _lastIdle = idle.Value;
            _lastKernel = kernel.Value;
            _lastUser = user.Value;

            ulong total = kernelDelta + userDelta; // kernel time includes idle time
            if (first || total == 0)
            {
                return 0;
            }

            return Math.Clamp(100.0 * (total - idleDelta) / total, 0, 100);
        }
    }

    private static bool IsProbeFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ManagementException
            or COMException or PlatformNotSupportedException or InvalidOperationException or FormatException
            or TypeInitializationException;
}

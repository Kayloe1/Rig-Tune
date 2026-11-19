namespace RigTune.Core.Models;

public sealed record OsInfo(string ProductName, string Edition, string DisplayVersion, int Build, int Ubr, string Architecture)
{
    public string BuildString => Ubr > 0 ? $"{Build}.{Ubr}" : Build.ToString();
    public string Summary => $"{ProductName} {DisplayVersion} (Build {BuildString})".Replace("  ", " ");
}

public sealed record CpuCore(int CoreIndex, byte EfficiencyClass, bool IsPerformanceCore, bool HasSmt,
    IReadOnlyList<int> LogicalProcessors);

public sealed record CpuTopology(IReadOnlyList<CpuCore> Cores)
{
    public static CpuTopology Empty { get; } = new(Array.Empty<CpuCore>());

    public bool IsHybrid => Cores.Select(c => c.EfficiencyClass).Distinct().Count() > 1;
    public int PhysicalCoreCount => Cores.Count;
    public int PerformanceCoreCount => Cores.Count(c => c.IsPerformanceCore);
    public int EfficientCoreCount => Cores.Count(c => !c.IsPerformanceCore);
    public int LogicalProcessorCount => Cores.Sum(c => c.LogicalProcessors.Count);

    public IReadOnlyList<int> PerformanceLogicalProcessors =>
        Cores.Where(c => c.IsPerformanceCore).SelectMany(c => c.LogicalProcessors).Order().ToList();

    public IReadOnlyList<int> EfficientLogicalProcessors =>
        Cores.Where(c => !c.IsPerformanceCore).SelectMany(c => c.LogicalProcessors).Order().ToList();

    /// <summary>Compact range text, e.g. "0–15" or "0–3, 8".</summary>
    public static string FormatRanges(IReadOnlyList<int> indices)
    {
        if (indices.Count == 0)
        {
            return "—";
        }

        var parts = new List<string>();
        int start = indices[0], prev = indices[0];
        for (int i = 1; i <= indices.Count; i++)
        {
            if (i < indices.Count && indices[i] == prev + 1)
            {
                prev = indices[i];
                continue;
            }

            parts.Add(start == prev ? $"{start}" : $"{start}–{prev}");
            if (i < indices.Count)
            {
                start = prev = indices[i];
            }
        }

        return string.Join(", ", parts);
    }
}

public sealed record CpuInfo(string Name, CpuTopology Topology, uint? MicrocodeRevision, uint? PreviousMicrocodeRevision)
{
    public string MicrocodeDisplay => MicrocodeRevision is { } r ? $"0x{r:X}" : "Unknown";
}

public sealed record GpuInfo(string Name, string Vendor, ulong? DedicatedMemoryBytes, string DriverVersion,
    string? VendorDriverVersion, DateTimeOffset? DriverDate);

public sealed record MemoryInfo(ulong TotalBytes, ulong AvailableBytes, uint LoadPercent);

public sealed record VolumeInfo(string Name, string Label, string Format, long TotalBytes, long FreeBytes)
{
    public long UsedBytes => TotalBytes - FreeBytes;
    public double UsedPercent => TotalBytes > 0 ? 100.0 * UsedBytes / TotalBytes : 0;
}

public sealed record PhysicalDiskInfo(string FriendlyName, string BusType, string MediaType, ulong SizeBytes);

public sealed record HostProfile(
    string MachineName,
    OsInfo Os,
    CpuInfo Cpu,
    IReadOnlyList<GpuInfo> Gpus,
    MemoryInfo Memory,
    IReadOnlyList<VolumeInfo> Volumes,
    IReadOnlyList<PhysicalDiskInfo> PhysicalDisks,
    bool? HypervisorPresent,
    DateTimeOffset DetectedAt);

public sealed record HostLiveStats(double CpuUsagePercent, MemoryInfo Memory, TimeSpan Uptime,
    IReadOnlyList<VolumeInfo> Volumes, DateTimeOffset SampledAt);

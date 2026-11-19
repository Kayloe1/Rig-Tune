using System.Buffers.Binary;
using RigTune.Core.Hardware;
using RigTune.Core.Models;

namespace RigTune.Core.Tests;

public sealed class HardwareParsingTests
{
    /// <summary>Builds a RelationProcessorCore record exactly as GetLogicalProcessorInformationEx lays it out.</summary>
    private static byte[] CoreRecord(byte efficiencyClass, bool smt, params int[] logical)
    {
        const int size = 48; // 8 header + 24 PROCESSOR_RELATIONSHIP fixed part + 16 GROUP_AFFINITY
        var rec = new byte[size];
        BinaryPrimitives.WriteInt32LittleEndian(rec, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(4), size);
        rec[8] = smt ? (byte)1 : (byte)0;
        rec[9] = efficiencyClass;
        BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(30), 1);
        ulong mask = logical.Aggregate(0UL, (m, lp) => m | (1UL << lp));
        BinaryPrimitives.WriteUInt64LittleEndian(rec.AsSpan(32), mask);
        return rec;
    }

    [Fact]
    public void Parses_i7_14700F_hybrid_topology_8P_12E_28_threads()
    {
        var buffer = new List<byte>();
        for (int p = 0; p < 8; p++)
        {
            buffer.AddRange(CoreRecord(1, smt: true, 2 * p, 2 * p + 1));
        }

        for (int e = 0; e < 12; e++)
        {
            buffer.AddRange(CoreRecord(0, smt: false, 16 + e));
        }

        CpuTopology topo = CpuTopologyParser.Parse(buffer.ToArray());

        Assert.True(topo.IsHybrid);
        Assert.Equal(20, topo.PhysicalCoreCount);
        Assert.Equal(8, topo.PerformanceCoreCount);
        Assert.Equal(12, topo.EfficientCoreCount);
        Assert.Equal(28, topo.LogicalProcessorCount);
        Assert.Equal(Enumerable.Range(0, 16), topo.PerformanceLogicalProcessors);
        Assert.Equal(Enumerable.Range(16, 12), topo.EfficientLogicalProcessors);
        Assert.Equal("0–15", CpuTopology.FormatRanges(topo.PerformanceLogicalProcessors));
        Assert.Equal("16–27", CpuTopology.FormatRanges(topo.EfficientLogicalProcessors));
    }

    [Fact]
    public void Non_hybrid_cpu_treats_every_core_as_performance()
    {
        byte[] buffer = CoreRecord(0, true, 0, 1).Concat(CoreRecord(0, true, 2, 3)).ToArray();

        CpuTopology topo = CpuTopologyParser.Parse(buffer);

        Assert.False(topo.IsHybrid);
        Assert.Equal(2, topo.PerformanceCoreCount);
        Assert.Equal(0, topo.EfficientCoreCount);
    }

    [Fact]
    public void Truncated_buffer_does_not_throw()
    {
        byte[] buffer = CoreRecord(1, true, 0, 1)[..20];

        Assert.Same(CpuTopology.Empty, CpuTopologyParser.Parse(buffer));
    }

    [Theory]
    [InlineData(new byte[] { 0, 0, 0, 0, 0x2B, 0x01, 0, 0 }, 0x12Bu)]
    [InlineData(new byte[] { 0, 0, 0, 0, 0x29, 0x01, 0, 0 }, 0x129u)]
    [InlineData(new byte[] { 0xC5, 0x00, 0x00, 0x00 }, 0xC5u)]
    public void Parses_intel_microcode_update_revision(byte[] raw, uint expected) =>
        Assert.Equal(expected, MicrocodeParser.Parse(raw));

    [Fact]
    public void Missing_or_zero_microcode_is_null()
    {
        Assert.Null(MicrocodeParser.Parse(null));
        Assert.Null(MicrocodeParser.Parse(new byte[8]));
    }

    [Theory]
    [InlineData("32.0.15.6094", "560.94")]
    [InlineData("32.0.15.7283", "572.83")]
    [InlineData("31.0.15.3623", "536.23")]
    [InlineData("garbage", null)]
    public void Converts_windows_driver_version_to_nvidia_version(string windows, string? expected) =>
        Assert.Equal(expected, HostProfileDetector.ToNvidiaDriverVersion(windows));

    [Theory]
    [InlineData(0, "Application Install")]
    [InlineData(1, "Application Uninstall")]
    [InlineData(10, "Device Driver Install")]
    [InlineData(12, "Modify Settings")]
    [InlineData(13, "Cancelled Operation")]
    [InlineData(16, "System Checkpoint / Update")]
    [InlineData(99, "Other (99)")]
    public void Maps_restore_point_types_to_origin_tags(int type, string tag) =>
        Assert.Equal(tag, RestorePointOrigin.Describe(type));
}

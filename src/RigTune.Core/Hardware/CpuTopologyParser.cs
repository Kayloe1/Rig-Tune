using System.Buffers.Binary;
using RigTune.Core.Models;

namespace RigTune.Core.Hardware;

/// <summary>
/// Parses the raw buffer returned by <c>GetLogicalProcessorInformationEx(RelationProcessorCore)</c>.
/// Layout per record (x64): Relationship(4) Size(4) | PROCESSOR_RELATIONSHIP: Flags(1) EfficiencyClass(1)
/// Reserved(20) GroupCount(2) pad(2) GROUP_AFFINITY[GroupCount] { Mask(8) Group(2) Reserved(6) }.
/// </summary>
public static class CpuTopologyParser
{
    private const int RelationProcessorCore = 0;
    private const byte LtpPcSmt = 0x1;
    private const int ProcessorRelationshipOffset = 8;
    private const int GroupCountOffset = ProcessorRelationshipOffset + 22;
    private const int GroupMaskOffset = ProcessorRelationshipOffset + 24;
    private const int GroupAffinitySize = 16;

    public static CpuTopology Parse(ReadOnlySpan<byte> buffer)
    {
        var raw = new List<(byte Efficiency, bool Smt, List<int> Logical)>();
        int offset = 0;
        while (offset + 8 <= buffer.Length)
        {
            int relationship = BinaryPrimitives.ReadInt32LittleEndian(buffer[offset..]);
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer[(offset + 4)..]);
            if (size <= 0 || offset + size > buffer.Length)
            {
                break;
            }

            if (relationship == RelationProcessorCore && size >= GroupMaskOffset)
            {
                ReadOnlySpan<byte> rec = buffer.Slice(offset, size);
                byte flags = rec[ProcessorRelationshipOffset];
                byte efficiency = rec[ProcessorRelationshipOffset + 1];
                int groupCount = BinaryPrimitives.ReadUInt16LittleEndian(rec[GroupCountOffset..]);
                var logical = new List<int>();
                for (int g = 0; g < groupCount; g++)
                {
                    int at = GroupMaskOffset + g * GroupAffinitySize;
                    if (at + 10 > rec.Length)
                    {
                        break;
                    }

                    ulong mask = BinaryPrimitives.ReadUInt64LittleEndian(rec[at..]);
                    int group = BinaryPrimitives.ReadUInt16LittleEndian(rec[(at + 8)..]);
                    for (int bit = 0; bit < 64; bit++)
                    {
                        if ((mask & (1UL << bit)) != 0)
                        {
                            logical.Add(group * 64 + bit);
                        }
                    }
                }

                raw.Add((efficiency, (flags & LtpPcSmt) != 0, logical));
            }

            offset += size;
        }

        if (raw.Count == 0)
        {
            return CpuTopology.Empty;
        }

        // Higher EfficiencyClass = more performant. On non-hybrid CPUs all cores share one class
        // and are all treated as performance cores.
        byte maxClass = raw.Max(r => r.Efficiency);
        var cores = raw
            .OrderBy(r => r.Logical.Count > 0 ? r.Logical.Min() : int.MaxValue)
            .Select((r, i) => new CpuCore(i, r.Efficiency, r.Efficiency == maxClass, r.Smt, r.Logical))
            .ToList();
        return new CpuTopology(cores);
    }
}

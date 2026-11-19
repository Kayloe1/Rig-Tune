using System.Buffers.Binary;

namespace RigTune.Core.Hardware;

/// <summary>
/// Parses <c>HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0\Update Revision</c>.
/// On Intel the value is an 8-byte REG_BINARY whose high DWORD holds the revision
/// (e.g. <c>00 00 00 00 2B 01 00 00</c> → <c>0x12B</c>).
/// </summary>
public static class MicrocodeParser
{
    public static uint? Parse(byte[]? raw)
    {
        if (raw is null || raw.Length < 4)
        {
            return null;
        }

        if (raw.Length >= 8)
        {
            uint high = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4, 4));
            if (high != 0)
            {
                return high;
            }
        }

        uint low = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0, 4));
        return low != 0 ? low : null;
    }
}

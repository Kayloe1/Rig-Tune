using System.Buffers.Binary;

namespace RigTune.Core.Services.SystemCare;

/// <summary>
/// Encodes/decodes the 12-byte REG_BINARY values under <c>Explorer\StartupApproved\Run|Run32|StartupFolder</c>
/// that Task Manager uses to enable/disable startup entries without deleting them.
/// Byte 0: even (0x02, 0x06) = enabled, odd (0x03, 0x07, 0x01) = disabled.
/// Bytes 4–11: FILETIME (UTC) when the entry was disabled (zero when enabled).
/// </summary>
public static class StartupApprovedFlags
{
    public const int Length = 12;
    public const byte EnabledMarker = 0x02;
    public const byte DisabledMarker = 0x03;

    /// <summary>A missing or malformed value means Windows treats the entry as enabled.</summary>
    public static bool IsEnabled(byte[]? data) => data is null || data.Length == 0 || (data[0] & 0x01) == 0;

    public static DateTimeOffset? DisabledAt(byte[]? data)
    {
        if (IsEnabled(data) || data!.Length < Length)
        {
            return null;
        }

        long fileTime = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(4, 8));
        if (fileTime <= 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromFileTime(fileTime).ToUniversalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public static byte[] Encode(bool enabled, DateTimeOffset? timestamp = null)
    {
        var data = new byte[Length];
        data[0] = enabled ? EnabledMarker : DisabledMarker;
        if (!enabled)
        {
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(4, 8), (timestamp ?? DateTimeOffset.UtcNow).ToFileTime());
        }

        return data;
    }
}

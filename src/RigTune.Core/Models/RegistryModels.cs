using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace RigTune.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RegistryHiveName
{
    HKLM,
    HKCU,
    HKCR,
    HKU,
    HKCC,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RegistryViewName
{
    Default,
    Registry64,
    Registry32,
}

/// <summary>Identifies a single registry value.</summary>
public sealed record RegistryTarget(RegistryHiveName Hive, RegistryViewName View, string KeyPath, string ValueName)
{
    public string Display => $@"{Hive}\{KeyPath}\{(ValueName.Length == 0 ? "(Default)" : ValueName)}" +
                             (View == RegistryViewName.Registry32 ? " [32-bit view]" : string.Empty);

    /// <summary>Case-insensitive identity used to de-duplicate snapshots of the same value.</summary>
    public string IdentityKey =>
        $"reg|{Hive}|{View}|{KeyPath.Trim('\\').ToUpperInvariant()}|{ValueName.ToUpperInvariant()}";
}

/// <summary>
/// A registry value captured exactly — kind plus raw typed payload — so it can be
/// written back bit-for-bit. Only the field matching <see cref="Kind"/> is populated.
/// </summary>
public sealed class RegistryValueData : IEquatable<RegistryValueData>
{
    public RegistryValueKind Kind { get; set; }
    public uint? DWord { get; set; }
    public ulong? QWord { get; set; }
    public string? String { get; set; }
    public string[]? MultiString { get; set; }
    public string? BinaryBase64 { get; set; }

    public static RegistryValueData FromDWord(uint value) => new() { Kind = RegistryValueKind.DWord, DWord = value };
    public static RegistryValueData FromQWord(ulong value) => new() { Kind = RegistryValueKind.QWord, QWord = value };
    public static RegistryValueData FromString(string value) => new() { Kind = RegistryValueKind.String, String = value };
    public static RegistryValueData FromExpandString(string value) => new() { Kind = RegistryValueKind.ExpandString, String = value };
    public static RegistryValueData FromMultiString(IEnumerable<string> value) => new() { Kind = RegistryValueKind.MultiString, MultiString = value.ToArray() };
    public static RegistryValueData FromBinary(byte[] value) => new() { Kind = RegistryValueKind.Binary, BinaryBase64 = Convert.ToBase64String(value) };

    /// <summary>Builds from the object returned by <c>RegistryKey.GetValue</c> + its kind.</summary>
    public static RegistryValueData FromRaw(object raw, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => FromDWord(unchecked((uint)Convert.ToInt32(raw))),
        RegistryValueKind.QWord => FromQWord(unchecked((ulong)Convert.ToInt64(raw))),
        RegistryValueKind.String => FromString((string)raw),
        RegistryValueKind.ExpandString => FromExpandString((string)raw),
        RegistryValueKind.MultiString => FromMultiString((string[])raw),
        RegistryValueKind.Binary => FromBinary((byte[])raw),
        // REG_NONE and exotic kinds (REG_RESOURCE_LIST, ...) come back as raw bytes.
        _ => new RegistryValueData
        {
            Kind = kind,
            BinaryBase64 = Convert.ToBase64String(raw as byte[] ?? Array.Empty<byte>()),
        },
    };

    /// <summary>Converts to the object/kind pair accepted by <c>RegistryKey.SetValue</c>.</summary>
    public (object Value, RegistryValueKind Kind) ToRaw() => Kind switch
    {
        RegistryValueKind.DWord => (unchecked((int)(DWord ?? 0)), RegistryValueKind.DWord),
        RegistryValueKind.QWord => (unchecked((long)(QWord ?? 0)), RegistryValueKind.QWord),
        RegistryValueKind.String => (String ?? string.Empty, RegistryValueKind.String),
        RegistryValueKind.ExpandString => (String ?? string.Empty, RegistryValueKind.ExpandString),
        RegistryValueKind.MultiString => (MultiString ?? Array.Empty<string>(), RegistryValueKind.MultiString),
        RegistryValueKind.Binary => (GetBytes(), RegistryValueKind.Binary),
        // Unknown kinds are written back as REG_NONE bytes (closest exact round-trip .NET exposes).
        _ => (GetBytes(), RegistryValueKind.None),
    };

    public byte[] GetBytes() => BinaryBase64 is null ? Array.Empty<byte>() : Convert.FromBase64String(BinaryBase64);

    public string ToDisplayString() => Kind switch
    {
        RegistryValueKind.DWord => $"0x{DWord:X8} ({DWord})",
        RegistryValueKind.QWord => $"0x{QWord:X16} ({QWord})",
        RegistryValueKind.String or RegistryValueKind.ExpandString => $"\"{String}\"",
        RegistryValueKind.MultiString => string.Join(" | ", MultiString ?? Array.Empty<string>()),
        _ => Convert.ToHexString(GetBytes()),
    };

    public bool Equals(RegistryValueData? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return Kind == other.Kind
               && DWord == other.DWord
               && QWord == other.QWord
               && string.Equals(String, other.String, StringComparison.Ordinal)
               && (MultiString ?? Array.Empty<string>()).SequenceEqual(other.MultiString ?? Array.Empty<string>(), StringComparer.Ordinal)
               && GetBytes().AsSpan().SequenceEqual(other.GetBytes());
    }

    public override bool Equals(object? obj) => Equals(obj as RegistryValueData);

    public override int GetHashCode() => HashCode.Combine(Kind, DWord, QWord, String);

    public override string ToString() => $"{Kind}: {ToDisplayString()}";
}

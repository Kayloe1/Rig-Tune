using System.Globalization;
using System.Text.Json;
using Microsoft.Win32;
using RigTune.Core.Models;

namespace RigTune.Core.Services;

/// <summary>Converts catalog JSON values into typed registry data and numbers.</summary>
public static class TweakValueParser
{
    public static RegistryHiveName ParseHive(string? hive) => (hive ?? string.Empty).ToUpperInvariant() switch
    {
        "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHiveName.HKLM,
        "HKCU" or "HKEY_CURRENT_USER" => RegistryHiveName.HKCU,
        "HKCR" or "HKEY_CLASSES_ROOT" => RegistryHiveName.HKCR,
        "HKU" or "HKEY_USERS" => RegistryHiveName.HKU,
        "HKCC" or "HKEY_CURRENT_CONFIG" => RegistryHiveName.HKCC,
        _ => throw new FormatException($"Unknown registry hive '{hive}'."),
    };

    public static RegistryViewName ParseView(string? view) => (view ?? "Registry64").ToUpperInvariant() switch
    {
        "REGISTRY32" => RegistryViewName.Registry32,
        "DEFAULT" => RegistryViewName.Default,
        _ => RegistryViewName.Registry64,
    };

    public static RegistryValueKind ParseKind(string? kind) => (kind ?? string.Empty).ToUpperInvariant() switch
    {
        "DWORD" or "REG_DWORD" => RegistryValueKind.DWord,
        "QWORD" or "REG_QWORD" => RegistryValueKind.QWord,
        "STRING" or "SZ" or "REG_SZ" => RegistryValueKind.String,
        "EXPANDSTRING" or "REG_EXPAND_SZ" => RegistryValueKind.ExpandString,
        "MULTISTRING" or "REG_MULTI_SZ" => RegistryValueKind.MultiString,
        "BINARY" or "REG_BINARY" => RegistryValueKind.Binary,
        _ => throw new FormatException($"Unknown registry value kind '{kind}'."),
    };

    public static RegistryValueData ToRegistryValue(JsonElement value, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => RegistryValueData.FromDWord(checked((uint)ToUInt64(value))),
        RegistryValueKind.QWord => RegistryValueData.FromQWord(ToUInt64(value)),
        RegistryValueKind.String => RegistryValueData.FromString(ToText(value)),
        RegistryValueKind.ExpandString => RegistryValueData.FromExpandString(ToText(value)),
        RegistryValueKind.MultiString => RegistryValueData.FromMultiString(
            value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Select(ToText) : new[] { ToText(value) }),
        RegistryValueKind.Binary => RegistryValueData.FromBinary(ToBytes(value)),
        _ => throw new FormatException($"Unsupported registry value kind {kind}."),
    };

    public static ulong ToUInt64(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.TryGetUInt64(out ulong u) ? u : unchecked((ulong)value.GetInt64());
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return ParseUInt64(value.GetString()!);
        }

        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean() ? 1UL : 0UL;
        }

        throw new FormatException($"Cannot convert {value.ValueKind} to an integer.");
    }

    public static ulong ParseUInt64(string text)
    {
        text = text.Trim();
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : ulong.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    public static string ToText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "1",
        JsonValueKind.False => "0",
        _ => value.GetRawText(),
    };

    public static bool IsDeleteMarker(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ||
        (value.ValueKind == JsonValueKind.String &&
         string.Equals(value.GetString(), "delete", StringComparison.OrdinalIgnoreCase));

    private static byte[] ToBytes(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray().Select(e => checked((byte)e.GetInt32())).ToArray();
        }

        string hex = new(ToText(value).Where(Uri.IsHexDigit).ToArray());
        return Convert.FromHexString(hex);
    }
}

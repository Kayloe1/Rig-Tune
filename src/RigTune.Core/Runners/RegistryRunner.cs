using Microsoft.Win32;
using RigTune.Core.Models;
using RigTune.Core.Platform;

namespace RigTune.Core.Runners;

/// <summary>Real Windows registry implementation of <see cref="IRegistryAccess"/>.</summary>
public sealed class RegistryRunner : IRegistryAccess
{
    public bool KeyExists(RegistryHiveName hive, RegistryViewName view, string keyPath)
    {
        using RegistryKey baseKey = OpenBase(hive, view);
        using RegistryKey? key = baseKey.OpenSubKey(Normalize(keyPath), writable: false);
        return key is not null;
    }

    public RegistryValueData? ReadValue(RegistryTarget target)
    {
        using RegistryKey baseKey = OpenBase(target.Hive, target.View);
        using RegistryKey? key = baseKey.OpenSubKey(Normalize(target.KeyPath), writable: false);
        if (key is null)
        {
            return null;
        }

        // DoNotExpandEnvironmentNames keeps REG_EXPAND_SZ exactly as stored.
        object? raw = key.GetValue(target.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (raw is null)
        {
            return null;
        }

        RegistryValueKind kind = key.GetValueKind(target.ValueName);
        return RegistryValueData.FromRaw(raw, kind);
    }

    public void WriteValue(RegistryTarget target, RegistryValueData data)
    {
        using RegistryKey baseKey = OpenBase(target.Hive, target.View);
        using RegistryKey key = baseKey.CreateSubKey(Normalize(target.KeyPath), writable: true)
            ?? throw new InvalidOperationException($"Unable to open or create {target.Hive}\\{target.KeyPath}.");
        (object value, RegistryValueKind kind) = data.ToRaw();
        key.SetValue(target.ValueName, value, kind);
    }

    public bool DeleteValue(RegistryTarget target)
    {
        using RegistryKey baseKey = OpenBase(target.Hive, target.View);
        using RegistryKey? key = baseKey.OpenSubKey(Normalize(target.KeyPath), writable: true);
        if (key is null || key.GetValue(target.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is null)
        {
            return false;
        }

        key.DeleteValue(target.ValueName, throwOnMissingValue: false);
        return true;
    }

    public IReadOnlyList<string> GetSubKeyNames(RegistryHiveName hive, RegistryViewName view, string keyPath)
    {
        using RegistryKey baseKey = OpenBase(hive, view);
        using RegistryKey? key = baseKey.OpenSubKey(Normalize(keyPath), writable: false);
        return key?.GetSubKeyNames() ?? Array.Empty<string>();
    }

    public IReadOnlyList<string> GetValueNames(RegistryHiveName hive, RegistryViewName view, string keyPath)
    {
        using RegistryKey baseKey = OpenBase(hive, view);
        using RegistryKey? key = baseKey.OpenSubKey(Normalize(keyPath), writable: false);
        return key?.GetValueNames() ?? Array.Empty<string>();
    }

    public bool DeleteKeyIfEmpty(RegistryHiveName hive, RegistryViewName view, string keyPath)
    {
        string path = Normalize(keyPath);
        using RegistryKey baseKey = OpenBase(hive, view);
        using (RegistryKey? key = baseKey.OpenSubKey(path, writable: false))
        {
            if (key is null || key.ValueCount > 0 || key.SubKeyCount > 0)
            {
                return false;
            }
        }

        baseKey.DeleteSubKey(path, throwOnMissingSubKey: false);
        return true;
    }

    internal static RegistryKey OpenBase(RegistryHiveName hive, RegistryViewName view) =>
        RegistryKey.OpenBaseKey(MapHive(hive), MapView(view));

    private static RegistryHive MapHive(RegistryHiveName hive) => hive switch
    {
        RegistryHiveName.HKLM => RegistryHive.LocalMachine,
        RegistryHiveName.HKCU => RegistryHive.CurrentUser,
        RegistryHiveName.HKCR => RegistryHive.ClassesRoot,
        RegistryHiveName.HKU => RegistryHive.Users,
        RegistryHiveName.HKCC => RegistryHive.CurrentConfig,
        _ => throw new ArgumentOutOfRangeException(nameof(hive), hive, null),
    };

    private static RegistryView MapView(RegistryViewName view) => view switch
    {
        RegistryViewName.Registry64 => RegistryView.Registry64,
        RegistryViewName.Registry32 => RegistryView.Registry32,
        _ => RegistryView.Default,
    };

    private static string Normalize(string keyPath) => keyPath.Trim().Trim('\\');
}

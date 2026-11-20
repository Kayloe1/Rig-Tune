using RigTune.Core.Models;
using RigTune.Core.Platform;

namespace RigTune.Core.Tests.Fakes;

/// <summary>Case-insensitive in-memory registry used to test snapshot logic on any OS.</summary>
public sealed class InMemoryRegistry : IRegistryAccess
{
    private readonly Dictionary<string, Dictionary<string, RegistryValueData>> _keys =
        new(StringComparer.OrdinalIgnoreCase);

    public Func<RegistryTarget, bool>? FailWritesWhen { get; set; }

    public int KeyCount => _keys.Count;

    public bool KeyExists(RegistryHiveName hive, RegistryViewName view, string keyPath) =>
        _keys.ContainsKey(Key(hive, view, keyPath));

    public RegistryValueData? ReadValue(RegistryTarget target) =>
        _keys.TryGetValue(Key(target.Hive, target.View, target.KeyPath), out var values) &&
        values.TryGetValue(target.ValueName, out RegistryValueData? data)
            ? Clone(data)
            : null;

    public void WriteValue(RegistryTarget target, RegistryValueData data)
    {
        if (FailWritesWhen?.Invoke(target) == true)
        {
            throw new UnauthorizedAccessException("Simulated access denied.");
        }

        string[] parts = target.KeyPath.Trim('\\').Split('\\');
        for (int i = 1; i <= parts.Length; i++)
        {
            string k = Key(target.Hive, target.View, string.Join('\\', parts, 0, i));
            if (!_keys.ContainsKey(k))
            {
                _keys[k] = new Dictionary<string, RegistryValueData>(StringComparer.OrdinalIgnoreCase);
            }
        }

        _keys[Key(target.Hive, target.View, target.KeyPath)][target.ValueName] = Clone(data);
    }

    public bool DeleteValue(RegistryTarget target) =>
        _keys.TryGetValue(Key(target.Hive, target.View, target.KeyPath), out var values) && values.Remove(target.ValueName);

    public IReadOnlyList<string> GetSubKeyNames(RegistryHiveName hive, RegistryViewName view, string keyPath)
    {
        string prefix = Key(hive, view, keyPath) + "\\";
        return _keys.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(k => k[prefix.Length..].Split('\\')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> GetValueNames(RegistryHiveName hive, RegistryViewName view, string keyPath) =>
        _keys.TryGetValue(Key(hive, view, keyPath), out var values) ? values.Keys.ToList() : Array.Empty<string>();

    public bool DeleteKeyIfEmpty(RegistryHiveName hive, RegistryViewName view, string keyPath)
    {
        string k = Key(hive, view, keyPath);
        if (!_keys.TryGetValue(k, out var values) || values.Count > 0)
        {
            return false;
        }

        string prefix = k + "\\";
        if (_keys.Keys.Any(other => other.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return _keys.Remove(k);
    }

    /// <summary>Removes a key with all its subkeys (test helper for uninstall/leftover tests).</summary>
    public void DeleteKeyTree(RegistryHiveName hive, RegistryViewName view, string keyPath)
    {
        string k = Key(hive, view, keyPath);
        foreach (string key in _keys.Keys.Where(x => x.Equals(k, StringComparison.OrdinalIgnoreCase) ||
                                                     x.StartsWith(k + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _keys.Remove(key);
        }
    }

    private static string Key(RegistryHiveName hive, RegistryViewName view, string path) =>
        $"{hive}|{view}|{path.Trim('\\')}";

    private static RegistryValueData Clone(RegistryValueData d) => new()
    {
        Kind = d.Kind,
        DWord = d.DWord,
        QWord = d.QWord,
        String = d.String,
        MultiString = d.MultiString?.ToArray(),
        BinaryBase64 = d.BinaryBase64,
    };
}

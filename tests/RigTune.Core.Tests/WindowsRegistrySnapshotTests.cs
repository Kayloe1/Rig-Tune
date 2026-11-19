using Microsoft.Win32;
using RigTune.Core.Models;
using RigTune.Core.Runners;

namespace RigTune.Core.Tests;

/// <summary>
/// End-to-end snapshot round trips against the REAL Windows registry. Every value lives under an
/// isolated, per-test <c>HKCU\Software\RigTuneTest\{guid}</c> key that is deleted afterwards, so host
/// settings are never touched. Skipped automatically on non-Windows hosts.
/// </summary>
public sealed class WindowsRegistrySnapshotTests : IDisposable
{
    private const string IsolationRoot = @"Software\RigTuneTest";
    private readonly string _testKey = $@"{IsolationRoot}\{Guid.NewGuid():N}";
    private readonly SnapshotHarness _h;

    public WindowsRegistrySnapshotTests()
    {
        _h = new SnapshotHarness(OperatingSystem.IsWindows() ? new RegistryRunner() : null);
    }

    private RegistryTarget Target(string valueName, string? sub = null)
    {
        var target = new RegistryTarget(RegistryHiveName.HKCU, RegistryViewName.Registry64,
            sub is null ? _testKey : $@"{_testKey}\{sub}", valueName);
        // Hard guard: these tests may only ever touch the isolated test key.
        Assert.StartsWith(IsolationRoot + "\\", target.KeyPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(RegistryHiveName.HKCU, target.Hive);
        return target;
    }

    [WindowsFact]
    public async Task Preexisting_values_of_every_kind_round_trip_exactly()
    {
        var originals = new Dictionary<string, RegistryValueData>
        {
            ["DWord"] = RegistryValueData.FromDWord(0x80000001),
            ["QWord"] = RegistryValueData.FromQWord(ulong.MaxValue - 1),
            ["Sz"] = RegistryValueData.FromString("400"),
            ["ExpandSz"] = RegistryValueData.FromExpandString(@"%USERPROFILE%\RigTune"),
            ["MultiSz"] = RegistryValueData.FromMultiString(new[] { "alpha", "beta" }),
            ["Binary"] = RegistryValueData.FromBinary(new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
        };

        foreach ((string name, RegistryValueData value) in originals)
        {
            _h.Registry.WriteValue(Target(name), value);
        }

        var snapshotIds = new List<Guid>();
        foreach (string name in originals.Keys)
        {
            snapshotIds.Add((await _h.Snapshots.ApplyRegistryAsync("test." + name, Target(name),
                RegistryValueData.FromDWord(1))).Id);
            Assert.Equal(RegistryValueData.FromDWord(1), _h.Registry.ReadValue(Target(name)));
        }

        RevertAllResult result = await _h.Snapshots.RevertAllAsync();

        Assert.Equal(originals.Count, result.Succeeded);
        foreach ((string name, RegistryValueData value) in originals)
        {
            Assert.Equal(value, _h.Registry.ReadValue(Target(name)));
        }

        // ExpandString must stay unexpanded and keep its kind.
        using RegistryKey key = Registry.CurrentUser.OpenSubKey(_testKey)!;
        Assert.Equal(RegistryValueKind.ExpandString, key.GetValueKind("ExpandSz"));
        Assert.Equal(@"%USERPROFILE%\RigTune",
            key.GetValue("ExpandSz", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
    }

    [WindowsFact]
    public async Task Previously_missing_value_and_created_keys_are_deleted_on_revert()
    {
        _h.Registry.WriteValue(Target("Anchor"), RegistryValueData.FromDWord(1));
        RegistryTarget fresh = Target("RawMouseThrottleDuration", @"Created\Nested");

        RegistrySnapshotEntry snap = await _h.Snapshots.ApplyRegistryAsync("perf.input.raw_mouse_throttle", fresh,
            RegistryValueData.FromDWord(1));
        Assert.False(snap.Existed);
        Assert.Equal(RegistryValueData.FromDWord(1), _h.Registry.ReadValue(fresh));

        await _h.Snapshots.RevertAsync(snap.Id);

        Assert.Null(_h.Registry.ReadValue(fresh));
        Assert.False(_h.Registry.KeyExists(RegistryHiveName.HKCU, RegistryViewName.Registry64, $@"{_testKey}\Created"));
        Assert.Equal(RegistryValueData.FromDWord(1), _h.Registry.ReadValue(Target("Anchor")));
    }

    [WindowsFact]
    public async Task Revert_survives_app_restart_on_real_registry()
    {
        RegistryTarget t = Target("MenuShowDelay");
        _h.Registry.WriteValue(t, RegistryValueData.FromString("400"));
        RegistrySnapshotEntry snap = await _h.Snapshots.ApplyRegistryAsync("registry.menu_show_delay", t,
            RegistryValueData.FromString("0"));

        RevertResult result = await _h.CreateSnapshotService().RevertAsync(snap.Id);

        Assert.True(result.Success);
        Assert.Equal(RegistryValueData.FromString("400"), _h.Registry.ReadValue(t));
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            Registry.CurrentUser.DeleteSubKeyTree(_testKey, throwOnMissingSubKey: false);
            using RegistryKey? root = Registry.CurrentUser.OpenSubKey(IsolationRoot);
            if (root is { SubKeyCount: 0, ValueCount: 0 })
            {
                root.Dispose();
                Registry.CurrentUser.DeleteSubKey(IsolationRoot, throwOnMissingSubKey: false);
            }
        }

        _h.Dispose();
    }
}

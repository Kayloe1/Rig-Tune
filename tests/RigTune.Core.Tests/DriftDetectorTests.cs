using Microsoft.Win32;
using RigTune.Core.Models;
using RigTune.Core.Platform;
using RigTune.Core.Runners;
using RigTune.Core.Runners.Handlers;
using RigTune.Core.Services;
using RigTune.Core.Storage;
using RigTune.Core.Tests.Fakes;

namespace RigTune.Core.Tests;

/// <summary>A minimal string-state runner (e.g. a scheduled task) whose live value the test can flip.</summary>
public sealed class FakeStateRunner : IStateRunner
{
    public Dictionary<string, string> States { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string Kind => "fake_state";
    public string? ReadState(string target) => States.GetValueOrDefault(target);
    public void WriteState(string target, string state) => States[target] = state;
}

/// <summary>Drift detection and re-apply across every snapshot type (fakes; runs on any OS).</summary>
public sealed class DriftDetectorTests : IDisposable
{
    private readonly FakeStateRunner _state = new();
    private readonly SnapshotHarness _h;
    private readonly DriftDetectorService _drift;

    public DriftDetectorTests()
    {
        _h = new SnapshotHarness(stateRunners: new IStateRunner[] { _state });
        _drift = new DriftDetectorService(_h.Snapshots, _h.Registry, _h.Services, _h.Power, _h.NvApi, _h.Audit,
            id => id == "perf.registry.menu_show_delay" ? "Set Menu Animation Delay to Zero" : null, _h.Clock);
    }

    private InMemoryRegistry Reg => (InMemoryRegistry)_h.Registry;

    private static readonly RegistryTarget MenuDelay = new(RegistryHiveName.HKCU, RegistryViewName.Registry64, @"Software\RigTuneTest\Desktop", "MenuShowDelay");

    [Fact]
    public async Task Untouched_changes_are_in_place()
    {
        await _h.Snapshots.ApplyRegistryAsync("perf.registry.menu_show_delay", MenuDelay, RegistryValueData.FromString("0"));

        DriftScanResult scan = await _drift.ScanAsync();

        DriftItem item = Assert.Single(scan.Items);
        Assert.Equal(DriftStatus.InPlace, item.Status);
        Assert.Equal("Set Menu Animation Delay to Zero", item.TweakName);
        Assert.Empty(scan.Drifted);
    }

    [Fact]
    public async Task Registry_value_changed_back_is_detected_with_expected_and_current()
    {
        Reg.WriteValue(MenuDelay, RegistryValueData.FromString("400"));
        await _h.Snapshots.ApplyRegistryAsync("perf.registry.menu_show_delay", MenuDelay, RegistryValueData.FromString("0"));
        Reg.WriteValue(MenuDelay, RegistryValueData.FromString("400")); // a Windows update resets it

        DriftItem item = Assert.Single((await _drift.ScanAsync()).Drifted);

        Assert.Contains("\"0\"", item.Expected);
        Assert.Contains("\"400\"", item.Current);
        Assert.Contains(await _h.Audit.GetAllAsync(), e => e.Action == AuditAction.DriftDetected);
    }

    [Fact]
    public async Task Reapply_restores_the_applied_value_logs_it_and_keeps_the_true_original()
    {
        Reg.WriteValue(MenuDelay, RegistryValueData.FromString("400"));
        RegistrySnapshotEntry snap = await _h.Snapshots.ApplyRegistryAsync("perf.registry.menu_show_delay", MenuDelay, RegistryValueData.FromString("0"));
        Reg.WriteValue(MenuDelay, RegistryValueData.FromString("250"));

        ReapplyResult r = await _drift.ReapplyAsync(snap.Id);

        Assert.True(r.Success, r.Message);
        Assert.Equal(RegistryValueData.FromString("0"), Reg.ReadValue(MenuDelay));
        Assert.Empty((await _drift.ScanAsync()).Drifted);
        Assert.Contains(await _h.Audit.GetAllAsync(), e => e.Action == AuditAction.Reapplied && e.Success);

        await _h.Snapshots.RevertAsync(snap.Id);
        Assert.Equal(RegistryValueData.FromString("400"), Reg.ReadValue(MenuDelay)); // pre-RigTune value, not 250
    }

    [Fact]
    public async Task Deleted_value_tweak_drifts_when_the_value_reappears()
    {
        Reg.WriteValue(MenuDelay, RegistryValueData.FromDWord(1));
        await _h.Snapshots.ApplyRegistryAsync("t.delete", MenuDelay, value: null);
        Reg.WriteValue(MenuDelay, RegistryValueData.FromDWord(1));

        DriftItem item = Assert.Single((await _drift.ScanAsync()).Drifted);
        Assert.Equal("(deleted)", item.Expected);

        await _drift.ReapplyAsync(item.SnapshotId);
        Assert.Null(Reg.ReadValue(MenuDelay));
    }

    [Fact]
    public async Task Service_power_nvapi_and_state_drift_are_detected_and_reapplied()
    {
        _h.Services.Services["DiagTrack"] = new ServiceState("DiagTrack", ServiceStartType.Automatic, true);
        Guid sub = PowerRunner.Guids.SubPciExpress, aspm = PowerRunner.Guids.Aspm;
        _h.Power.Values[(_h.Power.ActiveScheme, sub, aspm)] = (2, 2);
        _h.NvApi.BaseProfile[0x1057EB71] = 5;
        _state.States[@"\Microsoft\Windows\Maps\MapsUpdateTask"] = "Enabled";

        await _h.Snapshots.ApplyServiceAsync("svc", "DiagTrack", ServiceStartType.Disabled, stopService: true);
        await _h.Snapshots.ApplyPowerAsync("pwr", sub, aspm, 0, 0);
        await _h.Snapshots.ApplyNvApiDrsAsync("nv", 0x1057EB71, 1);
        await _h.Snapshots.ApplyStateAsync("task", "fake_state", @"\Microsoft\Windows\Maps\MapsUpdateTask", "Disabled");

        // Windows Update + a driver update flip everything back.
        _h.Services.Services["DiagTrack"] = new ServiceState("DiagTrack", ServiceStartType.Automatic, true);
        _h.Power.Values[(_h.Power.ActiveScheme, sub, aspm)] = (2, 2);
        _h.NvApi.BaseProfile.Remove(0x1057EB71);
        _state.States[@"\Microsoft\Windows\Maps\MapsUpdateTask"] = "Enabled";

        DriftScanResult scan = await _drift.ScanAsync();
        Assert.Equal(4, scan.Drifted.Count);

        IReadOnlyList<ReapplyResult> results = await _drift.ReapplyAllDriftedAsync();

        Assert.All(results, r => Assert.True(r.Success, r.Message));
        Assert.Equal((ServiceStartType.Disabled, false), (_h.Services.Query("DiagTrack")!.StartType, _h.Services.Query("DiagTrack")!.IsRunning));
        Assert.Equal((0u, 0u), _h.Power.Values[(_h.Power.ActiveScheme, sub, aspm)]);
        Assert.Equal(1u, _h.NvApi.BaseProfile[0x1057EB71]);
        Assert.Equal("Disabled", _state.States[@"\Microsoft\Windows\Maps\MapsUpdateTask"]);
        Assert.Empty(_drift.LastResult!.Drifted);
    }

    [Fact]
    public async Task Unreadable_targets_are_unavailable_not_drifted()
    {
        _h.NvApi.BaseProfile[0x00A06946] = 1;
        await _h.Snapshots.ApplyNvApiDrsAsync("nv", 0x00A06946, 102400);
        _h.NvApi.IsAvailable = false;

        DriftItem item = Assert.Single((await _drift.ScanAsync()).Items);

        Assert.Equal(DriftStatus.Unavailable, item.Status);
        Assert.False(item.IsDrifted);
    }

    [Fact]
    public async Task Reapplying_an_inactive_snapshot_is_refused()
    {
        RegistrySnapshotEntry snap = await _h.Snapshots.ApplyRegistryAsync("t", MenuDelay, RegistryValueData.FromDWord(1));
        await _h.Snapshots.RevertAsync(snap.Id);

        ReapplyResult r = await _drift.ReapplyAsync(snap.Id);

        Assert.False(r.Success);
        Assert.Empty(await _h.Snapshots.GetActiveAsync());
    }

    [Fact]
    public async Task Profile_export_captures_active_tweaks_with_options_and_import_reapplies_them()
    {
        var settings = new SettingsService(_h.Paths);
        var engine = new TweakEngine(new TweakCatalogService(RigTunePaths.BundledTweaksFile), _h.Snapshots, settings, _h.Audit,
            new ITweakActionHandler[] { new RegistryActionHandler(_h.Registry), new NvApiActionHandler(_h.NvApi) });
        var profiles = new ProfileService(engine, _h.Snapshots, _h.Audit, _h.Clock);
        await engine.ApplyAsync(engine.Find("perf.registry.menu_show_delay")!);
        await engine.ApplyAsync(engine.Find("perf.nvidia.shader_cache")!, new TweakParameters { OptionId = "10gb" });
        string file = Path.Combine(_h.Paths.Root, "profile.json");

        TweakProfile exported = await profiles.ExportAsync(file);
        await engine.RevertAllAsync();
        ProfileImportResult imported = await profiles.ImportAsync(ProfileService.Read(file));

        Assert.Equal(new[] { "perf.registry.menu_show_delay", "perf.nvidia.shader_cache" }, exported.Tweaks.Select(t => t.Id));
        Assert.Equal("10gb", exported.Tweaks.Single(t => t.Id == "perf.nvidia.shader_cache").OptionId);
        Assert.Equal(2, imported.Applied);
        Assert.Equal(10240u, _h.NvApi.BaseProfile[0x00A06946]);
        Assert.Equal(RegistryValueData.FromString("0"), Reg.ReadValue(new RegistryTarget(RegistryHiveName.HKCU,
            RegistryViewName.Registry64, @"Control Panel\Desktop", "MenuShowDelay")));
    }

    [Fact]
    public void Foreign_json_is_rejected_as_a_profile()
    {
        string file = Path.Combine(_h.Paths.Root, "x.json");
        Directory.CreateDirectory(_h.Paths.Root);
        File.WriteAllText(file, """{ "schemaVersion": 1, "application": "Other", "tweaks": [] }""");

        Assert.Throws<InvalidDataException>(() => ProfileService.Read(file));
    }

    public void Dispose() => _h.Dispose();
}

/// <summary>
/// Drift detection against the REAL Windows registry, confined to an isolated, per-test
/// <c>HKCU\Software\RigTuneTest\{guid}</c> key that is deleted afterwards. Skipped on non-Windows hosts.
/// </summary>
public sealed class WindowsRegistryDriftTests : IDisposable
{
    private const string IsolationRoot = @"Software\RigTuneTest";
    private readonly string _testKey = $@"{IsolationRoot}\{Guid.NewGuid():N}";
    private readonly SnapshotHarness _h;
    private readonly DriftDetectorService _drift;

    public WindowsRegistryDriftTests()
    {
        _h = new SnapshotHarness(OperatingSystem.IsWindows() ? new RegistryRunner() : null);
        _drift = new DriftDetectorService(_h.Snapshots, _h.Registry, _h.Services, _h.Power, _h.NvApi, _h.Audit);
    }

    private RegistryTarget Target(string name)
    {
        var target = new RegistryTarget(RegistryHiveName.HKCU, RegistryViewName.Registry64, _testKey, name);
        Assert.StartsWith(IsolationRoot + "\\", target.KeyPath, StringComparison.OrdinalIgnoreCase);
        return target;
    }

    [WindowsFact]
    public async Task Detects_a_manual_regedit_change_and_reapplies_it()
    {
        RegistryTarget t = Target("MenuShowDelay");
        _h.Registry.WriteValue(t, RegistryValueData.FromString("400"));
        RegistrySnapshotEntry snap = await _h.Snapshots.ApplyRegistryAsync("perf.registry.menu_show_delay", t, RegistryValueData.FromString("0"));

        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(_testKey, writable: true)!)
        {
            key.SetValue("MenuShowDelay", "400", RegistryValueKind.String); // the SPEC §8 manual drift test
        }

        DriftItem drifted = Assert.Single((await _drift.ScanAsync()).Drifted);
        ReapplyResult r = await _drift.ReapplyAsync(drifted.SnapshotId);

        Assert.True(r.Success, r.Message);
        Assert.Equal("0", Registry.CurrentUser.OpenSubKey(_testKey)!.GetValue("MenuShowDelay"));
        await _h.Snapshots.RevertAsync(snap.Id);
        Assert.Equal("400", Registry.CurrentUser.OpenSubKey(_testKey)!.GetValue("MenuShowDelay"));
    }

    [WindowsFact]
    public async Task Detects_a_dword_kind_change_as_drift()
    {
        RegistryTarget t = Target("Flags");
        RegistrySnapshotEntry snap = await _h.Snapshots.ApplyRegistryAsync("t", t, RegistryValueData.FromDWord(506));
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(_testKey, writable: true)!)
        {
            key.SetValue("Flags", "506", RegistryValueKind.String); // same digits, different kind
        }

        Assert.Single((await _drift.ScanAsync()).Drifted);
        await _drift.ReapplyAsync(snap.Id);
        Assert.Equal(RegistryValueKind.DWord, Registry.CurrentUser.OpenSubKey(_testKey)!.GetValueKind("Flags"));
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

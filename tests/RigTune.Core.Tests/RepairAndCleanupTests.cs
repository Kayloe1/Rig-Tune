using RigTune.Core.Models;
using RigTune.Core.Services.SystemCare;
using RigTune.Core.Tests.Fakes;

namespace RigTune.Core.Tests;

public sealed class RepairRulesTests
{
    private static DeviceInfo Device(string cls, bool disabled = false, bool problem = false, string enumerator = "PCI") =>
        new($"{enumerator}\\VEN_1234&DEV_5678\\0", $"{cls} device", cls, Guid.Empty, enumerator, null, null,
            Array.Empty<string>(), disabled, problem || disabled);

    [Theory]
    [InlineData("Bluetooth", "Bluetooth")]
    [InlineData("MEDIA", "Audio")]
    [InlineData("AudioEndpoint", "Audio")]
    [InlineData("Net", "Network")]
    [InlineData("USB", "USB")]
    [InlineData("Display", null)]
    public void Device_categories(string cls, string? category) => Assert.Equal(category, RepairRules.CategoryOf(Device(cls)));

    [Fact]
    public void Virtual_adapters_are_not_checked() => Assert.Null(RepairRules.CategoryOf(Device("Net", enumerator: "ROOT")));

    [Fact]
    public void Disabled_device_is_broken_and_offers_enable()
    {
        RepairCheck c = RepairRules.EvaluateDevice(Device("Bluetooth", disabled: true), "Bluetooth");

        Assert.Equal(RepairStatus.Broken, c.Status);
        Assert.Equal(RepairFixKind.EnableDevice, c.FixKind);
        Assert.True(c.CanFix);
    }

    [Fact]
    public void Device_with_problem_offers_restart_and_healthy_device_offers_nothing()
    {
        Assert.Equal(RepairFixKind.RestartDevice, RepairRules.EvaluateDevice(Device("USB", problem: true), "USB").FixKind);
        RepairCheck healthy = RepairRules.EvaluateDevice(Device("USB"), "USB");
        Assert.Equal(RepairStatus.Healthy, healthy.Status);
        Assert.False(healthy.CanFix);
    }

    private static ServiceExpectation Expect(string name) =>
        RepairRules.WatchedServices.Append(RepairRules.SearchIndexing).Single(e => e.ServiceName == name);

    [Fact]
    public void Disabled_windows_update_is_restored_to_its_default_start_type()
    {
        RepairCheck c = RepairRules.EvaluateService(Expect("wuauserv"), new ServiceState("wuauserv", ServiceStartType.Disabled, false));

        Assert.Equal(RepairStatus.Broken, c.Status);
        Assert.Equal(RepairFixKind.SetServiceStartType, c.FixKind);
        Assert.Equal("Manual", c.FixValue);
    }

    [Fact]
    public void Stopped_manual_windows_update_and_bits_are_healthy()
    {
        Assert.Equal(RepairStatus.Healthy, RepairRules.EvaluateService(Expect("wuauserv"), new ServiceState("wuauserv", ServiceStartType.Manual, false)).Status);
        Assert.Equal(RepairStatus.Healthy, RepairRules.EvaluateService(Expect("BITS"), new ServiceState("BITS", ServiceStartType.Manual, false)).Status);
    }

    [Fact]
    public void Stopped_automatic_print_spooler_is_stalled()
    {
        RepairCheck c = RepairRules.EvaluateService(Expect("Spooler"), new ServiceState("Spooler", ServiceStartType.Automatic, false));

        Assert.Equal(RepairStatus.Broken, c.Status);
        Assert.Equal(RepairFixKind.StartService, c.FixKind);
    }

    [Fact]
    public void Pending_service_is_stuck_and_missing_service_is_not_applicable()
    {
        Assert.Equal(RepairFixKind.RestartService,
            RepairRules.EvaluateService(Expect("BITS"), new ServiceState("BITS", ServiceStartType.Manual, false, IsPending: true)).FixKind);
        Assert.Equal(RepairStatus.NotApplicable, RepairRules.EvaluateService(Expect("Spooler"), null).Status);
    }

    [Fact]
    public void Disabled_search_indexing_is_a_flipped_policy()
    {
        RepairCheck c = RepairRules.EvaluateService(RepairRules.SearchIndexing, new ServiceState("WSearch", ServiceStartType.Disabled, false), "Policies");

        Assert.Equal(RepairStatus.Broken, c.Status);
        Assert.Equal("AutomaticDelayed", c.FixValue);
    }

    [Theory]
    [InlineData(0u, RepairStatus.Broken)]
    [InlineData(1u, RepairStatus.Healthy)]
    [InlineData(null, RepairStatus.Healthy)]
    public void Storage_sense_policy(uint? value, RepairStatus expected)
    {
        RegistryValueData? data = value is { } v ? RegistryValueData.FromDWord(v) : null;

        RepairCheck c = RepairRules.EvaluateStorageSensePolicy(data, RepairScanner.StorageSensePolicy);

        Assert.Equal(expected, c.Status);
        Assert.Equal(expected == RepairStatus.Broken ? RepairFixKind.DeleteRegistryValue : RepairFixKind.None, c.FixKind);
    }

    [Theory]
    [InlineData("Off", "Enabled", "Enabled", RepairStatus.Broken, RepairFixKind.SetBootHypervisor)]
    [InlineData("Auto", "Disabled", "Enabled", RepairStatus.Broken, RepairFixKind.EnableFeature)]
    [InlineData("Auto", "Enabled", "Enabled", RepairStatus.Healthy, RepairFixKind.None)]
    [InlineData("<unset>", "Enabled", "Disabled", RepairStatus.Healthy, RepairFixKind.None)]
    [InlineData("Off", "Disabled", "Disabled", RepairStatus.NotApplicable, RepairFixKind.None)]
    public void Hyper_v_wsl2_consistency(string launch, string vmp, string wsl, RepairStatus status, RepairFixKind fix)
    {
        RepairCheck c = RepairRules.EvaluateHypervisor(launch, vmp, wsl);

        Assert.Equal(status, c.Status);
        Assert.Equal(fix, c.FixKind);
    }

    [Fact]
    public void Breakage_caused_by_a_rigtune_tweak_is_fixed_by_reverting_its_snapshot()
    {
        RepairCheck broken = RepairRules.EvaluateService(RepairRules.SearchIndexing, new ServiceState("WSearch", ServiceStartType.Disabled, false));
        var snapshot = new ServiceSnapshotEntry { TweakId = "services.wsearch", ServiceName = "WSearch" };

        RepairCheck c = RepairRules.PreferSnapshotRevert(broken, snapshot);

        Assert.Equal(RepairFixKind.RevertSnapshot, c.FixKind);
        Assert.Equal(snapshot.Id, c.SnapshotId);
        Assert.Contains("services.wsearch", c.Detail);

        RepairCheck healthy = RepairRules.EvaluateService(RepairRules.SearchIndexing, new ServiceState("WSearch", ServiceStartType.AutomaticDelayed, true));
        Assert.Same(healthy, RepairRules.PreferSnapshotRevert(healthy, snapshot));
    }
}

public sealed class CleanupEngineTests : IDisposable
{
    private readonly SnapshotHarness _h = new();
    private readonly TempDir _dir = new();
    private readonly FakeMaintenanceNative _native = new();

    private CleanupEngine CreateEngine(params CleanupDefinition[] defs) =>
        new(_h.Audit, _native, defs.Length > 0 ? defs : CleanupEngine.DefaultDefinitions(), _h.Clock);

    private string MakeFile(string relative, int size, TimeSpan age)
    {
        string path = Path.Combine(_dir.Path, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        File.SetLastWriteTimeUtc(path, _h.Clock.GetUtcNow().UtcDateTime - age);
        return path;
    }

    [Fact]
    public void Default_definitions_cover_the_seven_spec_actions()
    {
        Assert.Equal(new[] { "temp_system_files", "temp_files", "prefetch", "dns_cache", "gpu_shader_cache", "thumbnails", "ram_standby" },
            CleanupEngine.DefaultDefinitions().Select(d => d.Id));
    }

    [Fact]
    public async Task Estimate_and_clean_respect_minimum_age_and_pattern()
    {
        string old = MakeFile(@"a/old.tmp", 1000, TimeSpan.FromDays(3));
        string nested = MakeFile(@"a/b/older.tmp", 500, TimeSpan.FromDays(5));
        string fresh = MakeFile(@"a/fresh.tmp", 300, TimeSpan.FromMinutes(5));
        CleanupEngine engine = CreateEngine(new CleanupDefinition("t", "Temp", "d", CleanupKind.Files,
            new[] { new CleanupLocation(Path.Combine(_dir.Path, "a"), MinAge: TimeSpan.FromDays(1)) }, true));

        CleanupEstimate estimate = await engine.EstimateAsync("t");
        CleanupResult result = await engine.CleanAsync("t");

        Assert.Equal((1500L, 2), (estimate.Bytes, estimate.Files));
        Assert.Equal((1500L, 2), (result.BytesFreed, result.FilesDeleted));
        Assert.False(File.Exists(old));
        Assert.False(File.Exists(nested));
        Assert.True(File.Exists(fresh));
        Assert.False(Directory.Exists(Path.Combine(_dir.Path, "a", "b"))); // emptied subfolder removed
        Assert.True(Directory.Exists(Path.Combine(_dir.Path, "a")));       // root kept
        Assert.Contains(await _h.Audit.GetAllAsync(), e => e.Action == AuditAction.Cleaned);
    }

    [Fact]
    public async Task Non_recursive_pattern_cleanups_only_touch_matching_files()
    {
        string pf = MakeFile(@"Prefetch/APP.EXE-1234.pf", 100, TimeSpan.Zero);
        string layout = MakeFile(@"Prefetch/Layout.ini", 100, TimeSpan.Zero);
        string ready = MakeFile(@"Prefetch/ReadyBoot/trace.pf", 100, TimeSpan.Zero);
        CleanupEngine engine = CreateEngine(new CleanupDefinition("prefetch", "Prefetch", "d", CleanupKind.Files,
            new[] { new CleanupLocation(Path.Combine(_dir.Path, "Prefetch"), "*.pf", Recursive: false) }, false));

        await engine.CleanAsync("prefetch");

        Assert.False(File.Exists(pf));
        Assert.True(File.Exists(layout));
        Assert.True(File.Exists(ready));
    }

    [Fact]
    public async Task Cleanup_never_follows_directory_symlinks_out_of_its_root()
    {
        string outside = MakeFile(@"precious/keep.dat", 100, TimeSpan.FromDays(10));
        Directory.CreateDirectory(Path.Combine(_dir.Path, "cache"));
        Directory.CreateSymbolicLink(Path.Combine(_dir.Path, "cache", "link"), Path.Combine(_dir.Path, "precious"));
        CleanupEngine engine = CreateEngine(new CleanupDefinition("c", "Cache", "d", CleanupKind.Files,
            new[] { new CleanupLocation(Path.Combine(_dir.Path, "cache")) }, true));

        await engine.CleanAsync("c");

        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void Protected_roots_are_refused()
    {
        Assert.False(CleanupEngine.IsSafeRoot(Path.GetPathRoot(_dir.Path)!));
        Assert.False(CleanupEngine.IsSafeRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        Assert.False(CleanupEngine.IsSafeRoot(""));
        Assert.True(CleanupEngine.IsSafeRoot(_dir.Path));
    }

    [Fact]
    public async Task Dns_and_memory_cleanups_use_native_primitives()
    {
        CleanupEngine engine = CreateEngine();

        CleanupEstimate memEstimate = await engine.EstimateAsync("ram_standby");
        CleanupResult dns = await engine.CleanAsync("dns_cache");
        CleanupResult mem = await engine.CleanAsync("ram_standby");

        Assert.Equal(8L * 1024 * 1024 * 1024, memEstimate.Bytes);
        Assert.True(dns.Success);
        Assert.Equal(1, _native.Flushes);
        Assert.True(mem.Success);
        Assert.Equal(1, _native.Purges);
        Assert.Equal(8L * 1024 * 1024 * 1024 - 512L * 1024 * 1024, mem.BytesFreed);
    }

    public void Dispose()
    {
        _h.Dispose();
        _dir.Dispose();
    }
}

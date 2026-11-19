using Microsoft.Win32;
using RigTune.Core.Models;
using RigTune.Core.Runners;
using RigTune.Core.Runners.Handlers;
using RigTune.Core.Services;
using RigTune.Core.Storage;
using RigTune.Core.Tests.Fakes;

namespace RigTune.Core.Tests;

public sealed class TweakEngineTests : IDisposable
{
    private readonly SnapshotHarness _h = new();
    private readonly SettingsService _settings;
    private readonly TweakEngine _engine;

    public TweakEngineTests()
    {
        _settings = new SettingsService(_h.Paths);
        _engine = new TweakEngine(new TweakCatalogService(RigTunePaths.BundledTweaksFile), _h.Snapshots, _settings, _h.Audit,
            new ITweakActionHandler[]
            {
                new RegistryActionHandler(_h.Registry),
                new ServiceActionHandler(_h.Services),
                new PowerActionHandler(_h.Power, _h.Registry),
                new NvApiActionHandler(_h.NvApi),
            });
    }

    private InMemoryRegistry Reg => (InMemoryRegistry)_h.Registry;

    private TweakDefinition Tweak(string id) => _engine.Find(id) ?? throw new KeyNotFoundException(id);

    /// <summary>Every production handler, constructed without touching the OS (used by catalog tests).</summary>
    public static TweakEngine CreateFullEngineForCatalog()
    {
        var paths = new RigTunePaths(Path.Combine(Path.GetTempPath(), "RigTuneTests", Guid.NewGuid().ToString("N")));
        var registry = new InMemoryRegistry();
        var audit = new AuditLogService(paths);
        var snapshots = new SnapshotService(paths, registry, new FakeServiceAccess(), new FakePowerAccess(), new FakeNvApi(), audit);
        var devices = new DeviceManagerRunner();
        var dism = new DismRunner();
        return new TweakEngine(new TweakCatalogService(RigTunePaths.BundledTweaksFile), snapshots, new SettingsService(paths), audit,
            new ITweakActionHandler[]
            {
                new RegistryActionHandler(registry),
                new ServiceActionHandler(new FakeServiceAccess()),
                new PowerActionHandler(new FakePowerAccess(), registry),
                new NvApiActionHandler(new FakeNvApi()),
                new DismActionHandler(dism, new DismFeatureStateRunner(dism), new BcdStateRunner(), new WinReStateRunner()),
                new ScheduledTaskActionHandler(new TaskSchedulerRunner()),
                new DeviceManagerActionHandler(devices),
                new PciActionHandler(new PciRunner(devices, registry), () => CpuTopology.Empty),
                new NetworkActionHandler(new NetworkAdapterRunner(devices, registry), new TcpGlobalStateRunner(), registry),
                new NtfsActionHandler(registry),
                new InspectionActionHandler(new WasapiInspectService(), new TimerResolutionService()),
                new CleanupActionHandler(new RigTune.Core.Services.SystemCare.CleanupEngine(audit, new FakeMaintenanceNative())),
            });
    }

    [Fact]
    public async Task Applying_and_reverting_a_registry_tweak_restores_the_exact_original()
    {
        var target = new RegistryTarget(RegistryHiveName.HKCU, RegistryViewName.Registry64, @"Control Panel\Mouse", "RawMouseThrottleDuration");
        Reg.WriteValue(target, RegistryValueData.FromDWord(8));
        TweakDefinition tweak = Tweak("perf.input.raw_mouse_throttle");

        TweakApplyResult applied = await _engine.ApplyAsync(tweak);
        TweakStatusReport afterApply = await _engine.GetStatusAsync(tweak);
        TweakApplyResult reverted = await _engine.RevertAsync(tweak);

        Assert.True(applied.Success, applied.Message);
        Assert.Equal(TweakStatus.Active, afterApply.Status);
        Assert.True(reverted.Success);
        Assert.Equal(RegistryValueData.FromDWord(8), Reg.ReadValue(target));
        Assert.Equal(TweakStatus.Inactive, (await _engine.GetStatusAsync(tweak)).Status);
    }

    [Fact]
    public async Task Accessibility_popups_tweak_clears_only_the_hotkey_bit_and_keeps_REG_SZ()
    {
        string[] keys = { "StickyKeys", "Keyboard Response", "ToggleKeys" };
        string[] originals = { "510", "126", "62" };
        for (int i = 0; i < keys.Length; i++)
        {
            Reg.WriteValue(Flags(keys[i]), RegistryValueData.FromString(originals[i]));
        }

        TweakDefinition tweak = Tweak("perf.input.accessibility_hotkeys");
        Assert.True((await _engine.ApplyAsync(tweak)).Success);

        Assert.Equal(RegistryValueData.FromString("506"), Reg.ReadValue(Flags("StickyKeys")));
        Assert.Equal(RegistryValueData.FromString("122"), Reg.ReadValue(Flags("Keyboard Response")));
        Assert.Equal(RegistryValueData.FromString("58"), Reg.ReadValue(Flags("ToggleKeys")));
        Assert.Equal(TweakStatus.Active, (await _engine.GetStatusAsync(tweak)).Status);

        await _engine.RevertAsync(tweak);
        for (int i = 0; i < keys.Length; i++)
        {
            Assert.Equal(RegistryValueData.FromString(originals[i]), Reg.ReadValue(Flags(keys[i])));
        }

        static RegistryTarget Flags(string key) =>
            new(RegistryHiveName.HKCU, RegistryViewName.Registry64, $@"Control Panel\Accessibility\{key}", "Flags");
    }

    [Fact]
    public async Task Danger_zone_tweaks_are_refused_until_danger_zone_is_enabled()
    {
        TweakDefinition dz = Tweak("perf.cpu.disable_paging_executive");

        TweakApplyResult locked = await _engine.ApplyAsync(dz);
        Assert.False(locked.Success);
        Assert.Empty(await _h.Snapshots.GetActiveAsync());

        await _settings.UpdateAsync(s => s.DangerZoneEnabled = true);
        TweakApplyResult unlocked = await _engine.ApplyAsync(dz);
        Assert.True(unlocked.Success, unlocked.Message);
        Assert.True(unlocked.RequiresRestart);
    }

    [Fact]
    public async Task Recommended_presets_never_contain_danger_zone_or_permanent_tweaks()
    {
        foreach (TweakDefinition preset in _engine.Tweaks.Where(t => t.IsPreset))
        {
            IReadOnlyList<TweakDefinition> members = _engine.GetPresetTweaks(preset.PresetScope!);
            Assert.NotEmpty(members);
            Assert.DoesNotContain(members, t => t.IsDangerZone || t.IsPermanent || t.IsReadOnly || t.Interactive is not null);
        }

        await _settings.UpdateAsync(s => s.DangerZoneEnabled = true); // even with Danger Zone on
        Assert.DoesNotContain(_engine.GetPresetTweaks(new PresetScope { Category = "Services" }), t => t.IsDangerZone);
    }

    [Fact]
    public async Task Preset_meta_tweak_applies_its_scope()
    {
        TweakApplyResult result = await _engine.ApplyAsync(Tweak("system.ui.apply_recommended"));

        Assert.True(result.Success, result.Message);
        Assert.Equal(_engine.GetPresetTweaks(new PresetScope { Category = "System", Subcategory = "Windows UI Tweaks" }).Count,
            result.ActionsApplied);
        Assert.Equal(RegistryValueData.FromDWord(1), Reg.ReadValue(new RegistryTarget(RegistryHiveName.HKCU,
            RegistryViewName.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "LaunchTo")));
    }

    [Fact]
    public async Task A_failing_action_rolls_back_the_tweaks_earlier_actions()
    {
        TweakDefinition tweak = Tweak("privacy.activity_history"); // three registry values
        var first = new RegistryTarget(RegistryHiveName.HKLM, RegistryViewName.Registry64, @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed");
        Reg.FailWritesWhen = t => t.ValueName == "UploadUserActivities";

        TweakApplyResult result = await _engine.ApplyAsync(tweak);

        Assert.False(result.Success);
        Assert.Contains("rolled back", result.Message);
        Assert.Null(Reg.ReadValue(first));
        Assert.Empty(await _h.Snapshots.GetActiveAsync());
    }

    [Fact]
    public async Task Nvidia_shader_cache_options_write_or_remove_the_drs_override_with_snapshots()
    {
        const uint shaderCache = 0x00A06946;
        _h.NvApi.BaseProfile[shaderCache] = 4096;
        TweakDefinition tweak = Tweak("perf.nvidia.shader_cache");

        await _engine.ApplyAsync(tweak); // default option = 100 GB
        Assert.Equal(102400u, _h.NvApi.BaseProfile[shaderCache]);

        await _engine.ApplyAsync(tweak, new TweakParameters { OptionId = "default" });
        Assert.False(_h.NvApi.BaseProfile.ContainsKey(shaderCache));

        await _engine.RevertAsync(tweak);
        Assert.Equal(4096u, _h.NvApi.BaseProfile[shaderCache]);
    }

    [Fact]
    public async Task Hybrid_scheduler_option_sets_sched_policies_on_the_active_scheme_and_reverts()
    {
        Guid sub = PowerRunner.Guids.SubProcessor;
        _h.Power.Values[(_h.Power.ActiveScheme, sub, PowerRunner.Guids.SchedPolicy)] = (5, 5);
        _h.Power.Values[(_h.Power.ActiveScheme, sub, PowerRunner.Guids.ShortSchedPolicy)] = (5, 5);
        TweakDefinition tweak = Tweak("perf.cpu.hybrid_scheduling");

        Assert.Equal(TweakStatus.Inactive, (await _engine.GetStatusAsync(tweak)).Status);
        await _engine.ApplyAsync(tweak, new TweakParameters { OptionId = "short_p" });

        Assert.Equal(5u, _h.Power.Values[(_h.Power.ActiveScheme, sub, PowerRunner.Guids.SchedPolicy)].Ac);
        Assert.Equal(2u, _h.Power.Values[(_h.Power.ActiveScheme, sub, PowerRunner.Guids.ShortSchedPolicy)].Ac);
        Assert.Equal(TweakStatus.Active, (await _engine.GetStatusAsync(tweak, new TweakParameters { OptionId = "short_p" })).Status);

        await _engine.RevertAsync(tweak);
        Assert.Equal((5u, 5u), _h.Power.Values[(_h.Power.ActiveScheme, sub, PowerRunner.Guids.ShortSchedPolicy)]);
    }

    [Fact]
    public async Task Power_plan_sliders_are_clamped_to_their_range()
    {
        Guid sub = PowerRunner.Guids.SubProcessor;
        Guid minCpu = Guid.Parse("893dee8e-2bef-41e0-89c6-b55d0929964c");
        foreach (TweakAction a in Tweak("perf.power.configure_plan").Actions)
        {
            _h.Power.Values[(_h.Power.ActiveScheme, Guid.Parse(a.Subgroup!), Guid.Parse(a.Setting!))] = (10, 10);
        }

        var parameters = new TweakParameters();
        parameters.Values["min_cpu"] = 250; // above the 100% maximum
        await _engine.ApplyAsync(Tweak("perf.power.configure_plan"), parameters);

        Assert.Equal(100u, _h.Power.Values[(_h.Power.ActiveScheme, sub, minCpu)].Ac);
    }

    [Fact]
    public async Task Missing_services_are_skipped_not_failed()
    {
        _h.Services.Services["DiagTrack"] = new ServiceState("DiagTrack", ServiceStartType.Automatic, true);

        TweakApplyResult result = await _engine.ApplyAsync(Tweak("services.group_telemetry")); // dmwappushservice absent

        Assert.True(result.Success, result.Message);
        Assert.Contains("not installed, skipped", result.Message);
        Assert.Equal(ServiceStartType.Disabled, _h.Services.Query("DiagTrack")!.StartType);
    }

    [Fact]
    public void Status_aggregation_rules()
    {
        ActionState a = new(ActionStatus.Applied, "a"), n = new(ActionStatus.NotApplied, "n"),
            na = new(ActionStatus.NotApplicable, "x"), info = ActionState.Info("i", "v");

        Assert.Equal(TweakStatus.Active, TweakEngine.Aggregate(new[] { a, na }, false));
        Assert.Equal(TweakStatus.Partial, TweakEngine.Aggregate(new[] { a, n }, false));
        Assert.Equal(TweakStatus.Inactive, TweakEngine.Aggregate(new[] { n }, false));
        Assert.Equal(TweakStatus.NotApplicable, TweakEngine.Aggregate(new[] { na }, false));
        Assert.Equal(TweakStatus.Informational, TweakEngine.Aggregate(new[] { info }, false));
        Assert.Equal(TweakStatus.Active, TweakEngine.Aggregate(new[] { info }, true));
    }

    [Fact]
    public void Affinity_masks_are_labelled_by_core_type()
    {
        var cores = Enumerable.Range(0, 8).Select(i => new CpuCore(i, 1, true, true, new[] { 2 * i, 2 * i + 1 }))
            .Concat(Enumerable.Range(0, 12).Select(i => new CpuCore(8 + i, 0, false, false, new[] { 16 + i })))
            .ToList();

        string label = PciActionHandler.DescribeMask(0b1111UL | (1UL << 16), new CpuTopology(cores));

        Assert.Equal("P-cores 0–3 · E-cores 16", label);
    }

    [Fact]
    public async Task Custom_tweaks_file_extends_the_catalog()
    {
        string custom = _h.Paths.CustomTweaksFile;
        Directory.CreateDirectory(_h.Paths.Root);
        await File.WriteAllTextAsync(custom, """
        { "schemaVersion": 1, "tweaks": [ { "id": "custom.test", "name": "My tweak", "category": "Performance",
          "subcategory": "Custom", "description": "d", "targetSummary": "t",
          "actions": [ { "type": "registry", "hive": "HKCU", "path": "Software\\RigTuneTest", "valueName": "V", "valueKind": "DWord", "applyValue": 1 } ] } ] }
        """);

        var catalog = new TweakCatalogService(RigTunePaths.BundledTweaksFile, custom);

        Assert.Equal(186, catalog.Tweaks.Count);
        Assert.NotNull(catalog.Find("custom.test"));
        Assert.Empty(catalog.LoadErrors);
    }

    [Fact]
    public void Registry_value_parser_handles_hex_strings_and_kinds()
    {
        var json = System.Text.Json.JsonDocument.Parse("""["0xFFFFFFFF", 38, "0x80000001"]""").RootElement;

        Assert.Equal(RegistryValueData.FromDWord(uint.MaxValue), TweakValueParser.ToRegistryValue(json[0], RegistryValueKind.DWord));
        Assert.Equal(RegistryValueData.FromString("38"), TweakValueParser.ToRegistryValue(json[1], RegistryValueKind.String));
        Assert.Equal(0x80000001u, TweakValueParser.ToRegistryValue(json[2], RegistryValueKind.DWord).DWord);
    }

    public void Dispose() => _h.Dispose();
}

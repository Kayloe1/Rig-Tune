using System.Text.Json;
using RigTune.Core.Models;
using RigTune.Core.Runners;
using RigTune.Core.Runners.Handlers;
using RigTune.Core.Services;
using RigTune.Core.Storage;

namespace RigTune.Core.Tests;

/// <summary>Schema integrity and safety contracts of the bundled Catalogs\tweaks.json (SPEC §3.2, §4.1, §6).</summary>
public sealed class TweakCatalogTests
{
    private static readonly TweakCatalog Catalog = TweakCatalogService.LoadFile(RigTunePaths.BundledTweaksFile);

    private static IEnumerable<TweakDefinition> Tweaks => Catalog.Tweaks;

    [Fact]
    public void Catalog_is_copied_to_output_and_passes_full_validation()
    {
        Assert.True(File.Exists(RigTunePaths.BundledTweaksFile));
        Assert.Empty(TweakCatalogValidator.Validate(Catalog));
    }

    [Fact]
    public void Catalog_contains_exactly_185_tweaks() => Assert.Equal(185, Catalog.Tweaks.Count);

    [Fact]
    public void Tweak_ids_are_unique() =>
        Assert.Equal(Catalog.Tweaks.Count, Tweaks.Select(t => t.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());

    [Theory]
    [InlineData("Privacy", 50)]
    [InlineData("Debloat", 13)]
    [InlineData("Services", 24)]
    [InlineData("Performance", 64)]
    [InlineData("System", 27)]
    [InlineData("System Care", 7)]
    public void Category_counts_match_spec(string category, int expected) =>
        Assert.Equal(expected, Tweaks.Count(t => t.Category == category));

    [Theory]
    [InlineData("Windows Privacy", 18)]
    [InlineData("Ads & Suggestions", 5)]
    [InlineData("Search & Cortana", 4)]
    [InlineData("Sync & Clipboard", 4)]
    [InlineData("AI Features", 6)]
    [InlineData("Microsoft Edge", 8)]
    [InlineData("Google Chrome", 3)]
    [InlineData("Mozilla Firefox", 2)]
    [InlineData("Optional Windows Features", 8)]
    [InlineData("Microsoft Store & Edge", 5)]
    [InlineData("Service Groups", 5)]
    [InlineData("Individual Services", 19)]
    [InlineData("Power Plans", 3)]
    [InlineData("CPU Scheduling", 8)]
    [InlineData("Graphics Pipeline", 8)]
    [InlineData("NVIDIA Driver", 6)]
    [InlineData("Input & Device Pipeline", 17)]
    [InlineData("Network Tuning", 12)]
    [InlineData("Registry & Policy", 10)]
    [InlineData("Boot & Recovery", 7)]
    [InlineData("Windows UI Tweaks", 10)]
    [InlineData("Usability & Convenience", 10)]
    [InlineData("System Cleanup", 7)]
    public void Subcategory_counts_match_spec(string subcategory, int expected) =>
        Assert.Equal(expected, Tweaks.Count(t => t.Subcategory == subcategory));

    [Fact]
    public void No_danger_zone_tweak_is_in_a_recommended_preset() =>
        Assert.DoesNotContain(Tweaks, t => t.IsDangerZone && t.InRecommendedPreset);

    [Fact]
    public void No_permanent_read_only_or_interactive_tweak_is_in_a_recommended_preset() =>
        Assert.DoesNotContain(Tweaks, t => t.InRecommendedPreset && (t.IsPermanent || t.IsReadOnly || t.Interactive is not null || t.IsPreset));

    [Fact]
    public void Every_spec_danger_zone_tweak_is_tagged_danger_zone()
    {
        string[] specDangerZone =
        {
            "Toggle SMB 1.0 File Sharing", "Remove or Restore Microsoft Store", "Uninstall OneDrive", "Uninstall Microsoft Edge",
            "Stop Edge from Reinstalling Itself", "Disable Windows Update Service Group", "Disable DoSvc", "Disable WaaSMedicSvc",
            "Keep Kernel in RAM / Disable Paging", "Review/Configure MSI Mode for PCI Devices", "Choose CPU Cores for GPU Interrupts",
            "Choose CPU Cores for USB/Network/Audio Interrupts", "Disable xHCI USB Interrupt Moderation",
            "Disable Windows Update Auto-Download (wuauserv)", "Disable IPv6 Protocol Stack", "Toggle Spectre / Meltdown CPU Mitigations",
            "Disable VBS and HVCI Security Layer", "Disable Hyper-V Hypervisor", "Toggle Windows Recovery Environment (WinRE)",
            "Disable Hibernation File",
        };

        Assert.Equal(specDangerZone.OrderBy(n => n), Tweaks.Where(t => t.IsDangerZone).Select(t => t.Name).OrderBy(n => n));
    }

    [Fact]
    public void Every_action_type_is_a_spec_runner_type() =>
        Assert.All(Tweaks.SelectMany(t => t.Actions), a => Assert.Contains(a.Type, TweakActionTypes.All));

    [Fact]
    public void Every_action_type_in_the_catalog_has_a_registered_handler()
    {
        TweakEngine engine = TweakEngineTests.CreateFullEngineForCatalog();

        foreach (string type in Tweaks.SelectMany(t => t.Actions).Select(a => a.Type).Distinct())
        {
            Assert.True(engine.CanHandle(type), $"No handler for '{type}'.");
        }
    }

    [Fact]
    public void Non_preset_tweaks_have_actions_and_preset_tweaks_have_none()
    {
        Assert.All(Tweaks.Where(t => !t.IsPreset), t => Assert.NotEmpty(t.Actions));
        Assert.All(Tweaks.Where(t => t.IsPreset), t => Assert.Empty(t.Actions));
        Assert.Equal(8, Tweaks.Count(t => t.IsPreset));
    }

    [Fact]
    public void Excluded_hardware_and_features_are_absent()
    {
        foreach (TweakDefinition t in Tweaks)
        {
            string text = JsonSerializer.Serialize(t, AtomicJsonFile.DefaultOptions);
            Assert.DoesNotContain("Radeon", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SvcHostSplitThreshold", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Defrag", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotMatch(@"\bAMD\b", text);
        }
    }

    [Fact]
    public void Intel_hybrid_scheduler_and_microcode_entries_are_present()
    {
        TweakDefinition hybrid = Tweaks.Single(t => t.Id == "perf.cpu.hybrid_scheduling");
        Assert.All(hybrid.Actions, a => Assert.Equal(PowerRunner.Guids.SubProcessor, Guid.Parse(a.Subgroup!)));
        Assert.Equal(
            new[] { PowerRunner.Guids.SchedPolicy, PowerRunner.Guids.ShortSchedPolicy },
            hybrid.Actions.Select(a => Guid.Parse(a.Setting!)));
        Assert.Contains(Tweaks.Single(t => t.Id == "perf.power.expose_hidden").Actions,
            a => Guid.Parse(a.Setting!) == PowerRunner.Guids.HeteroPolicy);
        Assert.Equal(TweakActionTypes.CpuMicrocodeInspect,
            Assert.Single(Tweaks.Single(t => t.Id == "perf.cpu.microcode_topology").Actions).Type);
    }

    [Fact]
    public void Three_nvidia_drs_tweaks_target_the_spec_setting_ids()
    {
        Assert.Equal("0x00A06946", Assert.Single(Tweaks.Single(t => t.Id == "perf.nvidia.shader_cache").Actions).SettingId);
        Assert.Equal("0x1057EB71", Assert.Single(Tweaks.Single(t => t.Id == "perf.nvidia.power_mode").Actions).SettingId);
        Assert.All(Tweaks.Single(t => t.Id == "perf.nvidia.rebar").Actions, a => Assert.Equal(TweakActionTypes.NvApiDrs, a.Type));
        Assert.Equal(new[] { "default", "10gb", "100gb", "unlimited" },
            Tweaks.Single(t => t.Id == "perf.nvidia.shader_cache").Options!.Select(o => o.Id));
    }

    [Fact]
    public void Sticky_filter_toggle_keys_tweak_clears_the_hotkey_bit_on_all_three_keys()
    {
        TweakDefinition t = Tweaks.Single(t => t.Id == "perf.input.accessibility_hotkeys");

        Assert.Equal(new[] { "StickyKeys", "Keyboard Response", "ToggleKeys" }, t.Actions.Select(a => a.Path!.Split('\\').Last()));
        Assert.All(t.Actions, a =>
        {
            Assert.Equal("clearBits", a.Operation);
            Assert.Equal(4u, a.BitMask);
            Assert.Equal("Flags", a.ValueName);
        });
    }

    [Fact]
    public void Validator_rejects_safety_contract_violations()
    {
        var bad = new TweakCatalog
        {
            Tweaks =
            {
                new TweakDefinition
                {
                    Id = "x", Name = "X", Category = "C", Subcategory = "S", Description = "d", TargetSummary = "t",
                    RiskTier = TweakRiskTiers.DangerZone, InRecommendedPreset = true,
                    Actions = { new TweakAction { Type = "amd_software" } },
                },
                new TweakDefinition
                {
                    Id = "x", Name = "Radeon thing", Category = "C", Subcategory = "S", Description = "d", TargetSummary = "t",
                    Actions = { new TweakAction { Type = "registry", Hive = "HKCU", Path = "p", ValueName = "v" } },
                },
            },
        };

        IReadOnlyList<string> errors = TweakCatalogValidator.Validate(bad);

        Assert.Contains(errors, e => e.Contains("Expected 185"));
        Assert.Contains(errors, e => e.Contains("Duplicate tweak id"));
        Assert.Contains(errors, e => e.Contains("cannot be in a Recommended preset"));
        Assert.Contains(errors, e => e.Contains("invalid action type 'amd_software'"));
        Assert.Contains(errors, e => e.Contains("out-of-scope term 'Radeon'"));
        Assert.Contains(errors, e => e.Contains("registry set needs valueKind and applyValue"));
    }

    [Fact]
    public void Every_registry_value_in_the_catalog_parses_to_its_declared_kind()
    {
        foreach (TweakAction a in Tweaks.SelectMany(t => t.Actions).Where(a => a.Type == TweakActionTypes.Registry && a.ApplyValue is not null))
        {
            RegistryValueData data = TweakValueParser.ToRegistryValue(a.ApplyValue!.Value, TweakValueParser.ParseKind(a.ValueKind));
            Assert.Equal(TweakValueParser.ParseKind(a.ValueKind), data.Kind);
        }
    }
}

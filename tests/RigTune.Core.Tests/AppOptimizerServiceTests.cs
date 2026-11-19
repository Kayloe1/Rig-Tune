using System.Text.Json.Nodes;
using RigTune.Core.Models;
using RigTune.Core.Runners.Handlers;
using RigTune.Core.Services;
using RigTune.Core.Services.AppOptimizer;
using RigTune.Core.Services.SystemCare;
using RigTune.Core.Storage;
using RigTune.Core.Tests.Fakes;

namespace RigTune.Core.Tests;

public sealed class AppOptimizerServiceTests : IDisposable
{
    private readonly SnapshotHarness _h;
    private readonly TempDir _root = new();
    private readonly AppOptimizerService _service;
    private readonly Dictionary<string, string> _env;
    private readonly ChromiumFlagStateRunner _flags = new();

    public AppOptimizerServiceTests()
    {
        _h = new SnapshotHarness(stateRunners: new[] { _flags });
        _env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["LOCALAPPDATA"] = Path.Combine(_root.Path, "Local"),
            ["APPDATA"] = Path.Combine(_root.Path, "Roaming"),
            ["PROGRAMDATA"] = Path.Combine(_root.Path, "ProgramData"),
            ["PROGRAMFILES"] = Path.Combine(_root.Path, "PF"),
            ["PROGRAMFILES(X86)"] = Path.Combine(_root.Path, "PF86"),
        };

        var startup = new StartupManager(_h.Registry, _h.Snapshots, _h.Audit, Path.Combine(_root.Path, "su"), Path.Combine(_root.Path, "cs"));
        var engine = new TweakEngine(new TweakCatalogService(RigTunePaths.BundledTweaksFile), _h.Snapshots, new SettingsService(_h.Paths), _h.Audit,
            new ITweakActionHandler[]
            {
                new RegistryActionHandler(_h.Registry),
                new ServiceActionHandler(_h.Services),
                new AppStartupActionHandler(startup, _h.Registry),
                new ChromiumFlagActionHandler(_flags, v => _env.GetValueOrDefault(v)),
            });
        _service = new AppOptimizerService(engine, _h.Registry, _h.Audit, RigTunePaths.BundledAppsFile, _h.Paths.CustomAppsFile,
            new JsonDocumentStore<GameModeDocument>(_h.Paths.GameModeFile), v => _env.GetValueOrDefault(v));
    }

    private InMemoryRegistry Reg => (InMemoryRegistry)_h.Registry;

    private string Make(string relative, int size = 0)
    {
        string path = Path.Combine(_root.Path, relative.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    [Fact]
    public void Path_expander_resolves_wildcard_profiles_and_file_globs()
    {
        Make(@"Local\Google\Chrome\User Data\Default\Cache\a", 10);
        Make(@"Local\Google\Chrome\User Data\Profile 1\Cache\b", 10);
        Make(@"Local\Google\Chrome\User Data\Default\Network\Cookies", 5);
        Make(@"Local\Google\Chrome\User Data\Default\Network\Cookies-journal", 5);

        Assert.Equal(2, AppPathExpander.Expand(@"%LOCALAPPDATA%\Google\Chrome\User Data\*\Cache", v => _env.GetValueOrDefault(v)).Count);
        Assert.Equal(2, AppPathExpander.Expand(@"%LOCALAPPDATA%\Google\Chrome\User Data\*\Network\Cookies*", v => _env.GetValueOrDefault(v)).Count);
        Assert.Empty(AppPathExpander.Expand(@"%NOPE%\x", v => _env.GetValueOrDefault(v)));
    }

    [Fact]
    public async Task Installed_apps_are_detected_and_uninstalled_ones_hidden()
    {
        Make(@"Local\Discord\Update.exe");
        Reg.WriteValue(new RegistryTarget(RegistryHiveName.HKLM, RegistryViewName.Registry32,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{abc}", "DisplayName"), RegistryValueData.FromString("NVIDIA App 11.0.2"));

        IReadOnlyList<AppDetection> detections = await _service.DetectAsync();

        Assert.Equal(38, detections.Count);
        Assert.Equal(new[] { "discord", "nvidia_app" }, detections.Where(d => d.IsInstalled).Select(d => d.Profile.Id).Order());
    }

    [Fact]
    public async Task Cache_scan_sizes_and_cleaning_keep_cookies_unless_selected()
    {
        Make(@"Local\Google\Chrome\User Data\Default\Cache\data_1", 4096);
        Make(@"Local\Google\Chrome\User Data\Default\Code Cache\js\x", 1024);
        string cookies = Make(@"Local\Google\Chrome\User Data\Default\Network\Cookies", 512);
        AppProfile chrome = _service.Find("chrome")!;

        IReadOnlyList<AppCacheEstimate> scan = await _service.ScanCachesAsync(chrome);
        AppCacheCleanResult result = await _service.CleanCachesAsync(chrome,
            chrome.Caches.Where(c => c.DefaultChecked).Select(c => c.Id));

        Assert.Equal(5120, scan.Single(s => s.CacheId == "web_cache").Bytes);
        Assert.Equal(512, scan.Single(s => s.CacheId == "cookies").Bytes);
        Assert.Equal(5120, result.BytesFreed);
        Assert.True(File.Exists(cookies));
        Assert.True(Directory.Exists(Path.Combine(_env["LOCALAPPDATA"], "Google", "Chrome", "User Data", "Default", "Cache")));
        Assert.Contains(await _h.Audit.GetAllAsync(), e => e.Action == AuditAction.Cleaned && e.TweakId == "app.chrome.cache");
    }

    [Fact]
    public async Task Browser_policy_toggle_applies_and_reverts_through_snapshots()
    {
        AppProfile chrome = _service.Find("chrome")!;
        AppToggle bg = chrome.Toggles.Single(t => t.Id == "bg_apps");
        var policy = new RegistryTarget(RegistryHiveName.HKLM, RegistryViewName.Registry64, @"SOFTWARE\Policies\Google\Chrome", "BackgroundModeEnabled");

        Assert.Equal(TweakStatus.Inactive, (await _service.GetToggleStatusAsync(chrome, bg)).Status);
        Assert.True((await _service.ApplyToggleAsync(chrome, bg)).Success);
        Assert.Equal(RegistryValueData.FromDWord(0), Reg.ReadValue(policy));
        Assert.Equal(TweakStatus.Active, (await _service.GetToggleStatusAsync(chrome, bg)).Status);

        await _service.RevertToggleAsync(chrome, bg);
        Assert.Null(Reg.ReadValue(policy));
    }

    [Fact]
    public async Task Auto_start_toggle_uses_startup_approved_and_packaged_task_state()
    {
        var run = new RegistryTarget(RegistryHiveName.HKCU, RegistryViewName.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Run", "Spotify");
        Reg.WriteValue(run, RegistryValueData.FromString(@"C:\Spotify\Spotify.exe /minimized"));
        var task = new RegistryTarget(RegistryHiveName.HKCU, RegistryViewName.Registry64,
            $@"{AppStartupActionHandler.PackagedAppDataKey}\SpotifyAB.SpotifyMusic_zpdnekdrzrea0\SpotifyStartupTask", "State");
        Reg.WriteValue(task, RegistryValueData.FromDWord(2));
        AppProfile spotify = _service.Find("spotify")!;
        AppToggle auto = spotify.Toggles.Single(t => t.Id == "auto_start");

        TweakApplyResult r = await _service.ApplyToggleAsync(spotify, auto);

        Assert.True(r.Success, r.Message);
        Assert.False(StartupApprovedFlags.IsEnabled(Reg.ReadValue(StartupManager.ApprovedTarget(StartupLocation.CurrentUserRun, "Spotify"))!.GetBytes()));
        Assert.Equal(RegistryValueData.FromDWord(1), Reg.ReadValue(task));
        Assert.NotNull(Reg.ReadValue(run)); // original entry untouched
        Assert.Equal(TweakStatus.Active, (await _service.GetToggleStatusAsync(spotify, auto)).Status);

        await _service.RevertToggleAsync(spotify, auto);
        Assert.Equal(RegistryValueData.FromDWord(2), Reg.ReadValue(task));
        Assert.Null(Reg.ReadValue(StartupManager.ApprovedTarget(StartupLocation.CurrentUserRun, "Spotify")));
    }

    [Fact]
    public async Task Parallel_downloading_flag_edits_local_state_and_reverts_exactly()
    {
        string localState = Make(@"Local\Google\Chrome\User Data\Local State");
        await File.WriteAllTextAsync(localState, """{"browser":{"enabled_labs_experiments":["smooth-scrolling@2"]},"profile":{"x":1}}""");
        AppProfile chrome = _service.Find("chrome")!;
        AppToggle flag = chrome.Toggles.Single(t => t.Id == "parallel_dl");

        TweakApplyResult r = await _service.ApplyToggleAsync(chrome, flag);

        Assert.True(r.Success, r.Message);
        JsonNode applied = JsonNode.Parse(await File.ReadAllTextAsync(localState))!;
        Assert.Contains("enable-parallel-downloading@1", applied["browser"]!["enabled_labs_experiments"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(1, applied["profile"]!["x"]!.GetValue<int>());

        await _service.RevertToggleAsync(chrome, flag);
        JsonNode reverted = JsonNode.Parse(await File.ReadAllTextAsync(localState))!;
        Assert.Equal(new[] { "smooth-scrolling@2" }, reverted["browser"]!["enabled_labs_experiments"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task While_you_play_rules_default_from_catalog_and_persist_overrides()
    {
        AppProfile discord = _service.Find("discord")!;
        AppProfile rockstar = _service.Find("rockstar")!;

        Assert.Equal("LowerPriority", (await _service.GetWhilePlayingAsync(discord))!.Mode);
        Assert.Null(await _service.GetWhilePlayingAsync(rockstar));

        await _service.SetWhilePlayingAsync(discord, new WhilePlayingRule { Enabled = false, Mode = "EcoQoS" });

        GameModeDocument doc = await AtomicJsonFile.ReadOrDefaultAsync(_h.Paths.GameModeFile, () => new GameModeDocument());
        Assert.False(doc.AppRules["discord"].Enabled);
        Assert.False((await _service.GetWhilePlayingAsync(discord))!.Enabled);
        Assert.DoesNotContain("discord", (await _service.GetEnabledRulesAsync()).Keys);
        await Assert.ThrowsAsync<ArgumentException>(() => _service.SetWhilePlayingAsync(discord, new WhilePlayingRule { Mode = "Turbo" }));
    }

    [Fact]
    public async Task Custom_apps_are_saved_loaded_edited_and_deleted()
    {
        AppProfile custom = AppOptimizerService.BuildCustomApp("OBS Studio", AppCategories.GpuCapture, @"%PROGRAMFILES%\obs-studio\bin\64bit\obs64.exe",
            new[] { @"%APPDATA%\obs-studio\logs" }, "OBS*", new WhilePlayingRule { Enabled = true, Mode = "LowerPriority" });

        Assert.Empty(await _service.SaveCustomAppAsync(custom));
        Assert.Equal(39, _service.Apps.Count);
        AppProfile loaded = _service.Find("custom_obsstudio")!;
        Assert.True(loaded.IsCustom);
        Assert.Equal("obs64.exe", Assert.Single(loaded.Processes));

        loaded.Name = "OBS";
        Assert.Empty(await _service.SaveCustomAppAsync(loaded));
        Assert.Equal("OBS", _service.Find("custom_obsstudio")!.Name);

        await _service.DeleteCustomAppAsync("custom_obsstudio");
        Assert.Equal(38, _service.Apps.Count);
    }

    [Fact]
    public async Task Custom_apps_are_validated_and_cannot_shadow_built_ins()
    {
        AppProfile bad = AppOptimizerService.BuildCustomApp("X", AppCategories.Media, @"C:\x.exe", Array.Empty<string>(), null, null);
        Assert.NotEmpty(await _service.SaveCustomAppAsync(bad)); // path without env variable

        AppProfile clash = AppOptimizerService.BuildCustomApp("Chrome", AppCategories.Browsers, @"%PROGRAMFILES%\c.exe", Array.Empty<string>(), null, null);
        clash.Id = "chrome";
        clash.IsCustom = false;
        Assert.NotEmpty(await _service.SaveCustomAppAsync(clash));
    }

    public void Dispose()
    {
        _h.Dispose();
        _root.Dispose();
    }
}

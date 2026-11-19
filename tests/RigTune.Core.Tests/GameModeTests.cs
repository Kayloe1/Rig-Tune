using RigTune.Core.Models;
using RigTune.Core.Services;
using RigTune.Core.Services.GameMode;
using RigTune.Core.Storage;
using RigTune.Core.Tests.Fakes;

namespace RigTune.Core.Tests;

public sealed class VdfParsingTests
{
    private const string LibraryFolders = """
        "libraryfolders"
        {
            "0"
            {
                "path"		"C:\\Program Files (x86)\\Steam"
                "label"		""
                "contentid"		"123"
                "apps"
                {
                    "228980"		"175165505"
                    "730"		"36741228383"
                }
            }
            "1"
            {
                "path"		"D:\\SteamLibrary"
                "apps"
                {
                    "1086940"		"150000000000"
                }
            }
        }
        """;

    [Fact]
    public void Parses_nested_keyvalues_with_escapes_and_comments()
    {
        VdfNode root = VdfParser.Parse("""
            // comment
            "Root" { "key" "va\"lue" "path" "C:\\Games\\X" "child" { "n" "1" } bare value }
            """);

        VdfNode r = root["Root"]!;
        Assert.Equal("va\"lue", r.Get("key"));
        Assert.Equal(@"C:\Games\X", r.Get("path"));
        Assert.Equal("1", r["child"]!.Get("n"));
        Assert.Equal("value", r.Get("bare"));
    }

    [Fact]
    public void Library_folders_new_format()
    {
        IReadOnlyList<string> libs = GameLibraryScanner.ParseLibraryFolders(LibraryFolders, @"C:\Program Files (x86)\Steam");

        Assert.Equal(new[] { @"C:\Program Files (x86)\Steam", @"D:\SteamLibrary" }, libs);
    }

    [Fact]
    public void Library_folders_legacy_format()
    {
        const string legacy = """
            "LibraryFolders"
            {
                "TimeNextStatsReport"		"1612345678"
                "ContentStatsID"		"-123"
                "1"		"E:\\Games\\Steam"
            }
            """;

        Assert.Equal(new[] { @"C:\Steam", @"E:\Games\Steam" }, GameLibraryScanner.ParseLibraryFolders(legacy, @"C:\Steam"));
    }

    [Fact]
    public void App_manifest_yields_name_and_installdir()
    {
        const string acf = """
            "AppState"
            {
                "appid"		"1086940"
                "Universe"		"1"
                "name"		"Baldur's Gate 3"
                "StateFlags"		"4"
                "installdir"		"Baldurs Gate 3"
                "LastUpdated"		"1700000000"
                "SizeOnDisk"		"150000000000"
            }
            """;

        var m = GameLibraryScanner.ParseAppManifest(acf)!.Value;

        Assert.Equal(("1086940", "Baldur's Gate 3", "Baldurs Gate 3"), (m.AppId, m.Name, m.InstallDir));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), m.LastUpdated);
    }

    [Theory]
    [InlineData("Steamworks Common Redistributables")]
    [InlineData("Proton 9.0")]
    [InlineData("Steam Linux Runtime 3.0 (sniper)")]
    [InlineData("Wallpaper Engine")]
    [InlineData("SteamVR")]
    public void Tools_and_redistributables_are_not_games(string name) =>
        Assert.Null(GameLibraryScanner.ParseAppManifest($"\"AppState\" {{ \"appid\" \"1\" \"name\" \"{name}\" \"installdir\" \"x\" }}"));

    [Fact]
    public void Scans_a_steam_library_and_filters_helper_executables()
    {
        using var dir = new TempDir();
        string steam = Path.Combine(dir.Path, "Steam");
        string lib2 = Path.Combine(dir.Path, "Lib2");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
            $"\"libraryfolders\" {{ \"0\" {{ \"path\" \"{steam.Replace("\\", "\\\\")}\" }} \"1\" {{ \"path\" \"{lib2.Replace("\\", "\\\\")}\" }} }}");
        WriteGame(lib2, "1086940", "Baldur's Gate 3", "Baldurs Gate 3", "bin/bg3.exe", "bin/bg3_dx11.exe", "Launcher/LariLauncher.exe",
            "_CommonRedist/vcredist_x64.exe", "bin/CrashReporter.exe");
        WriteGame(steam, "228980", "Steamworks Common Redistributables", "Steamworks Shared", "x.exe");

        var scanner = new GameLibraryScanner(new InMemoryRegistry(), Array.Empty<string>, dir.Path);
        DiscoveredGame game = Assert.Single(scanner.ScanSteam(steam));

        Assert.Equal("Baldur's Gate 3", game.Name);
        Assert.Equal(GameSource.Steam, game.Source);
        Assert.Equal(new[] { "bg3.exe", "bg3_dx11.exe" }, game.Executables.Select(e => Path.GetFileName(e)!).Order(StringComparer.Ordinal));
    }

    private static void WriteGame(string library, string appId, string name, string installDir, params string[] exes)
    {
        string steamapps = Path.Combine(library, "steamapps");
        Directory.CreateDirectory(steamapps);
        File.WriteAllText(Path.Combine(steamapps, $"appmanifest_{appId}.acf"),
            $"\"AppState\" {{ \"appid\" \"{appId}\" \"name\" \"{name}\" \"installdir\" \"{installDir}\" }}");
        foreach (string exe in exes)
        {
            string path = Path.Combine(steamapps, "common", installDir, exe.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "MZ");
        }
    }

    [Fact]
    public void Epic_manifests_and_xbox_folders_are_discovered()
    {
        using var dir = new TempDir();
        string manifests = Path.Combine(dir.Path, "Epic", "EpicGamesLauncher", "Data", "Manifests");
        string fortnite = Path.Combine(dir.Path, "Games", "Fortnite");
        Directory.CreateDirectory(manifests);
        Directory.CreateDirectory(Path.Combine(fortnite, "FortniteGame", "Binaries", "Win64"));
        File.WriteAllText(Path.Combine(fortnite, "FortniteGame", "Binaries", "Win64", "FortniteClient-Win64-Shipping.exe"), "MZ");
        File.WriteAllText(Path.Combine(manifests, "a.item"), $$"""
            { "DisplayName": "Fortnite", "InstallLocation": {{System.Text.Json.JsonSerializer.Serialize(fortnite)}},
              "LaunchExecutable": "FortniteGame/Binaries/Win64/FortniteClient-Win64-Shipping.exe", "AppCategories": ["public", "games"] }
            """);
        string xbox = Path.Combine(dir.Path, "D", "XboxGames", "Starfield", "Content");
        Directory.CreateDirectory(xbox);
        File.WriteAllText(Path.Combine(xbox, "Starfield.exe"), "MZ");
        File.WriteAllText(Path.Combine(xbox, "MicrosoftGame.config"),
            """<Game><ShellVisuals DefaultDisplayName="Starfield" /><ExecutableList><Executable Name="Starfield.exe" /></ExecutableList></Game>""");

        var scanner = new GameLibraryScanner(new InMemoryRegistry(), () => new[] { Path.Combine(dir.Path, "D") }, dir.Path);

        DiscoveredGame epic = Assert.Single(scanner.ScanEpic(manifests));
        DiscoveredGame starfield = Assert.Single(scanner.ScanXbox(new[] { Path.Combine(dir.Path, "D") }));
        Assert.Equal("Fortnite", epic.Name);
        Assert.EndsWith("FortniteClient-Win64-Shipping.exe", epic.Executables[0]);
        Assert.Equal(("Starfield", GameSource.Xbox), (starfield.Name, starfield.Source));
        Assert.EndsWith("Starfield.exe", Assert.Single(starfield.Executables));
    }
}

public sealed class GameMatcherTests
{
    private static readonly DiscoveredGame Bg3 = new("Baldur's Gate 3", GameSource.Steam, @"D:\SteamLibrary\steamapps\common\Baldurs Gate 3",
        new[] { @"D:\SteamLibrary\steamapps\common\Baldurs Gate 3\bin\bg3.exe" });

    [Fact]
    public void Matches_known_exe_and_any_game_exe_inside_install_folder()
    {
        var m = new GameMatcher(new[] { Bg3 }, Array.Empty<string>(), Array.Empty<string>());

        Assert.Equal("Baldur's Gate 3", m.Match("bg3.exe", @"D:\SteamLibrary\steamapps\common\Baldurs Gate 3\bin\bg3.exe"));
        Assert.Equal("Baldur's Gate 3", m.Match("bg3_vulkan.exe", @"D:\SteamLibrary\steamapps\common\Baldurs Gate 3\bin\bg3_vulkan.exe"));
        Assert.Null(m.Match("CrashReporter.exe", @"D:\SteamLibrary\steamapps\common\Baldurs Gate 3\bin\CrashReporter.exe"));
        Assert.Null(m.Match("chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe"));
    }

    [Fact]
    public void Allowlist_and_exclusions_override_discovery()
    {
        var m = new GameMatcher(new[] { Bg3 }, new[] { "notepad.exe" }, new[] { "bg3.exe" });

        Assert.Equal("notepad", m.Match("notepad.exe", @"C:\Windows\System32\notepad.exe"));
        Assert.Null(m.Match("bg3.exe", @"D:\SteamLibrary\steamapps\common\Baldurs Gate 3\bin\bg3.exe"));
    }

    [Theory]
    [InlineData("unins000.exe", true)]
    [InlineData("UnityCrashHandler64.exe", true)]
    [InlineData("vc_redist.x64.exe", true)]
    [InlineData("EasyAntiCheat_EOS_Setup.exe", true)]
    [InlineData("RockstarLauncher.exe", true)]
    [InlineData("eldenring.exe", false)]
    [InlineData("Cyberpunk2077.exe", false)]
    public void Executable_filter(string exe, bool excluded) => Assert.Equal(excluded, GameExeFilter.IsExcludedExe(exe));
}

public sealed class RefreshRateWatchdogTests : IDisposable
{
    private readonly SnapshotHarness _h = new();

    private static DisplayInfo Display(string device, int hz, params int[] supported) =>
        new(device, $@"\\?\DISPLAY#GSM{device[^1]}", $"Monitor {device[^1]}", device.EndsWith('1'), 2560, 1440, 32, hz, supported);

    [Fact]
    public void Drift_is_any_monitor_below_its_pinned_target()
    {
        var targets = new Dictionary<string, int>
        {
            [@"\\?\DISPLAY#GSM1"] = 165,
            [@"\\?\DISPLAY#GSM2"] = 60,
            [@"\\?\DISPLAY#GSM3"] = 144,
        };
        DisplayInfo[] displays =
        {
            Display(@"\\.\DISPLAY1", 60, 60, 120, 165), // dropped to 60
            Display(@"\\.\DISPLAY2", 60, 60),           // at target
            Display(@"\\.\DISPLAY3", 143, 60, 144),     // 143.98 reported as 143 — within tolerance
            Display(@"\\.\DISPLAY4", 60, 60, 240),      // no target pinned
        };

        RefreshDrift drift = Assert.Single(RefreshRateWatchdogService.FindDrift(displays, targets));

        Assert.Equal((@"\\.\DISPLAY1", 60, 165), (drift.Display.DeviceName, drift.CurrentHz, drift.TargetHz));
    }

    [Fact]
    public void Targets_keyed_by_legacy_gdi_name_still_match()
    {
        var targets = new Dictionary<string, int> { [@"\\.\DISPLAY1"] = 165 };

        Assert.Single(RefreshRateWatchdogService.FindDrift(new[] { Display(@"\\.\DISPLAY1", 60, 165) }, targets));
    }

    [Fact]
    public async Task Check_raises_drift_and_restore_returns_monitor_to_target()
    {
        var displays = new FakeDisplays();
        displays.Displays.Add(Display(@"\\.\DISPLAY1", 165, 60, 120, 165));
        var settings = new SettingsService(_h.Paths);
        var watchdog = new RefreshRateWatchdogService(displays, settings, _h.Audit);
        await watchdog.PinTargetAsync(displays.Displays[0], 165);
        displays.Displays[0] = displays.Displays[0] with { CurrentHz = 60 }; // Windows update resets to 60 Hz
        IReadOnlyList<RefreshDrift>? raised = null;
        watchdog.DriftDetected += (_, d) => raised = d;

        IReadOnlyList<RefreshDrift> drift = await watchdog.CheckAsync("test");
        await watchdog.RestoreAllAsync();

        Assert.Single(drift);
        Assert.NotNull(raised);
        Assert.Equal((@"\\.\DISPLAY1", 165), Assert.Single(displays.Changes));
        Assert.False(watchdog.LastStatus.Single().IsDrifted);
        Assert.Contains(await _h.Audit.GetAllAsync(), e => e.Action == AuditAction.DriftDetected);
    }

    [Fact]
    public async Task Auto_restore_fixes_drift_without_raising_the_prompt()
    {
        var displays = new FakeDisplays();
        displays.Displays.Add(Display(@"\\.\DISPLAY1", 60, 60, 144));
        var settings = new SettingsService(_h.Paths);
        await settings.UpdateAsync(s =>
        {
            s.AutoRestoreRefreshRate = true;
            s.RefreshRateTargets[@"\\?\DISPLAY#GSM1"] = 144;
        });
        var watchdog = new RefreshRateWatchdogService(displays, settings, _h.Audit);
        bool raised = false;
        watchdog.DriftDetected += (_, _) => raised = true;

        IReadOnlyList<RefreshDrift> remaining = await watchdog.CheckAsync("startup");

        Assert.Empty(remaining);
        Assert.False(raised);
        Assert.Equal(144, displays.Displays[0].CurrentHz);
    }

    public void Dispose() => _h.Dispose();
}

public sealed class GameModeSessionTests : IDisposable
{
    private static readonly Guid Balanced = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid HighPerformance = Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    private readonly SnapshotHarness _h = new();
    private readonly FakeGameModePlatform _platform = new();
    private readonly SettingsService _settings;
    private readonly JsonDocumentStore<GameModeDocument> _store;

    public GameModeSessionTests()
    {
        _settings = new SettingsService(_h.Paths);
        _store = new JsonDocumentStore<GameModeDocument>(_h.Paths.GameModeFile);
    }

    /// <summary>Reads gamemode.json from disk (each service instance has its own cached store, like separate app runs).</summary>
    private Task<GameModeDocument> ReadDocAsync() =>
        AtomicJsonFile.ReadOrDefaultAsync(_h.Paths.GameModeFile, () => new GameModeDocument());

    private static readonly AppProfile Chrome = new() { Id = "chrome", Name = "Chrome", Processes = { "chrome.exe" } };
    private static readonly AppProfile Discord = new() { Id = "discord", Name = "Discord", Processes = { "Discord.exe" } };

    private GameModeService CreateService() => new(_platform, new JsonDocumentStore<GameModeDocument>(_h.Paths.GameModeFile),
        _settings, _h.Audit,
        _ => Task.FromResult<IReadOnlyList<(AppProfile, WhilePlayingRule)>>(new[]
        {
            (Chrome, new WhilePlayingRule { Enabled = true, Mode = "EcoQoS", CpuPriority = "BelowNormal", IoPriority = "Low" }),
            (Discord, new WhilePlayingRule { Enabled = true, Mode = "LowerPriority", CpuPriority = "BelowNormal", IoPriority = "Normal" }),
        }), time: _h.Clock);

    [Fact]
    public async Task Session_applies_high_priority_ecoqos_and_restores_exact_originals()
    {
        FakeProcess game = _platform.Add(100, "eldenring.exe", @"D:\Games\ELDEN RING\eldenring.exe");
        FakeProcess chrome = _platform.Add(200, "chrome.exe", priority: PriorityClasses.AboveNormal);
        chrome.Throttle = new ThrottleState(0x1, 0x0); // explicitly "high QoS" before
        FakeProcess discord = _platform.Add(300, "Discord.exe");
        FakeProcess other = _platform.Add(400, "explorer.exe");
        GameModeService service = CreateService();

        GameSessionResult start = await service.StartAsync(_platform.GetProcess(100), "ELDEN RING", manual: false);

        Assert.True(start.Success);
        Assert.Equal(PriorityClasses.High, game.Priority);
        Assert.Equal((PriorityClasses.BelowNormal, ThrottleState.EcoQoS, IoPriorities.Low), (chrome.Priority, chrome.Throttle, chrome.Io));
        Assert.Equal((PriorityClasses.BelowNormal, ThrottleState.Default, IoPriorities.Normal), (discord.Priority, discord.Throttle, discord.Io));
        Assert.Equal(PriorityClasses.Normal, other.Priority);
        Assert.Equal(0, chrome.Trims); // memory load 40% < 80%

        await service.StopAsync();

        Assert.Equal(PriorityClasses.Normal, game.Priority);
        Assert.Equal((PriorityClasses.AboveNormal, new ThrottleState(0x1, 0x0), IoPriorities.Normal), (chrome.Priority, chrome.Throttle, chrome.Io));
        Assert.Equal(PriorityClasses.Normal, discord.Priority);
        Assert.Null((await ReadDocAsync()).ActiveSession);
    }

    [Fact]
    public async Task Ram_is_trimmed_only_at_or_above_the_threshold()
    {
        FakeProcess chrome = _platform.Add(200, "chrome.exe");
        _platform.MemoryLoad = 85;
        GameModeService service = CreateService();

        await service.StartAsync(null, null, manual: true);

        Assert.Equal(1, chrome.Trims);
    }

    [Fact]
    public async Task Power_plan_switch_is_opt_in_and_restored()
    {
        await _settings.UpdateAsync(s => s.SwitchPowerPlanDuringGames = true);
        GameModeService service = CreateService();

        await service.StartAsync(null, null, manual: true);
        Assert.Equal(HighPerformance, _platform.PowerPlan);

        await service.StopAsync();
        Assert.Equal(Balanced, _platform.PowerPlan);
    }

    [Fact]
    public async Task Session_is_persisted_before_changes_so_a_crash_can_be_recovered()
    {
        await _settings.UpdateAsync(s => s.SwitchPowerPlanDuringGames = true);
        FakeProcess game = _platform.Add(100, "game.exe");
        FakeProcess chrome = _platform.Add(200, "chrome.exe");
        await CreateService().StartAsync(_platform.GetProcess(100), "Game", manual: false);
        // ... RigTune crashes here: no Stop. A new instance starts:
        GameSession? persisted = (await ReadDocAsync()).ActiveSession;
        Assert.NotNull(persisted);
        Assert.Equal(PriorityClasses.Normal, persisted!.Throttled.Single().OriginalPriorityClass);

        GameModeService restarted = CreateService();
        await restarted.RecoverStaleSessionAsync();

        Assert.Equal(PriorityClasses.Normal, game.Priority);
        Assert.Equal((PriorityClasses.Normal, ThrottleState.Default, IoPriorities.Normal), (chrome.Priority, chrome.Throttle, chrome.Io));
        Assert.Equal(Balanced, _platform.PowerPlan);
        Assert.Null((await ReadDocAsync()).ActiveSession);
        Assert.Contains(await _h.Audit.GetAllAsync(), e => e.Summary.Contains("Recovered an unfinished Game Mode session"));
    }

    [Fact]
    public async Task Recovery_never_touches_a_new_process_that_reused_a_pid()
    {
        await _store.UpdateAsync(d => d.ActiveSession = new GameSession
        {
            GamePid = 100,
            GameStartTimeUtc = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            GameOriginalPriorityClass = PriorityClasses.Normal,
            Throttled =
            {
                new ThrottledProcess
                {
                    Pid = 200, Name = "chrome.exe", StartTimeUtc = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                    OriginalPriorityClass = PriorityClasses.Normal, EcoQosApplied = true, OriginalThrottle = ThrottleState.Default,
                },
            },
        });
        // Different processes now own PIDs 100/200 (start times differ by months).
        FakeProcess reused = _platform.Add(200, "notepad.exe", priority: PriorityClasses.High);
        reused.Throttle = ThrottleState.EcoQoS;

        GameSessionResult result = await CreateService().RecoverStaleSessionAsync();

        Assert.Equal(PriorityClasses.High, reused.Priority);
        Assert.Equal(ThrottleState.EcoQoS, reused.Throttle);
        Assert.Contains("already exited", result.Message);
        Assert.Null((await ReadDocAsync()).ActiveSession);
    }

    [Fact]
    public async Task Recovery_with_no_stale_session_is_a_no_op()
    {
        GameSessionResult r = await CreateService().RecoverStaleSessionAsync();

        Assert.True(r.Success);
        Assert.Equal("No stale session.", r.Message);
    }

    [Fact]
    public async Task Only_one_session_at_a_time()
    {
        GameModeService service = CreateService();
        await service.StartAsync(null, null, manual: true);

        GameSessionResult second = await service.StartAsync(null, null, manual: true);

        Assert.False(second.Success);
        await service.StopAsync();
    }

    [Fact]
    public void Start_with_windows_task_runs_highest_with_minimized()
    {
        string xml = StartWithWindowsService.BuildTaskXml(@"C:\Tools\RigTune\RigTune.exe", @"PC\me");

        var doc = System.Xml.Linq.XDocument.Parse(xml);
        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Assert.Equal("HighestAvailable", doc.Descendants(ns + "RunLevel").Single().Value);
        Assert.Equal("--minimized", doc.Descendants(ns + "Arguments").Single().Value);
        Assert.Single(doc.Descendants(ns + "LogonTrigger"));
        Assert.Equal(@"PC\me", doc.Descendants(ns + "LogonTrigger").Single().Element(ns + "UserId")!.Value);
        Assert.Equal("\"C:\\Tools\\RigTune\\RigTune.exe\"", doc.Descendants(ns + "Command").Single().Value);
    }

    public void Dispose() => _h.Dispose();
}

using RigTune.Core.Models;
using RigTune.Core.Services.DiskAnalyzer;
using RigTune.Core.Storage;
using RigTune.Core.Tests.Fakes;

namespace RigTune.Core.Tests;

public sealed class PathGuardTests
{
    private static readonly PathGuard Guard = new(@"C:\Windows", @"C:\Program Files", @"C:\Program Files (x86)", @"C:\ProgramData", @"C:\Users");

    [Theory]
    [InlineData(@"C:\Windows")]
    [InlineData(@"c:\windows\system32\drivers\etc\hosts")]
    [InlineData(@"C:\WINDOWS\Temp")]
    [InlineData(@"C:/Windows/System32/")]
    [InlineData(@"C:\Games\..\Windows\explorer.exe")]
    [InlineData(@"C:\Program Files\WindowsApps")]
    [InlineData(@"C:\Program Files\WINDOWSAPPS\Microsoft.GamingApp_1\x.dll")]
    [InlineData(@"C:\pagefile.sys")]
    [InlineData(@"D:\swapfile.sys")]
    [InlineData(@"C:\HIBERFIL.SYS")]
    [InlineData(@"D:\System Volume Information")]
    [InlineData(@"E:\system volume information\tracking.log")]
    [InlineData(@"C:\$Recycle.Bin")]
    [InlineData(@"D:\$RECYCLE.BIN\S-1-5-21\$RABC.iso")]
    [InlineData(@"C:\Users")]
    [InlineData(@"C:\Users\me")]
    [InlineData(@"c:\users\Public")]
    [InlineData(@"C:\Users\me\AppData")]
    [InlineData(@"C:\")]
    [InlineData(@"D:\")]
    [InlineData(@"D:")]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"C:\Program Files (x86)")]
    [InlineData(@"C:\ProgramData")]
    public void Hard_protected_paths_are_blocked(string path)
    {
        Assert.True(Guard.IsHardProtected(path), path);
        Assert.False(Guard.Evaluate(path, Array.Empty<string>()).Allowed);
    }

    [Theory]
    [InlineData(@"C:\Users\me\Downloads\Win11.iso")]
    [InlineData(@"C:\Users\me\AppData\Local\Temp")]
    [InlineData(@"C:\Program Files\OldApp")]
    [InlineData(@"D:\Games\Old Game")]
    [InlineData(@"C:\Windows.old")]
    [InlineData(@"C:\NVIDIA")]
    [InlineData(@"D:\pagefile.sys.bak")]
    [InlineData(@"D:\Backups\pagefile.sys")]
    public void Ordinary_paths_are_allowed(string path) =>
        Assert.True(Guard.Evaluate(path, Array.Empty<string>()).Allowed, Guard.ProtectionReason(path));

    [Fact]
    public void Locks_cover_the_path_and_every_descendant_case_insensitively()
    {
        string[] locks = { @"D:\Games\Elden Ring" };

        Assert.False(Guard.Evaluate(@"D:\Games\Elden Ring", locks).Allowed);
        Assert.False(Guard.Evaluate(@"d:\GAMES\elden ring\Game\eldenring.exe", locks).Allowed);
        Assert.False(Guard.Evaluate(@"D:/Games/Elden Ring/saves/", locks).Allowed);
        Assert.True(PathGuard.IsLocked(@"D:\GAMES\ELDEN RING\x", locks));
    }

    [Fact]
    public void Deleting_an_ancestor_of_a_locked_path_is_blocked()
    {
        DeletionCheck check = Guard.Evaluate(@"D:\Games", new[] { @"D:\Games\Elden Ring" });

        Assert.False(check.Allowed);
        Assert.Contains("Contains an Important item", check.Reason);
    }

    [Fact]
    public void A_sibling_sharing_a_name_prefix_is_not_locked()
    {
        string[] locks = { @"D:\Games\Elden Ring" };

        Assert.True(Guard.Evaluate(@"D:\Games\Elden Ring 2", locks).Allowed);
        Assert.True(Guard.Evaluate(@"D:\Games\Elden", locks).Allowed);
        Assert.False(PathGuard.IsLocked(@"D:\Games\Elden Ring Nightreign", locks));
    }

    [Theory]
    [InlineData(@"C:/Users//me\.\Downloads\", @"C:\Users\me\Downloads")]
    [InlineData(@"C:\a\b\..\..\..\Windows", @"C:\Windows")]
    [InlineData(@"d:", @"d:\")]
    [InlineData("\"C:\\Games\\X\"", @"C:\Games\X")]
    public void Normalization(string input, string expected) => Assert.Equal(expected, PathGuard.Normalize(input));
}

public sealed class SteamLocalConfigTests
{
    private const string LocalConfig = """
        "UserLocalConfigStore"
        {
            "Software"
            {
                "Valve"
                {
                    "Steam"
                    {
                        "apps"
                        {
                            "1086940"
                            {
                                "LastPlayed"		"1700000000"
                                "Playtime"		"6120"
                            }
                            "730"
                            {
                                "LastPlayed"		"1763400000"
                            }
                            "440"
                            {
                                "cloud"		{ "quota" "0" }
                            }
                        }
                    }
                }
            }
        }
        """;

    [Fact]
    public void Parses_last_played_unix_timestamps()
    {
        IReadOnlyDictionary<string, DateTimeOffset> played = SteamPlayHistory.ParseLastPlayed(LocalConfig);

        Assert.Equal(2, played.Count);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), played["1086940"]);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1763400000), played["730"]);
        Assert.False(played.ContainsKey("440"));
    }

    [Fact]
    public void Key_case_variations_are_tolerated()
    {
        string lower = LocalConfig.Replace("\"Software\"", "\"software\"").Replace("\"Valve\"", "\"valve\"").Replace("\"apps\"", "\"Apps\"");

        Assert.Equal(2, SteamPlayHistory.ParseLastPlayed(lower).Count);
    }

    [Fact]
    public void Multiple_steam_users_are_merged_latest_wins()
    {
        using var dir = new TempDir();
        Write(dir.Path, "111", "\"UserLocalConfigStore\" { \"Software\" { \"Valve\" { \"Steam\" { \"apps\" { \"730\" { \"LastPlayed\" \"100\" } } } } } }");
        Write(dir.Path, "222", "\"UserLocalConfigStore\" { \"Software\" { \"Valve\" { \"Steam\" { \"apps\" { \"730\" { \"LastPlayed\" \"200\" } \"570\" { \"LastPlayed\" \"50\" } } } } } }");

        IReadOnlyDictionary<string, DateTimeOffset> all = SteamPlayHistory.ReadAll(dir.Path);

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(200), all["730"]);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(50), all["570"]);
    }

    private static void Write(string steam, string user, string content)
    {
        string config = Path.Combine(steam, "userdata", user, "config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "localconfig.vdf"), content);
    }

    [Theory]
    [InlineData(10, PlayRecency.Recent)]
    [InlineData(59, PlayRecency.Recent)]
    [InlineData(60, PlayRecency.Dormant60)]
    [InlineData(89, PlayRecency.Dormant60)]
    [InlineData(90, PlayRecency.Dormant90)]
    [InlineData(400, PlayRecency.Dormant90)]
    public void Dormancy_thresholds(int daysAgo, PlayRecency expected)
    {
        var now = new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(expected, SteamPlayHistory.Classify(now.AddDays(-daysAgo), now));
    }

    [Fact]
    public void Never_played_is_its_own_state() => Assert.Equal(PlayRecency.NeverRecorded, SteamPlayHistory.Classify(null, DateTimeOffset.UtcNow));
}

public sealed class RecycleBinGuardrailTests : IDisposable
{
    private readonly SnapshotHarness _h = new();
    private readonly TempDir _root = new();
    private readonly FakeRecycleBin _bin = new();
    private readonly DiskLockService _locks;
    private readonly DiskDeletionService _deletion;

    public RecycleBinGuardrailTests()
    {
        string R(string p) => Path.Combine(_root.Path, p);
        var guard = new PathGuard(R("Windows"), R("Program Files"), R("Program Files (x86)"), R("ProgramData"), R("Users"));
        _locks = new DiskLockService(new JsonDocumentStore<DiskLocksDocument>(_h.Paths.DiskLocksFile), _h.Audit);
        _deletion = new DiskDeletionService(guard, _locks, _bin, _h.Audit);
    }

    private string Make(string relative, int size = 10)
    {
        string path = Path.Combine(_root.Path, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        return Path.GetFullPath(path);
    }

    [Fact]
    public async Task Unconfirmed_requests_never_reach_the_recycle_bin()
    {
        string file = Make(@"Data/old.iso");

        DeletionResult r = await _deletion.RecycleAsync(new DeletionRequest(file, 10, Confirmed: false));

        Assert.False(r.Success);
        Assert.Empty(_bin.Files);
    }

    [Fact]
    public async Task Confirmed_files_go_to_the_recycle_bin_and_are_never_deleted_directly()
    {
        string file = Make(@"Data/old.iso", 1234);

        DeletionResult r = await _deletion.RecycleAsync(new DeletionRequest(file, 1234, true));

        Assert.True(r.Success);
        Assert.Equal(1234, r.BytesRecycled);
        Assert.Equal(new[] { file }, _bin.Files);
        Assert.True(File.Exists(file)); // only the (fake) Recycle Bin may remove it
        Assert.Contains(await _h.Audit.GetAllAsync(), e => e.Action == AuditAction.Cleaned && e.Success);
    }

    [Fact]
    public async Task Protected_and_locked_paths_are_refused_even_when_confirmed()
    {
        string system = Make(@"Windows/System32/kernel.dll");
        string keep = Make(@"Games/Keep/save.dat");
        await _locks.LockAsync(Path.Combine(_root.Path, "Games", "Keep"));

        DeletionResult a = await _deletion.RecycleAsync(new DeletionRequest(system, 10, true));
        DeletionResult b = await _deletion.RecycleAsync(new DeletionRequest(keep, 10, true));
        DeletionResult c = await _deletion.RecycleAsync(new DeletionRequest(Path.Combine(_root.Path, "Games"), 10, true));
        DeletionResult d = await _deletion.RecycleAsync(new DeletionRequest(Path.Combine(_root.Path, "Users", "me"), 10, true));

        Assert.All(new[] { a, b, c, d }, r => Assert.False(r.Success));
        Assert.Empty(_bin.Files);
        Assert.Empty(_bin.Directories);
    }

    [Fact]
    public async Task Unlocking_allows_deletion_again()
    {
        string folder = Path.Combine(_root.Path, "Games", "Keep");
        Make(@"Games/Keep/save.dat");
        await _locks.LockAsync(folder);

        await _locks.UnlockAsync(folder);
        DeletionResult r = await _deletion.RecycleAsync(new DeletionRequest(folder, 10, true));

        Assert.True(r.Success);
        Assert.Equal(new[] { folder }, _bin.Directories);
    }

    [Fact]
    public async Task Directory_symlinks_are_never_followed_or_recycled()
    {
        Make(@"Target/precious.dat");
        string link = Path.Combine(_root.Path, "Link");
        Directory.CreateSymbolicLink(link, Path.Combine(_root.Path, "Target"));

        DeletionResult r = await _deletion.RecycleAsync(new DeletionRequest(link, 10, true));

        Assert.False(r.Success);
        Assert.Empty(_bin.Directories);
    }

    [Fact]
    public async Task Recycling_contents_keeps_the_folder_and_skips_locked_children()
    {
        string a = Make(@"Cache/a.bin");
        string keep = Make(@"Cache/keep.bin");
        await _locks.LockAsync(keep);

        DeletionResult r = await _deletion.RecycleContentsAsync(new DeletionRequest(Path.Combine(_root.Path, "Cache"), 20, true));

        Assert.True(r.Success);
        Assert.Equal(new[] { a }, _bin.Files);
        Assert.Contains("1 skipped", r.Message);
    }

    [Fact]
    public async Task Missing_items_report_cleanly()
    {
        DeletionResult r = await _deletion.RecycleAsync(new DeletionRequest(Path.Combine(_root.Path, "nope.txt"), 0, true));

        Assert.False(r.Success);
        Assert.Contains("no longer exists", r.Message);
    }

    public void Dispose()
    {
        _h.Dispose();
        _root.Dispose();
    }
}

public sealed class DiskScanAndAdvisorTests : IDisposable
{
    private readonly SnapshotHarness _h = new();
    private readonly TempDir _root = new();
    private readonly FakeRecycleBin _bin = new();

    private string Make(string relative, long size = 10, int daysOld = 0)
    {
        string path = Path.Combine(_root.Path, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var fs = new FileStream(path, FileMode.Create))
        {
            fs.SetLength(size);
        }

        File.SetLastWriteTimeUtc(path, _h.Clock.GetUtcNow().UtcDateTime.AddDays(-daysOld));
        return Path.GetFullPath(path);
    }

    private DiskCategoryClassifier Classifier() => new(Path.Combine(_root.Path, "Windows"),
        new[] { Path.Combine(_root.Path, "Program Files") }, Path.Combine(_root.Path, "Users"));

    [Fact]
    public async Task Scan_totals_sizes_categories_large_files_and_skips_links()
    {
        Make("Windows/System32/a.dll", 1000);
        Make("Program Files/App/app.exe", 2000);
        Make("Library/steamapps/common/Game/game.pak", 5 * 1024 * 1024);
        Make("Users/me/Videos/clip.bin", 300);
        Make("Users/me/Desktop/movie.mkv", 400);
        Make("Misc/notes.txt", 50);
        Directory.CreateSymbolicLink(Path.Combine(_root.Path, "Misc", "loop"), _root.Path);
        var store = new JsonDocumentStore<DiskLocksDocument>(_h.Paths.DiskLocksFile);
        var analyzer = new DiskAnalyzerService(store, _ => Task.FromResult(Classifier()));
        var reports = new List<ScanProgress>();

        DriveScanResult result = await analyzer.ScanAsync(_root.Path, new Progress<ScanProgress>(reports.Add));

        long total = 1000 + 2000 + 5 * 1024 * 1024 + 300 + 400 + 50;
        Assert.Equal(total, result.Root.Size);
        Assert.Equal(6, result.Root.FileCount);
        Assert.Equal(1000, result.CategoryBytes[DiskCategory.System]);
        Assert.Equal(2000, result.CategoryBytes[DiskCategory.Apps]);
        Assert.Equal(5 * 1024 * 1024, result.CategoryBytes[DiskCategory.Games]);
        Assert.Equal(700, result.CategoryBytes[DiskCategory.Media]);
        Assert.Equal(50, result.CategoryBytes[DiskCategory.Other]);
        Assert.Equal("Library", result.Root.Children[0].Name); // size-sorted
        DiskNode gameFolder = DiskAnalyzerService.Find(result.Root, Path.Combine(_root.Path, "Library", "steamapps", "common", "Game"))!;
        Assert.Single(gameFolder.Children, c => !c.IsDirectory); // ≥ 4 MB file listed individually
        Assert.NotNull(analyzer.GetCached(_root.Path));
        Assert.Equal(total, (await analyzer.GetCachedSummaryAsync(_root.Path))!.ScannedBytes);
    }

    [Fact]
    public async Task Scan_honours_cancellation()
    {
        Make("a/b.txt");
        var analyzer = new DiskAnalyzerService(new JsonDocumentStore<DiskLocksDocument>(_h.Paths.DiskLocksFile), _ => Task.FromResult(Classifier()));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => analyzer.ScanAsync(_root.Path, null, cts.Token));
    }

    [Fact]
    public async Task Removing_a_deleted_item_updates_every_ancestor()
    {
        Make("x/y/big.bin", 6 * 1024 * 1024);
        Make("x/small.txt", 100);
        var analyzer = new DiskAnalyzerService(new JsonDocumentStore<DiskLocksDocument>(_h.Paths.DiskLocksFile), _ => Task.FromResult(Classifier()));
        DriveScanResult result = await analyzer.ScanAsync(_root.Path);

        Assert.True(analyzer.RemoveFromCache(Path.Combine(_root.Path, "x", "y")));

        Assert.Equal(100, result.Root.Size);
        Assert.Equal(100, DiskAnalyzerService.Find(result.Root, Path.Combine(_root.Path, "x"))!.Size);
    }

    [Fact]
    public async Task Advisor_groups_findings_and_respects_locks()
    {
        string downloads = Path.Combine(_root.Path, "Downloads");
        string bigOld = Make("Downloads/old-video.mkv", 210L * 1024 * 1024, daysOld: 45);
        Make("Downloads/new-big.zip", 300L * 1024 * 1024, daysOld: 3);
        string installer = Make("Downloads/setup.exe", 60L * 1024 * 1024, daysOld: 40);
        Make("Downloads/tiny.exe", 1024, daysOld: 400);
        string locked = Make("Downloads/Keep/old-backup.iso", 400L * 1024 * 1024, daysOld: 200);
        Make("Temp/junk.tmp", 4096);
        string steam = Path.Combine(_root.Path, "Steam");
        Make("Steam/steamapps/common/Dormant/game.exe", 10);
        Make("Steam/steamapps/common/Recent/game.exe", 10);
        File.WriteAllText(Path.Combine(steam, "steamapps", "appmanifest_1.acf"),
            "\"AppState\" { \"appid\" \"1\" \"name\" \"Dormant Game\" \"installdir\" \"Dormant\" \"SizeOnDisk\" \"60000000000\" }");
        File.WriteAllText(Path.Combine(steam, "steamapps", "appmanifest_2.acf"),
            "\"AppState\" { \"appid\" \"2\" \"name\" \"Recent Game\" \"installdir\" \"Recent\" \"SizeOnDisk\" \"80000000000\" }");
        long now = _h.Clock.GetUtcNow().ToUnixTimeSeconds();
        Directory.CreateDirectory(Path.Combine(steam, "userdata", "1", "config"));
        File.WriteAllText(Path.Combine(steam, "userdata", "1", "config", "localconfig.vdf"),
            $"\"UserLocalConfigStore\" {{ \"Software\" {{ \"Valve\" {{ \"Steam\" {{ \"apps\" {{ \"1\" {{ \"LastPlayed\" \"{now - 100 * 86400}\" }} \"2\" {{ \"LastPlayed\" \"{now - 5 * 86400}\" }} }} }} }} }} }}");
        _bin.Size = 5000;

        SpaceAdvisorService advisor = CreateAdvisor(downloads, steam, out DiskLockService locks);
        await locks.LockAsync(Path.Combine(downloads, "Keep"));
        IReadOnlyList<SpaceFinding> findings = await advisor.AnalyzeAsync();

        SpaceFinding Find(string path) => findings.Single(f => f.Path == path);
        Assert.Equal(AdvisorGroup.CheckFirst, Find(bigOld).Group);
        Assert.Equal(AdvisorAction.RecycleItem, Find(bigOld).Action);
        Assert.Equal(AdvisorGroup.CheckFirst, Find(installer).Group);
        Assert.DoesNotContain(findings, f => f.Path.EndsWith("new-big.zip") || f.Path.EndsWith("tiny.exe"));
        Assert.Equal(AdvisorGroup.Keep, Find(locked).Group);
        Assert.Equal(AdvisorAction.None, Find(locked).Action);

        SpaceFinding dormant = findings.Single(f => f.Title.StartsWith("Dormant Game"));
        Assert.Equal((AdvisorGroup.CheckFirst, AdvisorAction.UninstallSteamGame, "1"), (dormant.Group, dormant.Action, dormant.ActionArgument));
        Assert.Contains("90+ days", dormant.Title);
        Assert.Equal(AdvisorGroup.Keep, findings.Single(f => f.Title == "Recent Game").Group);

        Assert.Equal(AdvisorGroup.SafeToRemove, findings.Single(f => f.Title.StartsWith("Your temp folder")).Group);
        Assert.Equal(AdvisorAction.EmptyRecycleBin, findings.Single(f => f.Title.StartsWith("Recycle Bin")).Action);
        Assert.Equal(AdvisorGroup.SafeToRemove, findings.First().Group); // grouped: Safe → Check First → Keep
    }

    [Fact]
    public async Task Advisor_actions_require_confirmation_and_use_the_recycle_bin()
    {
        string old = Make("Downloads/old.iso", 210L * 1024 * 1024, daysOld: 60);
        SpaceAdvisorService advisor = CreateAdvisor(Path.Combine(_root.Path, "Downloads"), null, out _);
        SpaceFinding finding = (await advisor.AnalyzeAsync()).Single(f => f.Path == old);

        string refused = await advisor.ExecuteAsync(finding, confirmed: false);
        Assert.Empty(_bin.Files);
        Assert.Contains("Not confirmed", refused);

        await advisor.ExecuteAsync(finding, confirmed: true);
        Assert.Equal(new[] { old }, _bin.Files);
    }

    private SpaceAdvisorService CreateAdvisor(string downloads, string? steam, out DiskLockService locks)
    {
        string R(string p) => Path.Combine(_root.Path, p);
        var guard = new PathGuard(R("Windows"), R("Program Files"), null, R("ProgramData"), R("Users"));
        locks = new DiskLockService(new JsonDocumentStore<DiskLocksDocument>(_h.Paths.DiskLocksFile), _h.Audit);
        var deletion = new DiskDeletionService(guard, locks, _bin, _h.Audit);
        var env = new AdvisorEnvironment(R("Local"), R("Temp"), R("Windows"), downloads, _root.Path, steam);
        return new SpaceAdvisorService(env, locks, deletion, _bin, _h.Audit, time: _h.Clock);
    }

    public void Dispose()
    {
        _h.Dispose();
        _root.Dispose();
    }
}

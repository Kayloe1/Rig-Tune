using RigTune.Core.Models;
using RigTune.Core.Services.DiskAnalyzer;
using RigTune.Core.Services.Uninstaller;
using RigTune.Core.Storage;
using RigTune.Core.Tests.Fakes;

namespace RigTune.Core.Tests;

internal static class UninstallFixtures
{
    public const string Root = InstalledProgramReader.UninstallRoot;

    public static void AddEntry(InMemoryRegistry reg, string keyName, RegistryHiveName hive = RegistryHiveName.HKLM,
        RegistryViewName view = RegistryViewName.Registry64, params (string Name, object Value)[] values)
    {
        foreach ((string name, object value) in values)
        {
            RegistryValueData data = value switch
            {
                uint u => RegistryValueData.FromDWord(u),
                int i => RegistryValueData.FromDWord((uint)i),
                _ => RegistryValueData.FromString(value.ToString()!),
            };
            reg.WriteValue(new RegistryTarget(hive, view, Root + "\\" + keyName, name), data);
        }
    }

    public static InstalledProgram Program(string name, string? publisher = null, string? location = null, string? uninstall = null,
        string keyName = "Prog", RegistryHiveName hive = RegistryHiveName.HKLM, RegistryViewName view = RegistryViewName.Registry64) => new()
    {
        Hive = hive,
        View = view,
        KeyName = keyName,
        KeyPath = Root + "\\" + keyName,
        DisplayName = name,
        Publisher = publisher,
        InstallLocation = location,
        UninstallString = uninstall ?? @"C:\Fake\uninst.exe",
    };
}

public sealed class InstalledProgramReaderTests
{
    [Fact]
    public void Lists_programs_from_all_three_roots_and_hides_system_components_updates_and_broken_entries()
    {
        var reg = new InMemoryRegistry();
        UninstallFixtures.AddEntry(reg, "Discord", RegistryHiveName.HKCU, RegistryViewName.Default,
            ("DisplayName", "Discord"), ("Publisher", "Discord Inc."), ("UninstallString", @"""C:\Users\me\AppData\Local\Discord\Update.exe"" --uninstall"));
        UninstallFixtures.AddEntry(reg, "7-Zip", values: new (string, object)[]
        {
            ("DisplayName", "7-Zip 23.01 (x64)"), ("DisplayVersion", "23.01"), ("UninstallString", @"""C:\Program Files\7-Zip\Uninstall.exe"""),
            ("EstimatedSize", 5600), ("InstallDate", "20240115"), ("InstallLocation", @"C:\Program Files\7-Zip\"),
        });
        UninstallFixtures.AddEntry(reg, "{11111111-2222-3333-4444-555555555555}", RegistryHiveName.HKLM, RegistryViewName.Registry32,
            ("DisplayName", "Old Tool"), ("WindowsInstaller", 1), ("UninstallString", "MsiExec.exe /I{11111111-2222-3333-4444-555555555555}"));
        UninstallFixtures.AddEntry(reg, "Hidden", values: new (string, object)[] { ("DisplayName", "Hidden"), ("SystemComponent", 1), ("UninstallString", "x.exe") });
        UninstallFixtures.AddEntry(reg, "KB123", values: new (string, object)[] { ("DisplayName", "Update for X"), ("ParentKeyName", "X"), ("UninstallString", "x.exe") });
        UninstallFixtures.AddEntry(reg, "Hotfix", values: new (string, object)[] { ("DisplayName", "Hotfix"), ("ReleaseType", "Hotfix"), ("UninstallString", "x.exe") });
        UninstallFixtures.AddEntry(reg, "NoName", values: new (string, object)[] { ("UninstallString", "x.exe") });
        UninstallFixtures.AddEntry(reg, "NoCommand", values: new (string, object)[] { ("DisplayName", "No command") });
        UninstallFixtures.AddEntry(reg, "Game_is1", values: new (string, object)[]
        {
            ("DisplayName", "Some Game"), ("UninstallString", @"""C:\Games\Some Game\unins000.exe"""),
        });

        IReadOnlyList<InstalledProgram> list = new InstalledProgramReader(reg).ReadAll();

        Assert.Equal(new[] { "7-Zip 23.01 (x64)", "Discord", "Old Tool", "Some Game" }, list.Select(p => p.DisplayName));
        InstalledProgram zip = list[0];
        Assert.Equal(5600L * 1024, zip.EstimatedSizeBytes);
        Assert.Equal(new DateOnly(2024, 1, 15), zip.InstallDate);
        Assert.Equal(@"C:\Program Files\7-Zip\", zip.InstallLocation);
        Assert.True(list[1].IsPerUser);
        Assert.Equal("{11111111-2222-3333-4444-555555555555}", list[2].ProductCode);
        Assert.Equal("32-bit", list[2].ScopeLabel);
        Assert.True(list[3].IsInnoSetup);
    }
}

public sealed class UninstallCommandBuilderTests
{
    private static readonly Func<string, bool> NoFiles = _ => false;

    [Fact]
    public void Msi_entries_always_use_msiexec_uninstall_never_the_repair_switch()
    {
        InstalledProgram p = UninstallFixtures.Program("Tool", uninstall: "MsiExec.exe /I{AAAAAAAA-2222-3333-4444-555555555555}") with
        {
            ProductCode = "{AAAAAAAA-2222-3333-4444-555555555555}",
        };

        UninstallCommand quiet = UninstallCommandBuilder.Build(p, true, NoFiles)!;
        UninstallCommand normal = UninstallCommandBuilder.Build(p, false, NoFiles)!;

        Assert.Equal("msiexec.exe", quiet.FileName);
        Assert.Equal("/x {AAAAAAAA-2222-3333-4444-555555555555} /qn /norestart", quiet.Arguments);
        Assert.True(quiet.IsQuiet);
        Assert.Equal("/x {AAAAAAAA-2222-3333-4444-555555555555}", normal.Arguments);
        Assert.False(normal.IsQuiet);
    }

    [Fact]
    public void Splits_quoted_and_unquoted_paths_with_spaces()
    {
        Assert.Equal((@"C:\Program Files\Foo\uninst.exe", "/x /y"),
            UninstallCommandBuilder.Split(@"""C:\Program Files\Foo\uninst.exe"" /x /y", NoFiles));

        Func<string, bool> exists = f => f == @"C:\Program Files\Bar App\un ins.exe";
        Assert.Equal((@"C:\Program Files\Bar App\un ins.exe", "--remove"),
            UninstallCommandBuilder.Split(@"C:\Program Files\Bar App\un ins.exe --remove", exists));
    }

    [Fact]
    public void Silent_mode_only_for_vendor_quiet_commands_and_inno_setup()
    {
        InstalledProgram withQuiet = UninstallFixtures.Program("A", uninstall: @"""C:\A\u.exe""") with { QuietUninstallString = @"""C:\A\u.exe"" /S" };
        InstalledProgram inno = UninstallFixtures.Program("B", uninstall: @"""C:\B\unins000.exe""") with { IsInnoSetup = true };
        InstalledProgram nsis = UninstallFixtures.Program("C", uninstall: @"""C:\C\uninst.exe""");

        Assert.Equal("/S", UninstallCommandBuilder.Build(withQuiet, true, NoFiles)!.Arguments);
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART", UninstallCommandBuilder.Build(inno, true, NoFiles)!.Arguments);
        UninstallCommand plain = UninstallCommandBuilder.Build(nsis, true, NoFiles)!;
        Assert.False(plain.IsQuiet);
        Assert.Equal(string.Empty, plain.Arguments);
        Assert.False(UninstallCommandBuilder.SupportsQuiet(nsis));
    }

    [Theory]
    [InlineData("Microsoft Visual C++ 2015-2022 Redistributable (x64)", true)]
    [InlineData("NVIDIA Graphics Driver 566.36", true)]
    [InlineData("Steam", true)]
    [InlineData("7-Zip 23.01 (x64)", false)]
    public void Shared_runtimes_and_launchers_get_a_warning(string name, bool warns) =>
        Assert.Equal(warns, UninstallCautions.Warning(UninstallFixtures.Program(name)) is not null);
}

/// <summary>Fake uninstaller: runs a callback (e.g. removes the registry entry) and returns an exit code.</summary>
internal sealed class FakeUninstallRunner : IUninstallProcessRunner
{
    public List<UninstallCommand> Commands { get; } = new();
    public Func<UninstallCommand, int> Behavior { get; set; } = _ => 0;
    public Action? OnRun { get; set; }

    public Task<int> RunAsync(UninstallCommand command, Func<bool> isComplete, CancellationToken cancellationToken)
    {
        Commands.Add(command);
        OnRun?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Behavior(command));
    }
}

internal sealed class FakeKeyRemover : IRegistryKeyRemover
{
    private readonly InMemoryRegistry _reg;

    public FakeKeyRemover(InMemoryRegistry reg) => _reg = reg;

    public bool FailExport { get; set; }
    public List<string> Log { get; } = new();

    public void Export(RegistryHiveName hive, RegistryViewName view, string keyPath, string file)
    {
        if (FailExport)
        {
            throw new InvalidOperationException("reg export failed");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "Windows Registry Editor Version 5.00");
        Log.Add("export " + keyPath);
    }

    public void DeleteTree(RegistryHiveName hive, RegistryViewName view, string keyPath)
    {
        Log.Add("delete " + keyPath);
        _reg.DeleteKeyTree(hive, view, keyPath);
    }
}

public sealed class UninstallerServiceTests : IDisposable
{
    private readonly SnapshotHarness _h = new();
    private readonly TempDir _root = new();
    private readonly InMemoryRegistry _reg = new();
    private readonly FakeUninstallRunner _runner = new();
    private readonly FakeRecycleBin _bin = new();
    private readonly FakeKeyRemover _keys;
    private readonly DiskLockService _locks;
    private readonly UninstallerService _service;

    public UninstallerServiceTests()
    {
        _keys = new FakeKeyRemover(_reg);
        string R(string p) => Path.Combine(_root.Path, p);
        var guard = new PathGuard(R("Windows"), R("Program Files"), R("Program Files (x86)"), R("ProgramData"), R("Users"));
        var roots = new LeftoverRoots(new[] { R("Program Files"), R("ProgramData"), R(@"Users\me\AppData\Roaming") }, new[] { R("Desktop") });
        _locks = new DiskLockService(new JsonDocumentStore<DiskLocksDocument>(_h.Paths.DiskLocksFile), _h.Audit);
        _service = new UninstallerService(new InstalledProgramReader(_reg), new LeftoverScanner(_reg, guard, roots), _runner, _keys, _bin,
            guard, _locks, _h.Audit, _h.Paths, fileExists: f => !f.Contains("Missing", StringComparison.Ordinal),
            entryRemovalGrace: TimeSpan.Zero);
    }

    private InstalledProgram Add(string key, string name, string uninstall = @"""C:\Apps\uninst.exe""")
    {
        UninstallFixtures.AddEntry(_reg, key, values: new (string, object)[] { ("DisplayName", name), ("UninstallString", uninstall) });
        return new InstalledProgramReader(_reg).ReadAll().Single(p => p.KeyName == key);
    }

    private void RemoveEntry(string key) =>
        _reg.DeleteKeyTree(RegistryHiveName.HKLM, RegistryViewName.Registry64, UninstallFixtures.Root + "\\" + key);

    [Fact]
    public async Task Batch_judges_success_by_the_entry_disappearing_not_the_exit_code()
    {
        InstalledProgram a = Add("A", "Alpha", @"""C:\Apps\A\uninst.exe""");
        InstalledProgram b = Add("B", "Bravo", @"""C:\Apps\B\uninst.exe""");
        InstalledProgram c = Add("C", "Charlie", @"""C:\Missing\uninst.exe""");
        _runner.Behavior = cmd =>
        {
            if (cmd.FileName.Contains(@"\A\", StringComparison.Ordinal))
            {
                RemoveEntry("A");
            }

            return 0; // B "succeeds" but the user cancelled inside it: entry still there
        };

        IReadOnlyList<UninstallResult> results = await _service.UninstallBatchAsync(new[] { a, b, c }, preferQuiet: true);

        Assert.Equal(new[] { UninstallOutcome.Removed, UninstallOutcome.StillInstalled, UninstallOutcome.UninstallerMissing },
            results.Select(r => r.Outcome));
        Assert.Equal(2, _runner.Commands.Count); // the missing uninstaller is never started
        Assert.True(results[0].CanScanLeftovers);
        Assert.False(results[1].CanScanLeftovers);
        Assert.True(results[2].CanScanLeftovers);
        IReadOnlyList<AuditEntry> audit = await _h.Audit.GetAllAsync();
        Assert.Equal(3, audit.Count(e => e.Action == AuditAction.Uninstalled));
    }

    [Fact]
    public async Task Exit_codes_map_to_cancelled_and_restart_required()
    {
        InstalledProgram a = Add("A", "Alpha");
        InstalledProgram b = Add("B", "Bravo");
        _runner.Behavior = _ => 1602;
        UninstallResult cancelled = await _service.UninstallAsync(a, false);
        _runner.Behavior = _ =>
        {
            RemoveEntry("B");
            return 3010;
        };
        UninstallResult restart = await _service.UninstallAsync(b, false);

        Assert.Equal(UninstallOutcome.Cancelled, cancelled.Outcome);
        Assert.Equal(UninstallOutcome.Removed, restart.Outcome);
        Assert.True(restart.RestartRequired);
    }

    [Fact]
    public async Task Stopping_a_batch_skips_the_remaining_programs()
    {
        InstalledProgram a = Add("A", "Alpha");
        InstalledProgram b = Add("B", "Bravo");
        using var cts = new CancellationTokenSource();
        _runner.OnRun = cts.Cancel;

        IReadOnlyList<UninstallResult> results = await _service.UninstallBatchAsync(new[] { a, b }, false, cancellationToken: cts.Token);

        Assert.Single(_runner.Commands);
        Assert.All(results, r => Assert.Equal(UninstallOutcome.Cancelled, r.Outcome));
    }

    [Fact]
    public async Task Leftovers_are_never_scanned_for_programs_that_are_still_installed()
    {
        InstalledProgram a = Add("A", "Alpha");
        Directory.CreateDirectory(Path.Combine(_root.Path, "ProgramData", "Alpha"));

        IReadOnlyList<Leftover> leftovers = await _service.ScanLeftoversAsync(new[]
        {
            new UninstallResult(a, UninstallOutcome.StillInstalled, 0, "still there"),
        });

        Assert.Empty(leftovers);
    }

    [Fact]
    public async Task Removal_recycles_files_backs_up_keys_and_respects_locks()
    {
        string folder = Directory.CreateDirectory(Path.Combine(_root.Path, "ProgramData", "Alpha")).FullName;
        string locked = Directory.CreateDirectory(Path.Combine(_root.Path, "ProgramData", "Bravo")).FullName;
        await _locks.LockAsync(locked);
        _reg.WriteValue(new RegistryTarget(RegistryHiveName.HKCU, RegistryViewName.Default, @"Software\Alpha", "x"), RegistryValueData.FromDWord(1));

        Leftover Dir(string p) => new() { ProgramName = "Alpha", Kind = LeftoverKind.Folder, Path = p, Reason = "r", Confidence = LeftoverConfidence.High };
        Leftover RegKey(string k) => new()
        {
            ProgramName = "Alpha", Kind = LeftoverKind.RegistryKey, Path = k, Reason = "r", Confidence = LeftoverConfidence.High,
            Hive = RegistryHiveName.HKCU, View = RegistryViewName.Default, KeyPath = k,
        };

        IReadOnlyList<LeftoverRemovalResult> results = await _service.RemoveLeftoversAsync(new[]
        {
            Dir(folder), Dir(locked), RegKey(@"Software\Alpha"), RegKey(@"Software\Microsoft\Windows"), RegKey("Software"),
        });

        Assert.Equal(new[] { folder }, _bin.Directories);
        Assert.False(results.Single(r => r.Leftover.Path == locked).Success);
        LeftoverRemovalResult key = results.Single(r => r.Leftover.KeyPath == @"Software\Alpha");
        Assert.True(key.Success);
        Assert.True(File.Exists(key.BackupFile));
        Assert.Equal(new[] { @"export Software\Alpha", @"delete Software\Alpha" }, _keys.Log);
        Assert.False(results.Single(r => r.Leftover.KeyPath == @"Software\Microsoft\Windows").Success);
        Assert.False(results.Single(r => r.Leftover.KeyPath == "Software").Success);
        Assert.False(_reg.KeyExists(RegistryHiveName.HKCU, RegistryViewName.Default, @"Software\Alpha"));
    }

    [Fact]
    public async Task A_failed_backup_leaves_the_registry_key_alone()
    {
        _reg.WriteValue(new RegistryTarget(RegistryHiveName.HKCU, RegistryViewName.Default, @"Software\Alpha", "x"), RegistryValueData.FromDWord(1));
        _keys.FailExport = true;

        IReadOnlyList<LeftoverRemovalResult> results = await _service.RemoveLeftoversAsync(new[]
        {
            new Leftover
            {
                ProgramName = "Alpha", Kind = LeftoverKind.RegistryKey, Path = "k", Reason = "r", Confidence = LeftoverConfidence.High,
                Hive = RegistryHiveName.HKCU, View = RegistryViewName.Default, KeyPath = @"Software\Alpha",
            },
        });

        Assert.False(results[0].Success);
        Assert.Empty(_keys.Log);
        Assert.True(_reg.KeyExists(RegistryHiveName.HKCU, RegistryViewName.Default, @"Software\Alpha"));
    }

    public void Dispose()
    {
        _h.Dispose();
        _root.Dispose();
    }
}

public sealed class LeftoverScannerTests : IDisposable
{
    private readonly TempDir _root = new();
    private readonly InMemoryRegistry _reg = new();
    private readonly LeftoverScanner _scanner;
    private readonly string _pf;
    private readonly string _pd;
    private readonly string _roaming;
    private readonly string _desktop;

    public LeftoverScannerTests()
    {
        string R(string p) => Path.GetFullPath(Path.Combine(_root.Path, p));
        _pf = R("Program Files");
        _pd = R("ProgramData");
        _roaming = R(@"Users\me\AppData\Roaming");
        _desktop = R(@"Users\me\Desktop");
        foreach (string d in new[] { _pf, _pd, _roaming, _desktop })
        {
            Directory.CreateDirectory(d);
        }

        var guard = new PathGuard(R("Windows"), _pf, R("Program Files (x86)"), _pd, R("Users"));
        _scanner = new LeftoverScanner(_reg, guard, new LeftoverRoots(new[] { _pf, _pd, _roaming }, new[] { _desktop }));
    }

    private string Dir(params string[] parts)
    {
        string path = Path.Combine(parts);
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "data.bin"), new byte[100]);
        return Path.GetFullPath(path);
    }

    private void Key(RegistryHiveName hive, RegistryViewName view, string path) =>
        _reg.WriteValue(new RegistryTarget(hive, view, path, "v"), RegistryValueData.FromDWord(1));

    [Fact]
    public void Finds_install_folder_same_name_folders_shortcuts_and_settings_keys()
    {
        string install = Dir(_pf, "Fabrikam", "Photo Studio");
        string data = Dir(_pd, "Photo Studio");
        string appdata = Dir(_roaming, "Fabrikam", "Photo Studio");
        string shortcut = Path.Combine(_desktop, "Photo Studio.lnk");
        File.WriteAllText(shortcut, "lnk");
        Key(RegistryHiveName.HKCU, RegistryViewName.Default, @"Software\Fabrikam\Photo Studio\Prefs");
        Key(RegistryHiveName.HKLM, RegistryViewName.Registry32, @"SOFTWARE\Photo Studio");
        InstalledProgram removed = UninstallFixtures.Program("Photo Studio 5.2 (x64)", "Fabrikam, Inc.", install + "\\");

        IReadOnlyList<Leftover> found = _scanner.Scan(removed, Array.Empty<InstalledProgram>());

        Assert.Contains(found, l => l.Path == PathGuard.Normalize(install) && l.Confidence == LeftoverConfidence.High && l.SizeBytes == 100);
        Assert.Contains(found, l => l.Path == PathGuard.Normalize(data) && l.DefaultSelected);
        Assert.Contains(found, l => l.Path == PathGuard.Normalize(shortcut) && l.Kind == LeftoverKind.File);
        Assert.Contains(found, l => l.KeyPath == @"SOFTWARE\Photo Studio" && l.View == RegistryViewName.Registry32);

        // Fabrikam is used by nothing else, so the publisher folders/keys are offered — but never pre-selected,
        // and the Fabrikam\Photo Studio children are still listed because the parent is only a suggestion.
        Assert.Contains(found, l => l.Path == PathGuard.Normalize(Path.Combine(_pf, "Fabrikam")) && !l.DefaultSelected);
        Assert.Contains(found, l => l.Path == PathGuard.Normalize(appdata) && l.DefaultSelected);
        Assert.Contains(found, l => l.KeyPath == @"Software\Fabrikam" && l.Confidence == LeftoverConfidence.Medium);
        Assert.Contains(found, l => l.KeyPath == @"Software\Fabrikam\Photo Studio" && l.Confidence == LeftoverConfidence.High);
    }

    [Fact]
    public void Never_offers_anything_still_used_by_installed_programs_or_partial_name_matches()
    {
        Dir(_pf, "Fabrikam", "Photo Studio");
        string other = Dir(_pf, "Fabrikam", "Video Studio");
        Dir(_pd, "Photo Studio Pro");      // longer name: not a match
        Dir(_pd, "Photo");                 // shorter name: not a match
        Dir(_pf, "Common Files", "Photo Studio");
        Key(RegistryHiveName.HKCU, RegistryViewName.Default, @"Software\Fabrikam\Video Studio");
        Key(RegistryHiveName.HKCU, RegistryViewName.Default, @"Software\Microsoft\Photo Studio");
        InstalledProgram removed = UninstallFixtures.Program("Photo Studio", "Fabrikam", keyName: "PS");
        InstalledProgram stillInstalled = UninstallFixtures.Program("Video Studio", "Fabrikam", other, keyName: "VS");

        IReadOnlyList<Leftover> found = _scanner.Scan(removed, new[] { stillInstalled });

        Assert.DoesNotContain(found, l => l.Path.Contains("Video Studio", StringComparison.Ordinal));
        Assert.DoesNotContain(found, l => l.Path.EndsWith(@"\Fabrikam", StringComparison.Ordinal) || l.KeyPath == @"Software\Fabrikam");
        Assert.DoesNotContain(found, l => l.Path.Contains("Photo Studio Pro", StringComparison.Ordinal) || l.Path.EndsWith(@"\Photo", StringComparison.Ordinal));
        Assert.DoesNotContain(found, l => l.Path.Contains("Common Files", StringComparison.Ordinal));
        Assert.DoesNotContain(found, l => l.KeyPath is not null && l.KeyPath.Contains("Microsoft", StringComparison.Ordinal));
        Assert.Contains(found, l => l.Path == PathGuard.Normalize(Path.Combine(_pf, "Fabrikam", "Photo Studio")));
        Assert.All(found, l => Assert.Equal(LeftoverConfidence.High, l.Confidence));
    }

    [Fact]
    public void Never_offers_roots_or_folders_containing_another_programs_install_folder()
    {
        string shared = Dir(_pf, "Suite");
        Dir(shared, "Editor");
        InstalledProgram removed = UninstallFixtures.Program("Suite", location: shared, keyName: "Suite");
        InstalledProgram editor = UninstallFixtures.Program("Editor", location: Path.Combine(shared, "Editor"), keyName: "Ed");
        InstalledProgram badLocation = UninstallFixtures.Program("Bad", location: _pf, keyName: "Bad");

        Assert.Empty(_scanner.Scan(removed, new[] { editor }));
        Assert.DoesNotContain(_scanner.Scan(badLocation, Array.Empty<InstalledProgram>()), l => l.Path == PathGuard.Normalize(_pf));
    }

    [Theory]
    [InlineData("7-Zip 23.01 (x64)", "7zip")]
    [InlineData("7-Zip", "7zip")]
    [InlineData("Notepad++ (64-bit x64)", "notepad")]
    [InlineData("Blender 4.2", "blender")]
    [InlineData("VLC media player", "vlcmediaplayer")]
    public void Normalizes_names(string input, string expected) => Assert.Equal(expected, LeftoverScanner.Normalize(input));

    [Theory]
    [InlineData("Valve Corporation", "valve")]
    [InlineData("Discord Inc.", "discord")]
    [InlineData("Fabrikam, Inc.", "fabrikam")]
    public void Normalizes_publishers(string input, string expected) => Assert.Equal(expected, LeftoverScanner.NormalizePublisher(input));

    public void Dispose() => _root.Dispose();
}

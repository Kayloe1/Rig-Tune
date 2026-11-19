using System.Text.Json;
using RigTune.Core.Models;
using RigTune.Core.Services;
using RigTune.Core.Storage;

namespace RigTune.Core.Tests;

public sealed class AtomicJsonStorageTests : IDisposable
{
    private readonly TempDir _dir = new();

    private sealed class Doc
    {
        public int Version { get; set; }
        public List<string> Items { get; set; } = new();
    }

    [Fact]
    public async Task Write_then_read_round_trips()
    {
        string path = Path.Combine(_dir.Path, "doc.json");
        await AtomicJsonFile.WriteAsync(path, new Doc { Version = 3, Items = { "a", "b" } });

        Doc read = await AtomicJsonFile.ReadOrDefaultAsync(path, () => new Doc());

        Assert.Equal(3, read.Version);
        Assert.Equal(new[] { "a", "b" }, read.Items);
    }

    [Fact]
    public async Task Missing_file_returns_default_without_creating_it()
    {
        string path = Path.Combine(_dir.Path, "missing.json");

        Doc read = await AtomicJsonFile.ReadOrDefaultAsync(path, () => new Doc { Version = 42 });

        Assert.Equal(42, read.Version);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Overwrite_keeps_previous_version_as_backup_and_leaves_no_temp_files()
    {
        string path = Path.Combine(_dir.Path, "doc.json");
        await AtomicJsonFile.WriteAsync(path, new Doc { Version = 1 });
        await AtomicJsonFile.WriteAsync(path, new Doc { Version = 2 });

        Doc current = await AtomicJsonFile.ReadOrDefaultAsync(path, () => new Doc());
        Doc backup = JsonSerializer.Deserialize<Doc>(await File.ReadAllTextAsync(AtomicJsonFile.BackupPathFor(path)),
            AtomicJsonFile.DefaultOptions)!;

        Assert.Equal(2, current.Version);
        Assert.Equal(1, backup.Version);
        Assert.Empty(Directory.GetFiles(_dir.Path, "*.tmp"));
    }

    [Fact]
    public async Task Corrupt_primary_falls_back_to_backup_and_is_quarantined_not_deleted()
    {
        string path = Path.Combine(_dir.Path, "doc.json");
        await AtomicJsonFile.WriteAsync(path, new Doc { Version = 1 });
        await AtomicJsonFile.WriteAsync(path, new Doc { Version = 2 });
        await File.WriteAllTextAsync(path, "{ \"version\": 2, \"items\": [ TRUNCATED");

        Doc read = await AtomicJsonFile.ReadOrDefaultAsync(path, () => new Doc { Version = -1 });

        Assert.Equal(1, read.Version);
        Assert.Single(Directory.GetFiles(_dir.Path, "doc.json.corrupt-*"));
    }

    [Fact]
    public async Task Concurrent_writes_always_leave_a_complete_valid_document()
    {
        string path = Path.Combine(_dir.Path, "doc.json");

        await Task.WhenAll(Enumerable.Range(0, 40).Select(i =>
            AtomicJsonFile.WriteAsync(path, new Doc { Version = i, Items = Enumerable.Repeat($"item{i}", 200).ToList() })));

        Doc read = JsonSerializer.Deserialize<Doc>(await File.ReadAllTextAsync(path), AtomicJsonFile.DefaultOptions)!;
        Assert.InRange(read.Version, 0, 39);
        Assert.All(read.Items, item => Assert.Equal($"item{read.Version}", item));
        Assert.Empty(Directory.GetFiles(_dir.Path, "*.tmp"));
    }

    [Fact]
    public async Task Document_store_persists_updates_and_returns_isolated_copies()
    {
        string path = Path.Combine(_dir.Path, "settings.json");
        var store = new JsonDocumentStore<AppSettings>(path);

        await store.UpdateAsync(s =>
        {
            s.DangerZoneEnabled = true;
            s.RefreshRateTargets[@"\\.\DISPLAY1"] = 165;
        });

        AppSettings copy = await store.ReadAsync();
        copy.DangerZoneEnabled = false; // must not leak into the store

        AppSettings reloaded = await new JsonDocumentStore<AppSettings>(path).ReadAsync();
        Assert.True((await store.ReadAsync()).DangerZoneEnabled);
        Assert.True(reloaded.DangerZoneEnabled);
        Assert.Equal(165, reloaded.RefreshRateTargets[@"\\.\display1"]);
    }

    [Fact]
    public async Task Default_settings_keep_danger_zone_off()
    {
        var settings = new SettingsService(new RigTunePaths(_dir.Path));

        AppSettings s = await settings.GetAsync();

        Assert.False(s.DangerZoneEnabled);
    }

    [Fact]
    public async Task Paths_resolve_the_three_core_files_under_the_root()
    {
        var paths = new RigTunePaths(_dir.Path);
        var settings = new SettingsService(paths);
        var audit = new AuditLogService(paths);

        await settings.UpdateAsync(s => s.MinimizeToTrayOnClose = false);
        await audit.LogAsync(AuditAction.Info, "hello");

        Assert.True(File.Exists(Path.Combine(_dir.Path, "settings.json")));
        Assert.True(File.Exists(Path.Combine(_dir.Path, "history.json")));
        Assert.Equal(Path.Combine(_dir.Path, "snapshots.json"), paths.SnapshotsFile);
    }

    public void Dispose() => _dir.Dispose();
}

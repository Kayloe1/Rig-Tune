using Microsoft.Win32;
using RigTune.Core.Models;
using RigTune.Core.Services;
using RigTune.Core.Storage;
using RigTune.Core.Tests.Fakes;

namespace RigTune.Core.Tests;

/// <summary>Round-trip behaviour of the snapshot engine against in-memory platform fakes (runs on any OS).</summary>
public sealed class SnapshotServiceTests : IDisposable
{
    private const string TestRoot = @"Software\RigTuneTest";
    private readonly SnapshotHarness _h = new();

    private InMemoryRegistry Reg => (InMemoryRegistry)_h.Registry;

    private static RegistryTarget Target(string value, string subKey = "Values") =>
        new(RegistryHiveName.HKCU, RegistryViewName.Registry64, $@"{TestRoot}\{subKey}", value);

    public static TheoryData<RegistryValueData> AllKinds() => new()
    {
        RegistryValueData.FromDWord(0xFFFFFFFE),
        RegistryValueData.FromQWord(0x0123456789ABCDEF),
        RegistryValueData.FromString("hello world"),
        RegistryValueData.FromExpandString(@"%SystemRoot%\system32"),
        RegistryValueData.FromMultiString(new[] { "one", "", "three" }),
        RegistryValueData.FromBinary(new byte[] { 0x02, 0x00, 0x00, 0x00, 0xFF, 0x10 }),
    };

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Revert_restores_exact_kind_and_value_of_preexisting_value(RegistryValueData original)
    {
        RegistryTarget t = Target("Existing");
        Reg.WriteValue(t, original);

        RegistrySnapshotEntry snap = await _h.Snapshots.ApplyRegistryAsync("test.tweak", t, RegistryValueData.FromDWord(1));
        Assert.Equal(RegistryValueData.FromDWord(1), Reg.ReadValue(t));
        Assert.True(snap.Existed);

        RevertResult result = await _h.Snapshots.RevertAsync(snap.Id);

        Assert.True(result.Success);
        RegistryValueData? restored = Reg.ReadValue(t);
        Assert.Equal(original, restored);
        Assert.Equal(original.Kind, restored!.Kind);
    }

    [Fact]
    public async Task Revert_deletes_value_that_did_not_exist_and_removes_created_keys()
    {
        RegistryTarget t = new(RegistryHiveName.HKCU, RegistryViewName.Registry64, $@"{TestRoot}\New\Deep\Key", "Fresh");
        Reg.WriteValue(new RegistryTarget(t.Hive, t.View, TestRoot, "Anchor"), RegistryValueData.FromDWord(7));

        RegistrySnapshotEntry snap = await _h.Snapshots.ApplyRegistryAsync("test.tweak", t, RegistryValueData.FromString("x"));
        Assert.False(snap.Existed);
        Assert.Null(snap.Original);
        Assert.Equal($@"{TestRoot}\New", snap.FirstCreatedKeyPath);

        await _h.Snapshots.RevertAsync(snap.Id);

        Assert.Null(Reg.ReadValue(t));
        Assert.False(Reg.KeyExists(t.Hive, t.View, $@"{TestRoot}\New"));
        Assert.True(Reg.KeyExists(t.Hive, t.View, TestRoot)); // pre-existing parent untouched
    }

    [Fact]
    public async Task Revert_of_missing_value_keeps_key_that_still_has_other_values()
    {
        RegistryTarget a = Target("A", "Shared");
        RegistryTarget b = Target("B", "Shared");

        RegistrySnapshotEntry snapA = await _h.Snapshots.ApplyRegistryAsync("t.a", a, RegistryValueData.FromDWord(1));
        await _h.Snapshots.ApplyRegistryAsync("t.b", b, RegistryValueData.FromDWord(2));

        await _h.Snapshots.RevertAsync(snapA.Id);

        Assert.Null(Reg.ReadValue(a));
        Assert.Equal(RegistryValueData.FromDWord(2), Reg.ReadValue(b));
    }

    [Fact]
    public async Task Reapplying_never_overwrites_the_true_original()
    {
        RegistryTarget t = Target("MenuShowDelay");
        Reg.WriteValue(t, RegistryValueData.FromString("400"));

        RegistrySnapshotEntry first = await _h.Snapshots.ApplyRegistryAsync("t", t, RegistryValueData.FromString("0"));
        RegistrySnapshotEntry second = await _h.Snapshots.ApplyRegistryAsync("t", t, RegistryValueData.FromString("50"));

        Assert.Equal(first.Id, second.Id);
        Assert.Single(await _h.Snapshots.GetActiveAsync());
        await _h.Snapshots.RevertAsync(first.Id);
        Assert.Equal(RegistryValueData.FromString("400"), Reg.ReadValue(t));
    }

    [Fact]
    public async Task Apply_can_delete_a_value_and_revert_recreates_it_exactly()
    {
        RegistryTarget t = Target("ToDelete");
        Reg.WriteValue(t, RegistryValueData.FromBinary(new byte[] { 3, 0, 0, 0 }));

        RegistrySnapshotEntry snap = await _h.Snapshots.ApplyRegistryAsync("t", t, value: null);
        Assert.Null(Reg.ReadValue(t));

        await _h.Snapshots.RevertAsync(snap.Id);
        Assert.Equal(RegistryValueData.FromBinary(new byte[] { 3, 0, 0, 0 }), Reg.ReadValue(t));
    }

    [Fact]
    public async Task Snapshots_survive_restart_and_can_be_reverted_by_a_new_instance()
    {
        RegistryTarget t = Target("Persisted");
        Reg.WriteValue(t, RegistryValueData.FromDWord(8));
        RegistrySnapshotEntry snap = await _h.Snapshots.ApplyRegistryAsync("t", t, RegistryValueData.FromDWord(1));

        SnapshotService restarted = _h.CreateSnapshotService();
        IReadOnlyList<SnapshotEntry> active = await restarted.GetActiveAsync();
        RevertResult result = await restarted.RevertAsync(snap.Id);

        Assert.Single(active);
        Assert.IsType<RegistrySnapshotEntry>(active[0]);
        Assert.True(result.Success);
        Assert.Equal(RegistryValueData.FromDWord(8), Reg.ReadValue(t));
    }

    [Fact]
    public async Task Snapshot_is_persisted_before_the_system_is_modified()
    {
        RegistryTarget t = Target("Ordering");
        bool snapshotOnDiskAtWriteTime = false;
        Reg.FailWritesWhen = target =>
        {
            if (target.ValueName == "Ordering")
            {
                snapshotOnDiskAtWriteTime = File.ReadAllText(_h.Paths.SnapshotsFile).Contains("Ordering");
            }

            return false;
        };

        await _h.Snapshots.ApplyRegistryAsync("t", t, RegistryValueData.FromDWord(1));

        Assert.True(snapshotOnDiskAtWriteTime);
    }

    [Fact]
    public async Task Failed_apply_leaves_state_unchanged_and_drops_the_snapshot()
    {
        RegistryTarget t = Target("Locked");
        Reg.WriteValue(t, RegistryValueData.FromDWord(5));
        Reg.FailWritesWhen = target => target.ValueName == "Locked";

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _h.Snapshots.ApplyRegistryAsync("t", t, RegistryValueData.FromDWord(1)));

        Assert.Equal(RegistryValueData.FromDWord(5), Reg.ReadValue(t));
        Assert.Empty(await _h.Snapshots.GetActiveAsync());
        Assert.Contains(await _h.Audit.GetAllAsync(), e => e.Action == AuditAction.ApplyFailed);
    }

    [Fact]
    public async Task Service_round_trip_restores_start_type_and_running_state()
    {
        _h.Services.Services["SysMain"] = new ServiceState("SysMain", ServiceStartType.AutomaticDelayed, IsRunning: true);

        ServiceSnapshotEntry snap = await _h.Snapshots.ApplyServiceAsync("svc.sysmain", "SysMain",
            ServiceStartType.Disabled, stopService: true);
        Assert.Equal(new ServiceState("SysMain", ServiceStartType.Disabled, false), _h.Services.Query("SysMain"));

        await _h.Snapshots.RevertAsync(snap.Id);

        Assert.Equal(new ServiceState("SysMain", ServiceStartType.AutomaticDelayed, true), _h.Services.Query("SysMain"));
    }

    [Fact]
    public async Task Service_that_was_stopped_is_not_started_on_revert()
    {
        _h.Services.Services["Fax"] = new ServiceState("Fax", ServiceStartType.Manual, IsRunning: false);

        ServiceSnapshotEntry snap = await _h.Snapshots.ApplyServiceAsync("svc.fax", "Fax", ServiceStartType.Disabled);
        await _h.Snapshots.RevertAsync(snap.Id);

        Assert.Equal(new ServiceState("Fax", ServiceStartType.Manual, false), _h.Services.Query("Fax"));
    }

    [Fact]
    public async Task Missing_service_cannot_be_snapshotted()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _h.Snapshots.ApplyServiceAsync("svc.none", "DoesNotExist", ServiceStartType.Disabled));
        Assert.Empty(await _h.Snapshots.GetAllAsync());
    }

    [Fact]
    public async Task Power_round_trip_restores_ac_and_dc_indices()
    {
        Guid sub = Guid.Parse("54533251-82be-4824-96c1-47b60b740d00");
        Guid heteroPolicy = Guid.Parse("7f2f5cfa-f10c-4823-b5e1-e93ae85f46b5");
        _h.Power.Values[(_h.Power.ActiveScheme, sub, heteroPolicy)] = (4, 3);

        PowerSnapshotEntry snap = await _h.Snapshots.ApplyPowerAsync("cpu.hetero", sub, heteroPolicy, acValue: 0, dcValue: null);
        Assert.Equal((0u, 3u), _h.Power.Values[(_h.Power.ActiveScheme, sub, heteroPolicy)]);

        await _h.Snapshots.RevertAsync(snap.Id);

        Assert.Equal((4u, 3u), _h.Power.Values[(_h.Power.ActiveScheme, sub, heteroPolicy)]);
        Assert.Equal(_h.Power.ActiveScheme, snap.SchemeGuid);
    }

    [Fact]
    public async Task NvApi_setting_that_existed_is_restored_and_one_that_did_not_is_deleted()
    {
        const uint shaderCache = 0x00A06946, powerMode = 0x1057EB71;
        _h.NvApi.BaseProfile[shaderCache] = 0x2800;

        NvApiDrsSnapshotEntry a = await _h.Snapshots.ApplyNvApiDrsAsync("nv.shader", shaderCache, 0xFFFFFFFF);
        NvApiDrsSnapshotEntry b = await _h.Snapshots.ApplyNvApiDrsAsync("nv.power", powerMode, 1);
        Assert.True(a.ExistedInBaseProfile);
        Assert.False(b.ExistedInBaseProfile);

        RevertAllResult all = await _h.Snapshots.RevertAllAsync();

        Assert.Equal(2, all.Succeeded);
        Assert.Equal(0x2800u, _h.NvApi.BaseProfile[shaderCache]);
        Assert.False(_h.NvApi.BaseProfile.ContainsKey(powerMode));
    }

    [Fact]
    public async Task Revert_all_restores_everything_newest_first_and_reports_restart_items()
    {
        RegistryTarget t1 = Target("One");
        RegistryTarget t2 = Target("Two");
        Reg.WriteValue(t1, RegistryValueData.FromDWord(10));
        _h.Services.Services["DiagTrack"] = new ServiceState("DiagTrack", ServiceStartType.Automatic, true);

        await _h.Snapshots.ApplyRegistryAsync("t1", t1, RegistryValueData.FromDWord(1));
        _h.Clock.Advance(TimeSpan.FromSeconds(1));
        await _h.Snapshots.ApplyRegistryAsync("t2", t2, RegistryValueData.FromDWord(2), requiresRestart: true);
        _h.Clock.Advance(TimeSpan.FromSeconds(1));
        await _h.Snapshots.ApplyServiceAsync("svc", "DiagTrack", ServiceStartType.Disabled, stopService: true);

        RevertAllResult result = await _h.Snapshots.RevertAllAsync();

        Assert.Equal(new[] { "svc", "t2", "t1" }, result.Results.Select(r => r.TweakId));
        Assert.Equal(3, result.Succeeded);
        Assert.Equal("t2", Assert.Single(result.NeedingRestart).TweakId);
        Assert.Equal(RegistryValueData.FromDWord(10), Reg.ReadValue(t1));
        Assert.Null(Reg.ReadValue(t2));
        Assert.Equal(new ServiceState("DiagTrack", ServiceStartType.Automatic, true), _h.Services.Query("DiagTrack"));
        Assert.Empty(await _h.Snapshots.GetActiveAsync());

        IReadOnlyList<AuditEntry> history = await _h.Audit.GetAllAsync();
        Assert.Equal(3, history.Count(e => e.Action == AuditAction.Applied));
        Assert.Equal(3, history.Count(e => e.Action == AuditAction.Reverted));
        Assert.Single(history, e => e.Action == AuditAction.RevertAll);
    }

    [Fact]
    public async Task Revert_all_continues_past_failures_and_keeps_failed_items_active()
    {
        RegistryTarget ok = Target("Ok");
        RegistryTarget stuck = Target("Stuck");
        Reg.WriteValue(stuck, RegistryValueData.FromDWord(9));
        await _h.Snapshots.ApplyRegistryAsync("ok", ok, RegistryValueData.FromDWord(1));
        await _h.Snapshots.ApplyRegistryAsync("stuck", stuck, RegistryValueData.FromDWord(1));
        Reg.FailWritesWhen = target => target.ValueName == "Stuck";

        RevertAllResult result = await _h.Snapshots.RevertAllAsync();

        Assert.Equal(1, result.Succeeded);
        Assert.Equal(1, result.Failed);
        Assert.Null(Reg.ReadValue(ok));
        SnapshotEntry remaining = Assert.Single(await _h.Snapshots.GetActiveAsync());
        Assert.Equal("stuck", remaining.TweakId);
        Assert.NotNull(remaining.LastError);
    }

    [Fact]
    public async Task Reverting_twice_is_a_harmless_no_op()
    {
        RegistryTarget t = Target("Twice");
        RegistrySnapshotEntry snap = await _h.Snapshots.ApplyRegistryAsync("t", t, RegistryValueData.FromDWord(1));
        await _h.Snapshots.RevertAsync(snap.Id);
        Reg.WriteValue(t, RegistryValueData.FromDWord(77)); // user changed it afterwards

        RevertResult again = await _h.Snapshots.RevertAsync(snap.Id);

        Assert.True(again.Success);
        Assert.Equal(RegistryValueData.FromDWord(77), Reg.ReadValue(t));
    }

    [Fact]
    public async Task Snapshot_json_round_trips_polymorphically_with_exact_values()
    {
        RegistryTarget t = Target("Json");
        Reg.WriteValue(t, RegistryValueData.FromMultiString(new[] { "a", "b" }));
        _h.Services.Services["WSearch"] = new ServiceState("WSearch", ServiceStartType.AutomaticDelayed, true);
        await _h.Snapshots.ApplyRegistryAsync("t", t, RegistryValueData.FromDWord(0));
        await _h.Snapshots.ApplyServiceAsync("s", "WSearch", ServiceStartType.Disabled);

        SnapshotDocument doc = await AtomicJsonFile.ReadOrDefaultAsync(_h.Paths.SnapshotsFile, () => new SnapshotDocument());

        var reg = Assert.IsType<RegistrySnapshotEntry>(doc.Entries[0]);
        var svc = Assert.IsType<ServiceSnapshotEntry>(doc.Entries[1]);
        Assert.Equal(RegistryValueKind.MultiString, reg.Original!.Kind);
        Assert.Equal(new[] { "a", "b" }, reg.Original.MultiString);
        Assert.Equal(ServiceStartType.AutomaticDelayed, svc.OriginalStartType);
        Assert.True(svc.WasRunning);
        Assert.Contains("\"$type\": \"registry\"", await File.ReadAllTextAsync(_h.Paths.SnapshotsFile));
    }

    public void Dispose() => _h.Dispose();
}

using RigTune.Core.Models;
using RigTune.Core.Services;
using RigTune.Core.Tests.Fakes;

namespace RigTune.Core.Tests;

public sealed class RestorePointServiceTests : IDisposable
{
    private readonly SnapshotHarness _h = new();
    private readonly FakeRestorePointBackend _backend = new();

    private RestorePointService CreateService() => new(_backend, _h.Registry, _h.Snapshots, _h.Audit);

    [Fact]
    public async Task Create_bypasses_cooldown_snapshotting_the_original_first()
    {
        _h.Registry.WriteValue(RestorePointService.CooldownTarget, RegistryValueData.FromDWord(1440));

        RestorePointCreateResult result = await CreateService().CreateAsync("Before tweaks");

        Assert.True(result.Success);
        Assert.Equal(RegistryValueData.FromDWord(0), _h.Registry.ReadValue(RestorePointService.CooldownTarget));
        var snap = Assert.IsType<RegistrySnapshotEntry>(Assert.Single(await _h.Snapshots.GetActiveAsync()));
        Assert.Equal(RestorePointService.CooldownTweakId, snap.TweakId);
        Assert.Equal(RegistryValueData.FromDWord(1440), snap.Original);
        Assert.Equal(("Before tweaks", RestorePointType.ModifySettings), Assert.Single(_backend.Created));
    }

    [Fact]
    public async Task Two_restore_points_in_a_row_snapshot_cooldown_only_once_and_revert_deletes_it()
    {
        RestorePointService service = CreateService();

        await service.CreateAsync("one");
        await service.CreateAsync("two");

        Assert.Equal(2, _backend.Created.Count);
        var snap = Assert.IsType<RegistrySnapshotEntry>(Assert.Single(await _h.Snapshots.GetActiveAsync()));
        Assert.False(snap.Existed);

        await _h.Snapshots.RevertAsync(snap.Id);
        Assert.Null(_h.Registry.ReadValue(RestorePointService.CooldownTarget));
    }

    [Fact]
    public async Task Results_are_written_to_the_audit_log()
    {
        RestorePointService service = CreateService();
        await service.CreateAsync("ok");
        _backend.Succeeds = () => false;
        RestorePointCreateResult failed = await service.CreateAsync("bad");

        Assert.False(failed.Success);
        IReadOnlyList<AuditEntry> log = await _h.Audit.GetAllAsync();
        Assert.Contains(log, e => e.Action == AuditAction.RestorePointCreated && e.Summary.Contains("\"ok\""));
        Assert.Contains(log, e => e.Action == AuditAction.RestorePointFailed && !e.Success);
    }

    [Fact]
    public async Task Blank_label_gets_a_default_description()
    {
        await CreateService().CreateAsync("  ");

        Assert.StartsWith("RigTune restore point", _backend.Created[0].Description);
    }

    public void Dispose() => _h.Dispose();
}

using RigTune.Core.Models;
using RigTune.Core.Services;
using RigTune.Core.Storage;
using RigTune.Core.Tests.Fakes;

namespace RigTune.Core.Tests;

public sealed class AuditLogServiceTests : IDisposable
{
    private readonly TempDir _dir = new();

    [Fact]
    public async Task Entries_are_persisted_and_returned_newest_first()
    {
        var clock = new ManualTimeProvider();
        var paths = new RigTunePaths(_dir.Path);
        var audit = new AuditLogService(paths, clock);

        await audit.LogAsync(AuditAction.Applied, "first", tweakId: "t1");
        clock.Advance(TimeSpan.FromMinutes(1));
        await audit.LogAsync(AuditAction.Reverted, "second", tweakId: "t1");

        IReadOnlyList<AuditEntry> recent = await new AuditLogService(paths, clock).GetRecentAsync(10);
        Assert.Equal(new[] { "second", "first" }, recent.Select(e => e.Summary));
        Assert.Equal(AuditAction.Reverted, recent[0].Action);
    }

    [Fact]
    public async Task Log_is_capped_dropping_oldest_entries()
    {
        var clock = new ManualTimeProvider();
        var audit = new AuditLogService(new RigTunePaths(_dir.Path), clock, maxEntries: 3);
        for (int i = 0; i < 5; i++)
        {
            await audit.LogAsync(AuditAction.Info, $"e{i}");
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        IReadOnlyList<AuditEntry> all = await audit.GetAllAsync();
        Assert.Equal(new[] { "e2", "e3", "e4" }, all.Select(e => e.Summary));
    }

    public void Dispose() => _dir.Dispose();
}

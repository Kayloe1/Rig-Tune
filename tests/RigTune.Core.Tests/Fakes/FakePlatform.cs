using RigTune.Core.Models;
using RigTune.Core.Platform;

namespace RigTune.Core.Tests.Fakes;

public sealed class FakeServiceAccess : IServiceAccess
{
    public Dictionary<string, ServiceState> Services { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ServiceState? Query(string serviceName) => Services.GetValueOrDefault(serviceName);

    public void SetStartType(string serviceName, ServiceStartType startType) =>
        Services[serviceName] = Services[serviceName] with { StartType = startType };

    public void Start(string serviceName, TimeSpan timeout) =>
        Services[serviceName] = Services[serviceName] with { IsRunning = true };

    public void Stop(string serviceName, TimeSpan timeout) =>
        Services[serviceName] = Services[serviceName] with { IsRunning = false };
}

public sealed class FakePowerAccess : IPowerAccess
{
    public Guid ActiveScheme { get; set; } = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
    public Dictionary<(Guid, Guid, Guid), (uint Ac, uint Dc)> Values { get; } = new();
    public int ApplyCount { get; private set; }

    public Guid GetActiveScheme() => ActiveScheme;
    public uint ReadAcValue(Guid scheme, Guid subgroup, Guid setting) => Values[(scheme, subgroup, setting)].Ac;
    public uint ReadDcValue(Guid scheme, Guid subgroup, Guid setting) => Values[(scheme, subgroup, setting)].Dc;

    public void WriteAcValue(Guid scheme, Guid subgroup, Guid setting, uint value) =>
        Values[(scheme, subgroup, setting)] = Values[(scheme, subgroup, setting)] with { Ac = value };

    public void WriteDcValue(Guid scheme, Guid subgroup, Guid setting, uint value) =>
        Values[(scheme, subgroup, setting)] = Values[(scheme, subgroup, setting)] with { Dc = value };

    public void ApplyScheme(Guid scheme) => ApplyCount++;
}

public sealed class FakeNvApi : INvApiDrsAccess
{
    public Dictionary<uint, uint> BaseProfile { get; } = new();
    public bool IsAvailable { get; set; } = true;

    public NvDrsValue ReadGlobal(uint settingId) =>
        BaseProfile.TryGetValue(settingId, out uint v) ? new NvDrsValue(true, v) : new NvDrsValue(false, null);

    public void WriteGlobal(uint settingId, uint value) => BaseProfile[settingId] = value;

    public void DeleteGlobal(uint settingId) => BaseProfile.Remove(settingId);
}

public sealed class FakeRestorePointBackend : IRestorePointBackend
{
    public List<(string Description, RestorePointType Type)> Created { get; } = new();
    public Func<bool> Succeeds { get; set; } = () => true;

    public RestorePointCreateResult Create(string description, RestorePointType type)
    {
        if (!Succeeds())
        {
            return new RestorePointCreateResult(false, "Simulated failure", null, "fake");
        }

        Created.Add((description, type));
        return new RestorePointCreateResult(true, "ok", Created.Count, "fake");
    }

    public IReadOnlyList<RestorePointInfo> Enumerate() =>
        Created.Select((c, i) => new RestorePointInfo((uint)(i + 1), c.Description, (int)c.Type, DateTimeOffset.Now)).ToList();
}

/// <summary>Deterministic, manually advanced clock.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

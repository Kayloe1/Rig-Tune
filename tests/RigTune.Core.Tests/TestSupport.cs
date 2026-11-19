using RigTune.Core.Models;
using RigTune.Core.Platform;
using RigTune.Core.Services;
using RigTune.Core.Storage;
using RigTune.Core.Tests.Fakes;

namespace RigTune.Core.Tests;

/// <summary>A temp directory standing in for %LOCALAPPDATA%\RigTune, removed on dispose.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RigTuneTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Wires a SnapshotService against fakes (or a supplied registry) and an isolated data folder.</summary>
public sealed class SnapshotHarness : IDisposable
{
    private readonly TempDir _dir = new();

    public SnapshotHarness(IRegistryAccess? registry = null)
    {
        Registry = registry ?? new InMemoryRegistry();
        Paths = new RigTunePaths(_dir.Path);
        Audit = new AuditLogService(Paths, Clock);
        Snapshots = CreateSnapshotService();
    }

    public ManualTimeProvider Clock { get; } = new();
    public RigTunePaths Paths { get; }
    public IRegistryAccess Registry { get; }
    public FakeServiceAccess Services { get; } = new();
    public FakePowerAccess Power { get; } = new();
    public FakeNvApi NvApi { get; } = new();
    public AuditLogService Audit { get; }
    public SnapshotService Snapshots { get; }

    /// <summary>Simulates an app restart: a brand-new service instance reading snapshots.json from disk.</summary>
    public SnapshotService CreateSnapshotService() =>
        new(Paths, Registry, Services, Power, NvApi, new AuditLogService(Paths, Clock), Clock);

    public void Dispose() => _dir.Dispose();
}

/// <summary>Runs only on Windows (real registry / Win32 APIs); skipped elsewhere.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Requires Windows (uses the real registry under HKCU\\Software\\RigTuneTest).";
        }
    }
}

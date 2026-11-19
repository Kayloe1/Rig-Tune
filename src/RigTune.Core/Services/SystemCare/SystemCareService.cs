namespace RigTune.Core.Services.SystemCare;

/// <summary>Façade over the System Care sub-workflows (restore points, cleanup, startup apps, repair).</summary>
public sealed class SystemCareService
{
    public SystemCareService(RestorePointService restorePoints, CleanupEngine cleanup, StartupManager startup, RepairScanner repair)
    {
        RestorePoints = restorePoints;
        Cleanup = cleanup;
        Startup = startup;
        Repair = repair;
    }

    public RestorePointService RestorePoints { get; }
    public CleanupEngine Cleanup { get; }
    public StartupManager Startup { get; }
    public RepairScanner Repair { get; }
}

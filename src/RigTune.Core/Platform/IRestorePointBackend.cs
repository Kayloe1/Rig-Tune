using RigTune.Core.Models;

namespace RigTune.Core.Platform;

/// <summary>Creates and enumerates System Restore points (srclient.dll / WMI).</summary>
public interface IRestorePointBackend
{
    RestorePointCreateResult Create(string description, RestorePointType type);

    IReadOnlyList<RestorePointInfo> Enumerate();
}

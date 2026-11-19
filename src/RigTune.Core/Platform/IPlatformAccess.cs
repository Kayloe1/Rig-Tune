using RigTune.Core.Models;

namespace RigTune.Core.Platform;

/// <summary>Registry primitive operations. Abstracted so snapshot logic is unit-testable.</summary>
public interface IRegistryAccess
{
    bool KeyExists(RegistryHiveName hive, RegistryViewName view, string keyPath);

    /// <summary>Returns the exact value, or null if the key or value does not exist.</summary>
    RegistryValueData? ReadValue(RegistryTarget target);

    /// <summary>Writes the value, creating the key (and missing parents) when needed.</summary>
    void WriteValue(RegistryTarget target, RegistryValueData data);

    /// <summary>Deletes the value. Returns false if it was already absent.</summary>
    bool DeleteValue(RegistryTarget target);

    /// <summary>Deletes the key only if it has no values and no subkeys. Returns true if deleted.</summary>
    bool DeleteKeyIfEmpty(RegistryHiveName hive, RegistryViewName view, string keyPath);
}

/// <summary>Windows Service Control Manager operations.</summary>
public interface IServiceAccess
{
    /// <summary>Returns null if the service is not installed.</summary>
    ServiceState? Query(string serviceName);

    void SetStartType(string serviceName, ServiceStartType startType);

    void Start(string serviceName, TimeSpan timeout);

    void Stop(string serviceName, TimeSpan timeout);
}

/// <summary>Power scheme value access (powrprof.dll).</summary>
public interface IPowerAccess
{
    Guid GetActiveScheme();

    uint ReadAcValue(Guid scheme, Guid subgroup, Guid setting);

    uint ReadDcValue(Guid scheme, Guid subgroup, Guid setting);

    void WriteAcValue(Guid scheme, Guid subgroup, Guid setting, uint value);

    void WriteDcValue(Guid scheme, Guid subgroup, Guid setting, uint value);

    /// <summary>Re-activates the scheme so edited values take effect immediately.</summary>
    void ApplyScheme(Guid scheme);
}

public readonly record struct NvDrsValue(bool ExistsInBaseProfile, uint? Value);

/// <summary>NVIDIA driver settings (DRS) global/base profile access via nvapi64.dll.</summary>
public interface INvApiDrsAccess
{
    bool IsAvailable { get; }

    NvDrsValue ReadGlobal(uint settingId);

    void WriteGlobal(uint settingId, uint value);

    /// <summary>Removes the user override so the driver default applies again.</summary>
    void DeleteGlobal(uint settingId);
}

using System.Management;
using RigTune.Core.Models;
using RigTune.Core.Platform;

namespace RigTune.Core.Runners;

/// <summary>
/// Physical network adapters: per-adapter advanced properties (<c>*InterruptModeration</c>, <c>*RSS</c>)
/// stored under the adapter's class key, TCP/IP interface keys (Nagle), and the global TCP profile.
/// </summary>
public sealed class NetworkAdapterRunner
{
    public const string NetClassGuid = "{4d36e972-e325-11ce-bfc1-08002be10318}";
    public const string InterruptModerationKeyword = "*InterruptModeration";
    public const string RssKeyword = "*RSS";

    private static readonly HashSet<string> VirtualEnumerators = new(StringComparer.OrdinalIgnoreCase)
    {
        "ROOT", "SWD", "VMBUS",
    };

    private readonly DeviceManagerRunner _devices;
    private readonly IRegistryAccess _registry;

    public NetworkAdapterRunner(DeviceManagerRunner devices, IRegistryAccess registry)
    {
        _devices = devices;
        _registry = registry;
    }

    public IReadOnlyList<NetworkAdapterInfo> EnumerateAdapters() =>
        _devices.EnumerateDevices()
            .Where(d => d.Class.Equals("Net", StringComparison.OrdinalIgnoreCase) &&
                        !VirtualEnumerators.Contains(d.Enumerator) &&
                        !string.IsNullOrEmpty(d.DriverKey))
            .Select(d =>
            {
                string classKey = $@"SYSTEM\CurrentControlSet\Control\Class\{d.DriverKey}";
                return new NetworkAdapterInfo(
                    d,
                    classKey,
                    ReadString(classKey, "NetCfgInstanceId"),
                    ReadString(classKey, InterruptModerationKeyword),
                    _registry.KeyExists(RegistryHiveName.HKLM, RegistryViewName.Registry64, $@"{classKey}\Ndi\Params\{InterruptModerationKeyword}"),
                    ReadString(classKey, RssKeyword),
                    _registry.KeyExists(RegistryHiveName.HKLM, RegistryViewName.Registry64, $@"{classKey}\Ndi\Params\{RssKeyword}"));
            })
            .OrderBy(a => a.Device.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static RegistryTarget AdapterPropertyTarget(NetworkAdapterInfo adapter, string keyword) =>
        new(RegistryHiveName.HKLM, RegistryViewName.Registry64, adapter.ClassKeyPath, keyword);

    /// <summary>TCP/IP interface key of an adapter (where TcpAckFrequency / TCPNoDelay live).</summary>
    public static RegistryTarget TcpInterfaceTarget(string netCfgInstanceId, string valueName) =>
        new(RegistryHiveName.HKLM, RegistryViewName.Registry64,
            $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{netCfgInstanceId}", valueName);

    /// <summary>Restarts the adapter so advanced-property changes take effect (brief disconnect).</summary>
    public void RestartAdapter(NetworkAdapterInfo adapter) => _devices.Restart(adapter.Device.InstanceId);

    private string? ReadString(string keyPath, string valueName) =>
        _registry.ReadValue(new RegistryTarget(RegistryHiveName.HKLM, RegistryViewName.Registry64, keyPath, valueName))
            ?.String;
}

/// <summary>
/// Global TCP settings as snapshot-able state. Read language-independently from CIM
/// (<c>root\StandardCimv2</c>), written with <c>netsh</c>. Targets: autotuninglevel, heuristics, rss.
/// </summary>
public sealed class TcpGlobalStateRunner : IStateRunner
{
    public string Kind => "tcp_global";

    public string? ReadState(string target)
    {
        try
        {
            return target.ToLowerInvariant() switch
            {
                "autotuninglevel" => QueryUInt("SELECT AutoTuningLevelLocal FROM MSFT_NetTCPSetting WHERE SettingName='Internet'",
                    "AutoTuningLevelLocal") switch
                {
                    0 => "disabled",
                    1 => "highlyrestricted",
                    2 => "restricted",
                    3 => "normal",
                    4 => "experimental",
                    _ => null,
                },
                "heuristics" => QueryUInt("SELECT ScalingHeuristics FROM MSFT_NetTCPSetting WHERE SettingName='Internet'",
                    "ScalingHeuristics") switch
                {
                    0 => "disabled",
                    1 => "enabled",
                    _ => null,
                },
                "rss" => QueryUInt("SELECT ReceiveSideScaling FROM MSFT_NetOffloadGlobalSetting", "ReceiveSideScaling") switch
                {
                    0 => "disabled",
                    1 => "enabled",
                    _ => null,
                },
                _ => null,
            };
        }
        catch (ManagementException)
        {
            return null;
        }
    }

    public void WriteState(string target, string state)
    {
        string value = new string(state.Where(char.IsLetter).ToArray()).ToLowerInvariant();
        string args = target.ToLowerInvariant() switch
        {
            "autotuninglevel" => $"int tcp set global autotuninglevel={value}",
            "heuristics" => $"int tcp set heuristics {value}",
            "rss" => $"int tcp set global rss={value}",
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown TCP global setting."),
        };
        ProcessRunner.RunChecked("netsh", args);
    }

    public string Describe(string target) => $"TCP global: {target}";

    private static uint? QueryUInt(string query, string property)
    {
        var scope = new ManagementScope(@"\\.\root\StandardCimv2");
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(query));
        using ManagementObjectCollection results = searcher.Get();
        foreach (ManagementBaseObject obj in results)
        {
            using (obj)
            {
                return obj[property] is { } v ? Convert.ToUInt32(v) : null;
            }
        }

        return null;
    }
}

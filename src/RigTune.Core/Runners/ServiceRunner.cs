using System.ComponentModel;
using System.ServiceProcess;
using Microsoft.Win32;
using RigTune.Core.Models;
using RigTune.Core.Native;
using RigTune.Core.Platform;

namespace RigTune.Core.Runners;

/// <summary>Windows SCM implementation of <see cref="IServiceAccess"/> (start type via <c>ChangeServiceConfig</c>).</summary>
public sealed class ServiceRunner : IServiceAccess
{
    public ServiceState? Query(string serviceName)
    {
        using var controller = new ServiceController(serviceName);
        try
        {
            ServiceStartMode mode = controller.StartType; // throws InvalidOperationException if not installed
            bool running = controller.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending
                or ServiceControllerStatus.ContinuePending;
            ServiceStartType startType = mode switch
            {
                ServiceStartMode.Boot => ServiceStartType.Boot,
                ServiceStartMode.System => ServiceStartType.System,
                ServiceStartMode.Automatic => IsDelayedAutoStart(controller.ServiceName)
                    ? ServiceStartType.AutomaticDelayed
                    : ServiceStartType.Automatic,
                ServiceStartMode.Manual => ServiceStartType.Manual,
                ServiceStartMode.Disabled => ServiceStartType.Disabled,
                _ => ServiceStartType.Manual,
            };
            return new ServiceState(controller.ServiceName, startType, running);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public void SetStartType(string serviceName, ServiceStartType startType)
    {
        IntPtr scm = AdvApi32.OpenSCManager(null, null, AdvApi32.SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero)
        {
            throw new Win32Exception();
        }

        try
        {
            IntPtr service = AdvApi32.OpenService(scm, serviceName,
                AdvApi32.SERVICE_CHANGE_CONFIG | AdvApi32.SERVICE_QUERY_CONFIG);
            if (service == IntPtr.Zero)
            {
                throw new Win32Exception();
            }

            try
            {
                uint nativeStart = startType switch
                {
                    ServiceStartType.Boot => AdvApi32.SERVICE_BOOT_START,
                    ServiceStartType.System => AdvApi32.SERVICE_SYSTEM_START,
                    ServiceStartType.Automatic or ServiceStartType.AutomaticDelayed => AdvApi32.SERVICE_AUTO_START,
                    ServiceStartType.Manual => AdvApi32.SERVICE_DEMAND_START,
                    ServiceStartType.Disabled => AdvApi32.SERVICE_DISABLED,
                    _ => throw new ArgumentOutOfRangeException(nameof(startType)),
                };

                if (!AdvApi32.ChangeServiceConfig(service, AdvApi32.SERVICE_NO_CHANGE, nativeStart,
                        AdvApi32.SERVICE_NO_CHANGE, null, null, IntPtr.Zero, null, null, null, null))
                {
                    var error = new Win32Exception();
                    if (error.NativeErrorCode != ErrorAccessDenied)
                    {
                        throw error;
                    }

                    // Protected services (e.g. WaaSMedicSvc) reject SCM changes even for admins;
                    // their configuration lives in the service key, which administrators can edit.
                    WriteStartTypeToRegistry(serviceName, nativeStart, startType == ServiceStartType.AutomaticDelayed);
                    return;
                }

                if (startType is ServiceStartType.Automatic or ServiceStartType.AutomaticDelayed)
                {
                    var info = new AdvApi32.SERVICE_DELAYED_AUTO_START_INFO
                    {
                        fDelayedAutostart = startType == ServiceStartType.AutomaticDelayed ? 1 : 0,
                    };
                    if (!AdvApi32.ChangeServiceConfig2(service, AdvApi32.SERVICE_CONFIG_DELAYED_AUTO_START_INFO, ref info))
                    {
                        throw new Win32Exception();
                    }
                }
            }
            finally
            {
                AdvApi32.CloseServiceHandle(service);
            }
        }
        finally
        {
            AdvApi32.CloseServiceHandle(scm);
        }
    }

    public void Start(string serviceName, TimeSpan timeout)
    {
        using var controller = new ServiceController(serviceName);
        if (controller.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
        {
            return;
        }

        controller.Start();
        controller.WaitForStatus(ServiceControllerStatus.Running, timeout);
    }

    public void Stop(string serviceName, TimeSpan timeout)
    {
        using var controller = new ServiceController(serviceName);
        if (controller.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)
        {
            return;
        }

        if (controller.CanStop)
        {
            controller.Stop();
            controller.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
        }
    }

    private const int ErrorAccessDenied = 5;

    private static void WriteStartTypeToRegistry(string serviceName, uint nativeStart, bool delayed)
    {
        using RegistryKey key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}", writable: true)
                                ?? throw new InvalidOperationException($"Service key for {serviceName} not found.");
        key.SetValue("Start", unchecked((int)nativeStart), RegistryValueKind.DWord);
        if (nativeStart == AdvApi32.SERVICE_AUTO_START)
        {
            key.SetValue("DelayedAutostart", delayed ? 1 : 0, RegistryValueKind.DWord);
        }
    }

    private static bool IsDelayedAutoStart(string serviceName)
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
        return key?.GetValue("DelayedAutostart") is int value && value != 0;
    }
}

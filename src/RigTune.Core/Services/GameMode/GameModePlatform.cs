using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using RigTune.Core.Models;
using RigTune.Core.Native;

namespace RigTune.Core.Services.GameMode;

public static class PriorityClasses
{
    public const int Idle = 0x40;
    public const int BelowNormal = 0x4000;
    public const int Normal = 0x20;
    public const int AboveNormal = 0x8000;
    public const int High = 0x80;
    public const int Realtime = 0x100;

    public static string Name(int value) => value switch
    {
        Idle => "Idle",
        BelowNormal => "Below Normal",
        Normal => "Normal",
        AboveNormal => "Above Normal",
        High => "High",
        Realtime => "Realtime",
        _ => $"0x{value:X}",
    };
}

/// <summary>I/O priority hints (IO_PRIORITY_HINT).</summary>
public static class IoPriorities
{
    public const int VeryLow = 0;
    public const int Low = 1;
    public const int Normal = 2;
}

/// <summary>Everything Game Mode does to processes and the power plan (abstracted for crash-recovery tests).</summary>
public interface IGameModePlatform
{
    IReadOnlyList<RunningProcess> GetProcesses();

    RunningProcess? GetProcess(int pid);

    int? GetPriorityClass(int pid);

    bool SetPriorityClass(int pid, int priorityClass);

    ThrottleState? GetPowerThrottling(int pid);

    bool SetPowerThrottling(int pid, ThrottleState state);

    int? GetIoPriority(int pid);

    bool SetIoPriority(int pid, int ioPriority);

    bool TrimWorkingSet(int pid);

    uint GetMemoryLoadPercent();

    Guid GetActivePowerPlan();

    bool SetActivePowerPlan(Guid scheme);

    int? GetForegroundProcessId();
}

public sealed class WindowsGameModePlatform : IGameModePlatform
{
    public IReadOnlyList<RunningProcess> GetProcesses()
    {
        var list = new List<RunningProcess>();
        foreach (Process p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id is 0 or 4)
                {
                    continue;
                }

                list.Add(new RunningProcess(p.Id, p.ProcessName + ".exe", QueryPath(p.Id), StartTime(p)));
            }
        }

        return list;
    }

    public RunningProcess? GetProcess(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            return new RunningProcess(p.Id, p.ProcessName + ".exe", QueryPath(pid), StartTime(p));
        }
        catch (ArgumentException)
        {
            return null; // exited
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public int? GetPriorityClass(int pid) =>
        WithHandle(pid, ProcessApi.PROCESS_QUERY_LIMITED_INFORMATION, h =>
        {
            uint value = ProcessApi.GetPriorityClass(h);
            return value == 0 ? (int?)null : (int)value;
        });

    public bool SetPriorityClass(int pid, int priorityClass) =>
        WithHandle(pid, ProcessApi.PROCESS_SET_INFORMATION, h => ProcessApi.SetPriorityClass(h, (uint)priorityClass));

    public ThrottleState? GetPowerThrottling(int pid) =>
        WithHandle(pid, ProcessApi.PROCESS_QUERY_INFORMATION, h =>
        {
            var state = new ProcessApi.PROCESS_POWER_THROTTLING_STATE { Version = ProcessApi.PROCESS_POWER_THROTTLING_CURRENT_VERSION };
            return ProcessApi.GetProcessInformation(h, ProcessApi.ProcessPowerThrottling, ref state, Marshal.SizeOf(state))
                ? new ThrottleState(state.ControlMask, state.StateMask)
                : null;
        });

    public bool SetPowerThrottling(int pid, ThrottleState state) =>
        WithHandle(pid, ProcessApi.PROCESS_SET_INFORMATION, h =>
        {
            var native = new ProcessApi.PROCESS_POWER_THROTTLING_STATE
            {
                Version = ProcessApi.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = state.ControlMask,
                StateMask = state.StateMask,
            };
            return ProcessApi.SetProcessInformation(h, ProcessApi.ProcessPowerThrottling, ref native, Marshal.SizeOf(native));
        });

    public int? GetIoPriority(int pid) =>
        WithHandle(pid, ProcessApi.PROCESS_QUERY_INFORMATION, h =>
        {
            int value = 0;
            return ProcessApi.NtQueryInformationProcess(h, ProcessApi.ProcessIoPriority, ref value, sizeof(int), out _) == 0 ? value : (int?)null;
        });

    public bool SetIoPriority(int pid, int ioPriority) =>
        WithHandle(pid, ProcessApi.PROCESS_SET_INFORMATION, h =>
        {
            int value = ioPriority;
            return ProcessApi.NtSetInformationProcess(h, ProcessApi.ProcessIoPriority, ref value, sizeof(int)) == 0;
        });

    public bool TrimWorkingSet(int pid) =>
        WithHandle(pid, ProcessApi.PROCESS_QUERY_INFORMATION | ProcessApi.PROCESS_SET_QUOTA, MemoryApi.EmptyWorkingSet);

    public uint GetMemoryLoadPercent() => Hardware.HostProfileDetector.DetectMemory().LoadPercent;

    public Guid GetActivePowerPlan() => new Runners.PowerRunner().GetActiveScheme();

    public bool SetActivePowerPlan(Guid scheme) => PowrProf.PowerSetActiveScheme(IntPtr.Zero, ref scheme) == 0;

    public int? GetForegroundProcessId()
    {
        IntPtr hwnd = ProcessApi.GetForegroundWindow();
        return hwnd == IntPtr.Zero || ProcessApi.GetWindowThreadProcessId(hwnd, out int pid) == 0 ? null : pid;
    }

    private static string? QueryPath(int pid) =>
        WithHandle(pid, ProcessApi.PROCESS_QUERY_LIMITED_INFORMATION, h =>
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return ProcessApi.QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        });

    private static DateTime? StartTime(Process p)
    {
        try
        {
            return p.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static T? WithHandle<T>(int pid, uint access, Func<IntPtr, T> action)
    {
        IntPtr handle = ProcessApi.OpenProcess(access, false, pid);
        if (handle == IntPtr.Zero)
        {
            return default;
        }

        try
        {
            return action(handle);
        }
        finally
        {
            ProcessApi.CloseHandle(handle);
        }
    }
}

/// <summary>
/// Process-start notifications: WMI <c>Win32_ProcessStartTrace</c> (event-driven), falling back to a
/// 2-second snapshot poller if the WMI subscription cannot be created.
/// </summary>
public sealed class ProcessStartWatcher : IDisposable
{
    private readonly IGameModePlatform _platform;
    private ManagementEventWatcher? _wmi;
    private Timer? _poller;
    private HashSet<int> _known = new();

    public ProcessStartWatcher(IGameModePlatform platform) => _platform = platform;

    public event EventHandler<int>? ProcessStarted;

    public bool UsingWmi { get; private set; }

    public void Start()
    {
        if (_wmi is not null || _poller is not null)
        {
            return;
        }

        try
        {
            _wmi = new ManagementEventWatcher(new WqlEventQuery("SELECT ProcessID FROM Win32_ProcessStartTrace"));
            _wmi.EventArrived += (_, e) =>
            {
                if (e.NewEvent["ProcessID"] is { } pid)
                {
                    ProcessStarted?.Invoke(this, Convert.ToInt32(pid));
                }
            };
            _wmi.Start();
            UsingWmi = true;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException or PlatformNotSupportedException)
        {
            _wmi?.Dispose();
            _wmi = null;
            StartPolling();
        }
    }

    private void StartPolling()
    {
        UsingWmi = false;
        _known = _platform.GetProcesses().Select(p => p.Pid).ToHashSet();
        _poller = new Timer(_ =>
        {
            HashSet<int> now = _platform.GetProcesses().Select(p => p.Pid).ToHashSet();
            foreach (int pid in now.Where(p => !_known.Contains(p)))
            {
                ProcessStarted?.Invoke(this, pid);
            }

            _known = now;
        }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    public void Stop()
    {
        try
        {
            _wmi?.Stop();
        }
        catch (ManagementException)
        {
        }

        _wmi?.Dispose();
        _wmi = null;
        _poller?.Dispose();
        _poller = null;
    }

    public void Dispose() => Stop();
}

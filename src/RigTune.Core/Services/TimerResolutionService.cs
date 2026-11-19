using System.Diagnostics;
using System.Runtime.InteropServices;
using RigTune.Core.Models;
using RigTune.Core.Native;

namespace RigTune.Core.Services;

/// <summary>
/// High-precision timer resolution scoped strictly to RigTune's own process (SPEC §3.3.7).
/// Measures real <c>Sleep(1)</c> latency before and after, and always releases the request on
/// <see cref="Reset"/>, dispose, or process exit.
/// </summary>
public sealed class TimerResolutionService : IDisposable
{
    private const int ProcessPowerThrottling = 4;
    private const uint ThrottlingIgnoreTimerResolution = 0x4;

    private readonly object _lock = new();
    private uint? _requested;

    public TimerResolutionService()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Reset();
    }

    public bool IsActive
    {
        get
        {
            lock (_lock)
            {
                return _requested is not null;
            }
        }
    }

    public TimerResolutionInfo Query()
    {
        TimerApi.NtQueryTimerResolution(out uint min, out uint max, out uint current);
        // NT naming is inverted: "minimum" is the coarsest interval, "maximum" the finest.
        return new TimerResolutionInfo(max / 10_000.0, min / 10_000.0, current / 10_000.0);
    }

    /// <summary>Average real duration of <c>Sleep(1)</c> in milliseconds.</summary>
    public static double MeasureSleep1(int iterations = 40)
    {
        var sw = new Stopwatch();
        double total = 0;
        for (int i = 0; i < iterations; i++)
        {
            sw.Restart();
            TimerApi.Sleep(1);
            total += sw.Elapsed.TotalMilliseconds;
        }

        return total / iterations;
    }

    public Task<TimerResolutionResult> RequestAsync(double targetMs = 0.5) => Task.Run(() => Request(targetMs));

    public TimerResolutionResult Request(double targetMs = 0.5)
    {
        lock (_lock)
        {
            TimerResolutionInfo before = Query();
            double sleepBefore = MeasureSleep1();

            // Windows 11 ignores timer requests from processes it considers in the background unless opted out.
            OptOutOfTimerThrottling();

            uint desired = (uint)Math.Max(before.MaximumMs * 10_000, targetMs * 10_000);
            TimerApi.NtSetTimerResolution(desired, true, out _);
            _requested = desired;

            TimerResolutionInfo after = Query();
            return new TimerResolutionResult(before, after, sleepBefore, MeasureSleep1(), true);
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            if (_requested is { } desired)
            {
                TimerApi.NtSetTimerResolution(desired, false, out _);
                _requested = null;
            }
        }
    }

    public void Dispose() => Reset();

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass,
        ref PROCESS_POWER_THROTTLING_STATE info, int size);

    private static void OptOutOfTimerThrottling()
    {
        var state = new PROCESS_POWER_THROTTLING_STATE
        {
            Version = 1,
            ControlMask = ThrottlingIgnoreTimerResolution,
            StateMask = 0,
        };
        SetProcessInformation(Process.GetCurrentProcess().Handle, ProcessPowerThrottling, ref state,
            Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
    }
}

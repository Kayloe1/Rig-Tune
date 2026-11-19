using RigTune.Core.Models;
using RigTune.Core.Services.GameMode;

namespace RigTune.Core.Tests.Fakes;

public sealed class FakeProcess
{
    public required int Pid { get; init; }
    public required string Name { get; init; }
    public string? Path { get; init; }
    public DateTime StartTimeUtc { get; init; } = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
    public int Priority { get; set; } = PriorityClasses.Normal;
    public ThrottleState Throttle { get; set; } = ThrottleState.Default;
    public int Io { get; set; } = IoPriorities.Normal;
    public int Trims { get; set; }
}

public sealed class FakeGameModePlatform : IGameModePlatform
{
    public Dictionary<int, FakeProcess> Processes { get; } = new();
    public uint MemoryLoad { get; set; } = 40;
    public Guid PowerPlan { get; set; } = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
    public HashSet<Guid> KnownPlans { get; } = new() { Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e"), Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c") };
    public int? Foreground { get; set; }

    public FakeProcess Add(int pid, string name, string? path = null, int priority = PriorityClasses.Normal)
    {
        var p = new FakeProcess { Pid = pid, Name = name, Path = path, Priority = priority };
        Processes[pid] = p;
        return p;
    }

    public IReadOnlyList<RunningProcess> GetProcesses() => Processes.Values.Select(ToRunning).ToList();
    public RunningProcess? GetProcess(int pid) => Processes.TryGetValue(pid, out FakeProcess? p) ? ToRunning(p) : null;
    public int? GetPriorityClass(int pid) => Processes.TryGetValue(pid, out FakeProcess? p) ? p.Priority : null;
    public bool SetPriorityClass(int pid, int priorityClass) => Mutate(pid, p => p.Priority = priorityClass);
    public ThrottleState? GetPowerThrottling(int pid) => Processes.TryGetValue(pid, out FakeProcess? p) ? p.Throttle : null;
    public bool SetPowerThrottling(int pid, ThrottleState state) => Mutate(pid, p => p.Throttle = state);
    public int? GetIoPriority(int pid) => Processes.TryGetValue(pid, out FakeProcess? p) ? p.Io : null;
    public bool SetIoPriority(int pid, int ioPriority) => Mutate(pid, p => p.Io = ioPriority);
    public bool TrimWorkingSet(int pid) => Mutate(pid, p => p.Trims++);
    public uint GetMemoryLoadPercent() => MemoryLoad;
    public Guid GetActivePowerPlan() => PowerPlan;

    public bool SetActivePowerPlan(Guid scheme)
    {
        if (!KnownPlans.Contains(scheme))
        {
            return false;
        }

        PowerPlan = scheme;
        return true;
    }

    public int? GetForegroundProcessId() => Foreground;

    private static RunningProcess ToRunning(FakeProcess p) => new(p.Pid, p.Name, p.Path, p.StartTimeUtc);

    private bool Mutate(int pid, Action<FakeProcess> action)
    {
        if (!Processes.TryGetValue(pid, out FakeProcess? p))
        {
            return false;
        }

        action(p);
        return true;
    }
}

public sealed class FakeDisplays : RigTune.Core.Services.GameMode.IDisplayAccess
{
    public List<DisplayInfo> Displays { get; } = new();
    public List<(string Device, int Hz)> Changes { get; } = new();

    public IReadOnlyList<DisplayInfo> EnumerateDisplays() => Displays.ToList();

    public bool SetRefreshRate(string deviceName, int hz, out string message)
    {
        int i = Displays.FindIndex(d => d.DeviceName == deviceName);
        Displays[i] = Displays[i] with { CurrentHz = hz };
        Changes.Add((deviceName, hz));
        message = $"{deviceName} set to {hz} Hz.";
        return true;
    }
}

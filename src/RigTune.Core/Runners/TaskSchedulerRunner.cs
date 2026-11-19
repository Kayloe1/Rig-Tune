using System.Runtime.InteropServices;
using RigTune.Core.Platform;

namespace RigTune.Core.Runners;

/// <summary>Enables/disables scheduled tasks through the Task Scheduler 2.0 COM API.</summary>
public sealed class TaskSchedulerRunner : IStateRunner
{
    public string Kind => "schtask";

    public string? ReadState(string target)
    {
        dynamic? task = OpenTask(target);
        if (task is null)
        {
            return null;
        }

        try
        {
            return (bool)task.Enabled ? "Enabled" : "Disabled";
        }
        finally
        {
            Marshal.FinalReleaseComObject(task);
        }
    }

    public void WriteState(string target, string state)
    {
        dynamic task = OpenTask(target) ?? throw new InvalidOperationException($"Scheduled task {target} not found.");
        try
        {
            task.Enabled = string.Equals(state, "Enabled", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Marshal.FinalReleaseComObject(task);
        }
    }

    public string Describe(string target) => $"Scheduled task: {target}";

    private static dynamic? OpenTask(string path)
    {
        Type type = Type.GetTypeFromProgID("Schedule.Service")
                    ?? throw new PlatformNotSupportedException("Task Scheduler COM API unavailable.");
        dynamic service = Activator.CreateInstance(type)!;
        try
        {
            service.Connect();
            int slash = path.LastIndexOf('\\');
            string folderPath = slash <= 0 ? "\\" : path[..slash];
            string name = path[(slash + 1)..];
            // The connected service must outlive the returned task object, so it is left to the GC.
            dynamic folder = service.GetFolder(folderPath);
            return folder.GetTask(name);
        }
        catch (COMException)
        {
            return null; // folder or task missing
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }
}

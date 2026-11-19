using System.Runtime.InteropServices;
using System.Security;

namespace RigTune.Core.Services.GameMode;

/// <summary>
/// Start-with-Windows for an elevated app (SPEC §2): HKCU\Run is ignored for elevated processes, so a
/// Task Scheduler logon task <c>\RigTune\AutoStart</c> with RunLevel = Highest launches <c>--minimized</c>.
/// </summary>
public sealed class StartWithWindowsService
{
    public const string FolderName = "RigTune";
    public const string TaskName = "AutoStart";
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonInteractiveToken = 3;

    /// <summary>Task Scheduler 1.4 XML for the logon task (pure; unit-tested).</summary>
    public static string BuildTaskXml(string executablePath, string userId) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>Starts RigTune elevated in the system tray at sign-in.</Description>
            <URI>\{FolderName}\{TaskName}</URI>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
              <UserId>{SecurityElement.Escape(userId)}</UserId>
              <Delay>PT10S</Delay>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>{SecurityElement.Escape(userId)}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>7</Priority>
            <AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>false</StartWhenAvailable>
            <Enabled>true</Enabled>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>"{SecurityElement.Escape(executablePath)}"</Command>
              <Arguments>--minimized</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    public bool IsEnabled()
    {
        try
        {
            dynamic service = Connect();
            dynamic folder = service.GetFolder($"\\{FolderName}");
            dynamic task = folder.GetTask(TaskName);
            return task is not null;
        }
        catch (COMException)
        {
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    public void Enable(string executablePath)
    {
        dynamic service = Connect();
        dynamic root = service.GetFolder("\\");
        dynamic folder;
        try
        {
            folder = service.GetFolder($"\\{FolderName}");
        }
        catch (Exception ex) when (ex is COMException or FileNotFoundException)
        {
            folder = root.CreateFolder(FolderName);
        }

        string user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        folder.RegisterTask(TaskName, BuildTaskXml(executablePath, user), TaskCreateOrUpdate, null, null, TaskLogonInteractiveToken);
    }

    public void Disable()
    {
        try
        {
            dynamic folder = Connect().GetFolder($"\\{FolderName}");
            folder.DeleteTask(TaskName, 0);
        }
        catch (Exception ex) when (ex is COMException or FileNotFoundException)
        {
            // Already absent.
        }
    }

    private static dynamic Connect()
    {
        Type type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new PlatformNotSupportedException("Task Scheduler is unavailable.");
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service;
    }
}

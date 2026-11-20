using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using RigTune.Core.Models;
using RigTune.Core.Runners;

namespace RigTune.Core.Services.Uninstaller;

/// <summary>Starts an uninstaller and waits for it (and every process it spawns) to finish.</summary>
public interface IUninstallProcessRunner
{
    /// <param name="isComplete">Polled after the main process exits; true ends the wait early (e.g. the entry is gone
    /// but the uninstaller left a browser "feedback" page open in its process tree).</param>
    /// <returns>The main process's exit code.</returns>
    Task<int> RunAsync(UninstallCommand command, Func<bool> isComplete, CancellationToken cancellationToken);
}

/// <summary>Backs up and deletes leftover registry keys.</summary>
public interface IRegistryKeyRemover
{
    /// <summary>Exports the key (with subkeys) to a .reg file. Throws on failure.</summary>
    void Export(RegistryHiveName hive, RegistryViewName view, string keyPath, string file);

    void DeleteTree(RegistryHiveName hive, RegistryViewName view, string keyPath);
}

/// <summary>
/// Runs the uninstaller inside a Win32 job object (created suspended, so nothing escapes) and waits until
/// every process in the job has exited. NSIS and Inno uninstallers copy themselves to %TEMP% and exit
/// immediately, so waiting on the first process alone would report "done" while they are still running.
/// The uninstaller is never killed — cancelling only stops waiting.
/// </summary>
public sealed class JobObjectUninstallRunner : IUninstallProcessRunner
{
    private const uint CREATE_SUSPENDED = 0x00000004;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const int JobObjectBasicAccountingInformation = 1;
    private const uint INFINITE_EXIT = 259; // STILL_ACTIVE

    public async Task<int> RunAsync(UninstallCommand command, Func<bool> isComplete, CancellationToken cancellationToken)
    {
        string file = command.FileName.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe")
            : command.FileName;
        string commandLine = $"\"{file}\" {command.Arguments}".Trim();
        string? workingDir = Path.IsPathRooted(file) ? Path.GetDirectoryName(file) : null;

        using SafeJobHandle job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed.");
        }

        var startup = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        if (!CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT,
                IntPtr.Zero, workingDir is not null && Directory.Exists(workingDir) ? workingDir : null, ref startup, out PROCESS_INFORMATION pi))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not start {file}.");
        }

        try
        {
            // If assignment fails (rare: process already in a job that forbids nesting) fall back to the main process only.
            bool inJob = AssignProcessToJobObject(job, pi.hProcess);
            ResumeThread(pi.hThread);

            bool mainExited = false;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!mainExited && WaitForSingleObject(pi.hProcess, 0) == 0)
                {
                    mainExited = true;
                }

                if (mainExited && (!inJob || ActiveProcesses(job) == 0 || isComplete()))
                {
                    break;
                }

                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }

            GetExitCodeProcess(pi.hProcess, out uint exit);
            return exit == INFINITE_EXIT ? 0 : unchecked((int)exit);
        }
        finally
        {
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
        }
    }

    private static uint ActiveProcesses(SafeJobHandle job)
    {
        var info = new JOBOBJECT_BASIC_ACCOUNTING_INFORMATION();
        return QueryInformationJobObject(job, JobObjectBasicAccountingInformation, ref info,
            Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(), IntPtr.Zero)
            ? info.ActiveProcesses
            : 0;
    }

    private sealed class SafeJobHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle()
            : base(true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeJobHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeJobHandle job, int infoClass,
        ref JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info, int length, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string? applicationName, string commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, IntPtr environment,
        string? currentDirectory, ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>Backs keys up with <c>reg.exe export</c> (double-click the .reg to restore) and deletes them.</summary>
public sealed class WindowsRegistryKeyRemover : IRegistryKeyRemover
{
    public void Export(RegistryHiveName hive, RegistryViewName view, string keyPath, string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string viewSwitch = view switch
        {
            RegistryViewName.Registry32 => " /reg:32",
            RegistryViewName.Registry64 => " /reg:64",
            _ => string.Empty,
        };
        CommandResult result = ProcessRunner.Run("reg.exe", $"export \"{hive}\\{keyPath}\" \"{file}\" /y{viewSwitch}", TimeSpan.FromMinutes(1));
        if (!result.Succeeded() || !File.Exists(file))
        {
            throw new InvalidOperationException($"Backup failed ({result.Describe()}).");
        }
    }

    public void DeleteTree(RegistryHiveName hive, RegistryViewName view, string keyPath)
    {
        RegistryHive h = hive switch
        {
            RegistryHiveName.HKLM => RegistryHive.LocalMachine,
            RegistryHiveName.HKCU => RegistryHive.CurrentUser,
            _ => throw new NotSupportedException($"{hive} keys are never removed as leftovers."),
        };
        RegistryView v = view switch
        {
            RegistryViewName.Registry32 => RegistryView.Registry32,
            RegistryViewName.Registry64 => RegistryView.Registry64,
            _ => RegistryView.Default,
        };
        using RegistryKey root = RegistryKey.OpenBaseKey(h, v);
        root.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
    }
}

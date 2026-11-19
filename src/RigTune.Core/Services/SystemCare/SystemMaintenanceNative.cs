using System.Diagnostics;
using RigTune.Core.Native;
using RigTune.Core.Runners;

namespace RigTune.Core.Services.SystemCare;

/// <summary>Non-file cleanup primitives (DNS cache, memory lists). Abstracted for tests.</summary>
public interface ISystemMaintenanceNative
{
    /// <summary>Bytes currently held in the standby list, or null if unavailable.</summary>
    long? GetStandbyBytes();

    /// <summary>Trims every accessible process's working set. Returns the number of processes trimmed.</summary>
    int EmptyWorkingSets();

    bool PurgeStandbyList();

    bool FlushDnsCache();
}

public sealed class SystemMaintenanceNative : ISystemMaintenanceNative
{
    private const string ProfileSingleProcessPrivilege = "SeProfileSingleProcessPrivilege";
    private const string IncreaseQuotaPrivilege = "SeIncreaseQuotaPrivilege";

    public long? GetStandbyBytes()
    {
        MemoryApi.EnablePrivilege(ProfileSingleProcessPrivilege);
        // SYSTEM_MEMORY_LIST_INFORMATION: 5 ULONG_PTR counters, then PageCountByPriority[8] (the standby list).
        var buffer = new byte[IntPtr.Size * 22];
        if (MemoryApi.NtQuerySystemInformation(MemoryApi.SystemMemoryListInformation, buffer, buffer.Length, out _) != 0)
        {
            return null;
        }

        long pages = 0;
        for (int i = 0; i < 8; i++)
        {
            pages += IntPtr.Size == 8 ? BitConverter.ToInt64(buffer, (5 + i) * 8) : BitConverter.ToInt32(buffer, (5 + i) * 4);
        }

        return pages * Environment.SystemPageSize;
    }

    public int EmptyWorkingSets()
    {
        MemoryApi.EnablePrivilege(IncreaseQuotaPrivilege);
        int self = Environment.ProcessId, trimmed = 0;
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id is 0 or 4 || process.Id == self)
                {
                    continue;
                }

                try
                {
                    if (MemoryApi.EmptyWorkingSet(process.Handle))
                    {
                        trimmed++;
                    }
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // Protected / exited processes are skipped.
                }
            }
        }

        return trimmed;
    }

    public bool PurgeStandbyList()
    {
        MemoryApi.EnablePrivilege(ProfileSingleProcessPrivilege);
        int command = MemoryApi.MemoryPurgeStandbyList;
        return MemoryApi.NtSetSystemInformation(MemoryApi.SystemMemoryListInformation, ref command, sizeof(int)) == 0;
    }

    public bool FlushDnsCache()
    {
        try
        {
            if (MemoryApi.DnsFlushResolverCache())
            {
                return true;
            }
        }
        catch (EntryPointNotFoundException)
        {
        }

        return ProcessRunner.Run("ipconfig", "/flushdns").Succeeded();
    }
}

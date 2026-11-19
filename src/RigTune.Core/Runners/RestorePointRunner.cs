using System.ComponentModel;
using System.Management;
using RigTune.Core.Models;
using RigTune.Core.Native;
using RigTune.Core.Platform;

namespace RigTune.Core.Runners;

/// <summary>
/// Restore-point creation via <c>SRSetRestorePointW</c> with a WMI
/// <c>root\default:SystemRestore.CreateRestorePoint</c> fallback, and WMI enumeration.
/// </summary>
public sealed class RestorePointRunner : IRestorePointBackend
{
    private const int ErrorServiceDisabled = 1058;

    public RestorePointCreateResult Create(string description, RestorePointType type)
    {
        string desc = description.Length > SrClient.MAX_DESC_W - 1
            ? description[..(SrClient.MAX_DESC_W - 1)]
            : description;

        RestorePointCreateResult sr = TryCreateWithSrClient(desc, type);
        if (sr.Success)
        {
            return sr;
        }

        RestorePointCreateResult wmi = TryCreateWithWmi(desc, type);
        return wmi.Success
            ? wmi
            : new RestorePointCreateResult(false, $"{sr.Message} | {wmi.Message}", null, "srclient+wmi");
    }

    public IReadOnlyList<RestorePointInfo> Enumerate()
    {
        var points = new List<RestorePointInfo>();
        var scope = new ManagementScope(@"\\.\root\default");
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM SystemRestore"));
        using ManagementObjectCollection results = searcher.Get();
        foreach (ManagementBaseObject obj in results)
        {
            using (obj)
            {
                uint seq = Convert.ToUInt32(obj["SequenceNumber"] ?? 0u);
                string desc = obj["Description"] as string ?? string.Empty;
                int rpType = Convert.ToInt32(obj["RestorePointType"] ?? -1);
                DateTimeOffset? created = null;
                if (obj["CreationTime"] is string dmtf && dmtf.Length > 0)
                {
                    try
                    {
                        created = new DateTimeOffset(ManagementDateTimeConverter.ToDateTime(dmtf));
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                    }
                }

                points.Add(new RestorePointInfo(seq, desc, rpType, created));
            }
        }

        return points.OrderByDescending(p => p.SequenceNumber).ToList();
    }

    private static RestorePointCreateResult TryCreateWithSrClient(string description, RestorePointType type)
    {
        try
        {
            var begin = new SrClient.RESTOREPOINTINFOW
            {
                dwEventType = SrClient.BEGIN_SYSTEM_CHANGE,
                dwRestorePtType = (int)type,
                llSequenceNumber = 0,
                szDescription = description,
            };

            if (!SrClient.SRSetRestorePointW(ref begin, out SrClient.STATEMGRSTATUS status))
            {
                string reason = status.nStatus == ErrorServiceDisabled
                    ? "System Restore is turned off for the system drive (enable System Protection)."
                    : new Win32Exception(status.nStatus).Message;
                return new RestorePointCreateResult(false, $"SRSetRestorePointW failed: {reason}", null, "srclient");
            }

            var end = new SrClient.RESTOREPOINTINFOW
            {
                dwEventType = SrClient.END_SYSTEM_CHANGE,
                dwRestorePtType = (int)type,
                llSequenceNumber = status.llSequenceNumber,
                szDescription = description,
            };
            SrClient.SRSetRestorePointW(ref end, out _);

            return new RestorePointCreateResult(true, "Restore point created.", status.llSequenceNumber, "srclient");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return new RestorePointCreateResult(false, $"srclient.dll unavailable: {ex.Message}", null, "srclient");
        }
    }

    private static RestorePointCreateResult TryCreateWithWmi(string description, RestorePointType type)
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\default");
            using var cls = new ManagementClass(scope, new ManagementPath("SystemRestore"), null);
            using ManagementBaseObject inParams = cls.GetMethodParameters("CreateRestorePoint");
            inParams["Description"] = description;
            inParams["RestorePointType"] = (uint)type;
            inParams["EventType"] = (uint)SrClient.BEGIN_SYSTEM_CHANGE;
            using ManagementBaseObject outParams = cls.InvokeMethod("CreateRestorePoint", inParams, null);
            uint rv = Convert.ToUInt32(outParams["ReturnValue"] ?? 1u);
            return rv == 0
                ? new RestorePointCreateResult(true, "Restore point created (WMI).", null, "wmi")
                : new RestorePointCreateResult(false, $"WMI CreateRestorePoint returned {rv}: {new Win32Exception(unchecked((int)rv)).Message}", null, "wmi");
        }
        catch (ManagementException ex)
        {
            return new RestorePointCreateResult(false, $"WMI CreateRestorePoint failed: {ex.Message}", null, "wmi");
        }
    }
}

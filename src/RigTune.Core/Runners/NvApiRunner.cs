using RigTune.Core.Native;
using RigTune.Core.Platform;

namespace RigTune.Core.Runners;

/// <summary>Global (base) profile DRS access through nvapi64.dll.</summary>
public sealed unsafe class NvApiRunner : INvApiDrsAccess
{
    /// <summary>Shader Cache Size (DRS).</summary>
    public const uint ShaderCacheSizeSettingId = 0x00A06946;

    /// <summary>Power Management Mode (DRS).</summary>
    public const uint PowerManagementModeSettingId = 0x1057EB71;

    private static readonly object SessionLock = new();

    public bool IsAvailable => NvApi.EnsureInitialized();

    public NvDrsValue ReadGlobal(uint settingId) => WithBaseProfile(save: false, (session, profile) =>
    {
        var setting = new NvApi.NVDRS_SETTING { version = NvApi.SettingVersion1 };
        int status = NvApi.GetSetting(session, profile, settingId, &setting);
        if (status == NvApi.NVAPI_SETTING_NOT_FOUND)
        {
            return new NvDrsValue(false, null);
        }

        ThrowIfFailed(status, "NvAPI_DRS_GetSetting");
        uint value = *(uint*)setting.currentValue;
        bool inBase = setting.settingLocation == NvApi.NVDRS_CURRENT_PROFILE_LOCATION;
        return new NvDrsValue(inBase, value);
    });

    public void WriteGlobal(uint settingId, uint value) => WithBaseProfile(save: true, (session, profile) =>
    {
        var setting = new NvApi.NVDRS_SETTING
        {
            version = NvApi.SettingVersion1,
            settingId = settingId,
            settingType = NvApi.NVDRS_DWORD_TYPE,
        };
        *(uint*)setting.currentValue = value;
        ThrowIfFailed(NvApi.SetSetting(session, profile, &setting), "NvAPI_DRS_SetSetting");
        return true;
    });

    public void DeleteGlobal(uint settingId) => WithBaseProfile(save: true, (session, profile) =>
    {
        int status = NvApi.DeleteProfileSetting(session, profile, settingId);
        if (status != NvApi.NVAPI_SETTING_NOT_FOUND)
        {
            ThrowIfFailed(status, "NvAPI_DRS_DeleteProfileSetting");
        }

        return true;
    });

    private static T WithBaseProfile<T>(bool save, Func<IntPtr, IntPtr, T> action)
    {
        if (!NvApi.EnsureInitialized())
        {
            throw new InvalidOperationException("NVIDIA NVAPI (nvapi64.dll) is not available on this system.");
        }

        lock (SessionLock)
        {
            ThrowIfFailed(NvApi.CreateSession(out IntPtr session), "NvAPI_DRS_CreateSession");
            try
            {
                ThrowIfFailed(NvApi.LoadSettings(session), "NvAPI_DRS_LoadSettings");
                ThrowIfFailed(NvApi.GetBaseProfile(session, out IntPtr profile), "NvAPI_DRS_GetBaseProfile");
                T result = action(session, profile);
                if (save)
                {
                    ThrowIfFailed(NvApi.SaveSettings(session), "NvAPI_DRS_SaveSettings");
                }

                return result;
            }
            finally
            {
                NvApi.DestroySession(session);
            }
        }
    }

    private static void ThrowIfFailed(int status, string call)
    {
        if (status != NvApi.NVAPI_OK)
        {
            throw new InvalidOperationException($"{call} failed with NVAPI status {status}.");
        }
    }
}

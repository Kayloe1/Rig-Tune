using System.Runtime.InteropServices;
using RigTune.Core.Models;
using RigTune.Core.Native;

namespace RigTune.Core.Services;

/// <summary>
/// Read-only Core Audio / WASAPI inspection: active endpoints, shared-mode mix format and the
/// engine's default/minimum device periods (SPEC "Check Audio Latency Settings"). Changes nothing.
/// </summary>
public sealed class WasapiInspectService
{
    public Task<IReadOnlyList<AudioEndpointInfo>> InspectAsync() => Task.Run(Inspect);

    public IReadOnlyList<AudioEndpointInfo> Inspect()
    {
        var endpoints = new List<AudioEndpointInfo>();
        Type? type = Type.GetTypeFromCLSID(CoreAudio.MMDeviceEnumeratorClsid);
        if (type is null || Activator.CreateInstance(type) is not CoreAudio.IMMDeviceEnumerator enumerator)
        {
            return endpoints;
        }

        try
        {
            foreach (CoreAudio.EDataFlow flow in new[] { CoreAudio.EDataFlow.Render, CoreAudio.EDataFlow.Capture })
            {
                string? defaultId = null;
                if (enumerator.GetDefaultAudioEndpoint(flow, CoreAudio.ERole.Multimedia, out CoreAudio.IMMDevice def) == 0)
                {
                    def.GetId(out defaultId);
                    Marshal.ReleaseComObject(def);
                }

                if (enumerator.EnumAudioEndpoints(flow, CoreAudio.DEVICE_STATE_ACTIVE, out CoreAudio.IMMDeviceCollection collection) != 0)
                {
                    continue;
                }

                collection.GetCount(out uint count);
                for (uint i = 0; i < count; i++)
                {
                    if (collection.Item(i, out CoreAudio.IMMDevice device) != 0)
                    {
                        continue;
                    }

                    try
                    {
                        AudioEndpointInfo? info = Describe(device, flow, defaultId);
                        if (info is not null)
                        {
                            endpoints.Add(info);
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(device);
                    }
                }

                Marshal.ReleaseComObject(collection);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }

        return endpoints;
    }

    private static AudioEndpointInfo? Describe(CoreAudio.IMMDevice device, CoreAudio.EDataFlow flow, string? defaultId)
    {
        device.GetId(out string id);
        string name = ReadFriendlyName(device) ?? id;

        Guid iid = CoreAudio.IAudioClientIid;
        if (device.Activate(ref iid, CoreAudio.CLSCTX_ALL, IntPtr.Zero, out object clientObj) != 0 ||
            clientObj is not CoreAudio.IAudioClient client)
        {
            return null;
        }

        try
        {
            int rate = 0, channels = 0, bits = 0;
            if (client.GetMixFormat(out IntPtr format) == 0 && format != IntPtr.Zero)
            {
                channels = Marshal.ReadInt16(format, 2);
                rate = Marshal.ReadInt32(format, 4);
                bits = Marshal.ReadInt16(format, 14);
                Marshal.FreeCoTaskMem(format);
            }

            client.GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            return new AudioEndpointInfo(id, name, flow == CoreAudio.EDataFlow.Render ? "Playback" : "Recording",
                string.Equals(id, defaultId, StringComparison.Ordinal), rate, channels, bits,
                defaultPeriod / 10_000.0, minimumPeriod / 10_000.0);
        }
        finally
        {
            Marshal.ReleaseComObject(client);
        }
    }

    private static string? ReadFriendlyName(CoreAudio.IMMDevice device)
    {
        if (device.OpenPropertyStore(CoreAudio.STGM_READ, out CoreAudio.IPropertyStore store) != 0)
        {
            return null;
        }

        try
        {
            CoreAudio.PROPERTYKEY key = CoreAudio.FriendlyNameKey;
            if (store.GetValue(ref key, out CoreAudio.PROPVARIANT value) != 0)
            {
                return null;
            }

            try
            {
                return value.vt == CoreAudio.VT_LPWSTR ? Marshal.PtrToStringUni(value.pointerValue) : null;
            }
            finally
            {
                CoreAudio.PropVariantClear(ref value);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }
}

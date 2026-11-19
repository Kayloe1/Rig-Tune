using Microsoft.Win32;
using RigTune.Core.Models;
using RigTune.Core.Native;

namespace RigTune.Core.Services.GameMode;

/// <summary>Display enumeration and mode changes (abstracted for tests).</summary>
public interface IDisplayAccess
{
    IReadOnlyList<DisplayInfo> EnumerateDisplays();

    /// <summary>Applies a refresh rate at the current resolution, persisted to the registry.</summary>
    bool SetRefreshRate(string deviceName, int hz, out string message);
}

/// <summary>user32 implementation: EnumDisplayDevicesW + EnumDisplaySettingsExW + ChangeDisplaySettingsExW.</summary>
public sealed class DisplayRunner : IDisplayAccess
{
    public IReadOnlyList<DisplayInfo> EnumerateDisplays()
    {
        var displays = new List<DisplayInfo>();
        var adapter = DisplayApi.DISPLAY_DEVICE.Create();
        for (uint i = 0; DisplayApi.EnumDisplayDevices(null, i, ref adapter, 0); i++, adapter = DisplayApi.DISPLAY_DEVICE.Create())
        {
            if ((adapter.StateFlags & DisplayApi.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0)
            {
                continue;
            }

            var monitor = DisplayApi.DISPLAY_DEVICE.Create();
            bool hasMonitor = DisplayApi.EnumDisplayDevices(adapter.DeviceName, 0, ref monitor, DisplayApi.EDD_GET_DEVICE_INTERFACE_NAME);
            var current = DisplayApi.DEVMODE.Create();
            if (!DisplayApi.EnumDisplaySettingsEx(adapter.DeviceName, DisplayApi.ENUM_CURRENT_SETTINGS, ref current, 0))
            {
                continue;
            }

            var rates = new SortedSet<int>();
            var mode = DisplayApi.DEVMODE.Create();
            for (int m = 0; DisplayApi.EnumDisplaySettingsEx(adapter.DeviceName, m, ref mode, 0); m++, mode = DisplayApi.DEVMODE.Create())
            {
                if (mode.dmPelsWidth == current.dmPelsWidth && mode.dmPelsHeight == current.dmPelsHeight &&
                    mode.dmBitsPerPel == current.dmBitsPerPel && mode.dmDisplayFrequency > 1)
                {
                    rates.Add(mode.dmDisplayFrequency);
                }
            }

            displays.Add(new DisplayInfo(
                adapter.DeviceName,
                hasMonitor ? monitor.DeviceID : string.Empty,
                hasMonitor && !string.IsNullOrWhiteSpace(monitor.DeviceString) ? $"{monitor.DeviceString} ({adapter.DeviceString})" : adapter.DeviceString,
                (adapter.StateFlags & DisplayApi.DISPLAY_DEVICE_PRIMARY_DEVICE) != 0,
                current.dmPelsWidth,
                current.dmPelsHeight,
                current.dmBitsPerPel,
                current.dmDisplayFrequency,
                rates.ToList()));
        }

        return displays;
    }

    public bool SetRefreshRate(string deviceName, int hz, out string message)
    {
        var mode = DisplayApi.DEVMODE.Create();
        if (!DisplayApi.EnumDisplaySettingsEx(deviceName, DisplayApi.ENUM_CURRENT_SETTINGS, ref mode, 0))
        {
            message = "Could not read the current display mode.";
            return false;
        }

        mode.dmDisplayFrequency = hz;
        mode.dmFields = DisplayApi.DM_DISPLAYFREQUENCY;
        int result = DisplayApi.ChangeDisplaySettingsEx(deviceName, ref mode, IntPtr.Zero, DisplayApi.CDS_UPDATEREGISTRY, IntPtr.Zero);
        message = result switch
        {
            DisplayApi.DISP_CHANGE_SUCCESSFUL => $"{deviceName} set to {hz} Hz.",
            DisplayApi.DISP_CHANGE_RESTART => $"{deviceName} will use {hz} Hz after a restart.",
            _ => $"Windows rejected {hz} Hz on {deviceName} (code {result}).",
        };
        return result is DisplayApi.DISP_CHANGE_SUCCESSFUL or DisplayApi.DISP_CHANGE_RESTART;
    }
}

/// <summary>
/// Refresh Rate Watchdog (SPEC §3.3.6): users pin a target Hz per monitor; whenever display settings change
/// (and on app start / Game Mode start) any monitor below its target raises <see cref="DriftDetected"/>,
/// or is restored automatically when Auto-Restore is on.
/// </summary>
public sealed class RefreshRateWatchdogService : IDisposable
{
    private readonly IDisplayAccess _displays;
    private readonly SettingsService _settings;
    private readonly AuditLogService _audit;
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    private CancellationTokenSource? _debounce;
    private bool _listening;

    public RefreshRateWatchdogService(IDisplayAccess displays, SettingsService settings, AuditLogService audit)
    {
        _displays = displays;
        _settings = settings;
        _audit = audit;
    }

    /// <summary>Raised (on a background thread) with the monitors that dropped below their targets.</summary>
    public event EventHandler<IReadOnlyList<RefreshDrift>>? DriftDetected;

    /// <summary>Raised after every check with the full status list.</summary>
    public event EventHandler<IReadOnlyList<DisplayStatus>>? StatusChanged;

    public IReadOnlyList<DisplayStatus> LastStatus { get; private set; } = Array.Empty<DisplayStatus>();

    /// <summary>Monitors whose current rate is below their pinned target (1 Hz tolerance for 59.94/143.98-style rates).</summary>
    public static IReadOnlyList<RefreshDrift> FindDrift(IEnumerable<DisplayInfo> displays, IReadOnlyDictionary<string, int> targets) =>
        displays
            .Select(d => (Display: d, Target: TargetFor(d, targets)))
            .Where(x => x.Target is { } t && x.Display.CurrentHz + 1 < t)
            .Select(x => new RefreshDrift(x.Display, x.Target!.Value))
            .ToList();

    public static int? TargetFor(DisplayInfo display, IReadOnlyDictionary<string, int> targets) =>
        targets.TryGetValue(display.Key, out int t) || targets.TryGetValue(display.DeviceName, out t) ? t : null;

    public static IReadOnlyList<DisplayStatus> BuildStatus(IEnumerable<DisplayInfo> displays, IReadOnlyDictionary<string, int> targets)
    {
        HashSet<string> drifted = FindDrift(displays, targets).Select(d => d.Display.Key).ToHashSet();
        return displays.Select(d => new DisplayStatus(d, TargetFor(d, targets), drifted.Contains(d.Key))).ToList();
    }

    public void Start()
    {
        if (_listening)
        {
            return;
        }

        _listening = true;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public async Task<IReadOnlyList<RefreshDrift>> CheckAsync(string reason, CancellationToken cancellationToken = default)
    {
        await _checkLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AppSettings settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<DisplayInfo> displays = await Task.Run(_displays.EnumerateDisplays, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<RefreshDrift> drift = FindDrift(displays, settings.RefreshRateTargets);
            LastStatus = BuildStatus(displays, settings.RefreshRateTargets);
            StatusChanged?.Invoke(this, LastStatus);
            if (drift.Count == 0)
            {
                return drift;
            }

            await _audit.LogAsync(AuditAction.DriftDetected,
                $"Refresh rate dropped ({reason}): " + string.Join(", ", drift.Select(d => $"{d.Display.FriendlyName} {d.CurrentHz}→target {d.TargetHz} Hz")),
                "watchdog.refresh_rate", cancellationToken: CancellationToken.None).ConfigureAwait(false);

            if (settings.AutoRestoreRefreshRate)
            {
                await RestoreCoreAsync(drift).ConfigureAwait(false);
                displays = await Task.Run(_displays.EnumerateDisplays, cancellationToken).ConfigureAwait(false);
                LastStatus = BuildStatus(displays, settings.RefreshRateTargets);
                StatusChanged?.Invoke(this, LastStatus);
                return FindDrift(displays, settings.RefreshRateTargets);
            }

            DriftDetected?.Invoke(this, drift);
            return drift;
        }
        finally
        {
            _checkLock.Release();
        }
    }

    /// <summary>1-click Restore: sets every drifted monitor back to its pinned rate.</summary>
    public async Task<IReadOnlyList<string>> RestoreAllAsync(CancellationToken cancellationToken = default)
    {
        AppSettings settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<RefreshDrift> drift = FindDrift(await Task.Run(_displays.EnumerateDisplays, cancellationToken).ConfigureAwait(false),
            settings.RefreshRateTargets);
        IReadOnlyList<string> log = await RestoreCoreAsync(drift).ConfigureAwait(false);
        await CheckAsync("after restore", cancellationToken).ConfigureAwait(false);
        return log;
    }

    public async Task PinTargetAsync(DisplayInfo display, int? hz, CancellationToken cancellationToken = default)
    {
        await _settings.UpdateAsync(s =>
        {
            s.RefreshRateTargets.Remove(display.DeviceName);
            if (hz is { } value)
            {
                s.RefreshRateTargets[display.Key] = value;
            }
            else
            {
                s.RefreshRateTargets.Remove(display.Key);
            }
        }, cancellationToken).ConfigureAwait(false);
        await _audit.LogAsync(AuditAction.Info,
            hz is null ? $"Unpinned refresh rate for {display.FriendlyName}" : $"Pinned {display.FriendlyName} to {hz} Hz",
            "watchdog.refresh_rate", display.Key, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        await CheckAsync("target changed", cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<string>> RestoreCoreAsync(IReadOnlyList<RefreshDrift> drift)
    {
        var log = new List<string>();
        foreach (RefreshDrift d in drift)
        {
            int hz = d.Display.SupportedHz.Count == 0 || d.Display.SupportedHz.Contains(d.TargetHz)
                ? d.TargetHz
                : d.Display.SupportedHz.Where(r => r <= d.TargetHz).DefaultIfEmpty(d.Display.MaxHz).Max();
            bool ok = _displays.SetRefreshRate(d.Display.DeviceName, hz, out string message);
            log.Add(message);
            await _audit.LogAsync(ok ? AuditAction.Repaired : AuditAction.ApplyFailed,
                $"Refresh rate restore: {d.Display.FriendlyName} {d.CurrentHz} → {hz} Hz", "watchdog.refresh_rate", d.Display.Key,
                message, ok, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }

        return log;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // Mode switches arrive in bursts; wait for them to settle.
        _debounce?.Cancel();
        var cts = _debounce = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromSeconds(2), cts.Token).ContinueWith(
            async t =>
            {
                if (!t.IsCanceled)
                {
                    await CheckAsync("display settings changed").ConfigureAwait(false);
                }
            },
            TaskScheduler.Default);
    }

    public void Dispose()
    {
        if (_listening)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _listening = false;
        }

        _debounce?.Cancel();
    }
}

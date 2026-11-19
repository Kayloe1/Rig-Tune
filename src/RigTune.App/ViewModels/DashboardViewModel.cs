using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RigTune.App.Converters;
using RigTune.Core.Hardware;
using RigTune.Core.Models;
using RigTune.Core.Services;
using RigTune.Core.Services.GameMode;
using Wpf.Ui.Controls;

namespace RigTune.App.ViewModels;

public sealed partial class VolumeItem : ObservableObject
{
    public VolumeItem(string name) => Name = name;

    public string Name { get; }

    [ObservableProperty]
    private string _label = string.Empty;

    [ObservableProperty]
    private string _freeText = string.Empty;

    [ObservableProperty]
    private string _detail = string.Empty;

    [ObservableProperty]
    private double _usedPercent;

    public void Update(VolumeInfo v)
    {
        Label = string.IsNullOrWhiteSpace(v.Label) ? "Local Disk" : v.Label;
        FreeText = $"{Format.Bytes(v.FreeBytes)} free";
        Detail = $"{Format.Bytes(v.UsedBytes)} used of {Format.Bytes(v.TotalBytes)} · {v.Format}";
        UsedPercent = v.UsedPercent;
    }
}

public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly HostProfileDetector _detector;
    private readonly RestorePointService _restorePoints;
    private readonly AuditLogService _audit;
    private readonly SnapshotService _snapshots;
    private readonly DispatcherTimer _liveTimer;
    private readonly RefreshRateWatchdogService _watchdog;
    private readonly DriftDetectorService _drift;
    private readonly GameModeService _gameMode;
    private bool _sampling;
    private bool _loaded;

    public DashboardViewModel(HostProfileDetector detector, RestorePointService restorePoints, AuditLogService audit,
        SnapshotService snapshots, RefreshRateWatchdogService watchdog, GameModeService gameMode, DriftDetectorService drift)
    {
        _drift = drift;
        _drift.ScanCompleted += (_, r) => App.Current?.Dispatcher.InvokeAsync(() => UpdateDrift(r));
        if (_drift.LastResult is { } last)
        {
            UpdateDrift(last);
        }

        _watchdog = watchdog;
        _gameMode = gameMode;
        _watchdog.StatusChanged += (_, status) => App.Current?.Dispatcher.InvokeAsync(() => UpdateWatchdog(status));
        _gameMode.SessionChanged += (_, _) => App.Current?.Dispatcher.InvokeAsync(UpdateGameMode);
        UpdateWatchdog(_watchdog.LastStatus);
        UpdateGameMode();
        _detector = detector;
        _restorePoints = restorePoints;
        _audit = audit;
        _snapshots = snapshots;
        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _liveTimer.Tick += async (_, _) => await SampleLiveAsync();
        _audit.EntryAdded += (_, _) => App.Current?.Dispatcher.InvokeAsync(LoadHistoryAsync);
        _snapshots.SnapshotsChanged += (_, _) => App.Current?.Dispatcher.InvokeAsync(LoadSnapshotCountAsync);
    }

    // ---- Host profile -------------------------------------------------------

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private string _machineName = string.Empty;

    [ObservableProperty]
    private string _osTitle = "Detecting…";

    [ObservableProperty]
    private string _osDetail = string.Empty;

    [ObservableProperty]
    private string _cpuName = "Detecting…";

    [ObservableProperty]
    private string _cpuTopologyText = string.Empty;

    [ObservableProperty]
    private string _pCoreRange = "—";

    [ObservableProperty]
    private string _eCoreRange = "—";

    [ObservableProperty]
    private string _microcode = "—";

    [ObservableProperty]
    private string _microcodeDetail = string.Empty;

    [ObservableProperty]
    private string _gpuName = "Detecting…";

    [ObservableProperty]
    private string _gpuDetail = string.Empty;

    [ObservableProperty]
    private string _memoryTotal = "—";

    [ObservableProperty]
    private string _virtualization = string.Empty;

    [ObservableProperty]
    private string _physicalDisks = string.Empty;

    // ---- Live stats ---------------------------------------------------------

    [ObservableProperty]
    private double _cpuUsage;

    [ObservableProperty]
    private string _cpuUsageText = "—";

    [ObservableProperty]
    private double _memoryLoad;

    [ObservableProperty]
    private string _memoryLoadText = "—";

    [ObservableProperty]
    private string _memoryDetail = string.Empty;

    [ObservableProperty]
    private string _uptime = "—";

    public ObservableCollection<VolumeItem> Volumes { get; } = new();

    // ---- Restore points / history ------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateRestorePointCommand))]
    private bool _isCreatingRestorePoint;

    [ObservableProperty]
    private string _restorePointLabel = string.Empty;

    [ObservableProperty]
    private bool _isRestorePointStatusOpen;

    [ObservableProperty]
    private string _restorePointStatusTitle = string.Empty;

    [ObservableProperty]
    private string _restorePointStatus = string.Empty;

    [ObservableProperty]
    private InfoBarSeverity _restorePointSeverity = InfoBarSeverity.Informational;

    [ObservableProperty]
    private string _restorePointListStatus = "Loading restore points…";

    [ObservableProperty]
    private int _activeSnapshotCount;

    public ObservableCollection<RestorePointInfo> RestorePoints { get; } = new();

    // ---- Watchdog / Game Mode cards ----------------------------------------

    [ObservableProperty]
    private string _watchdogText = "Checking displays…";

    [ObservableProperty]
    private bool _isRefreshDrifted;

    [ObservableProperty]
    private string _gameModeText = "Idle";

    [ObservableProperty]
    private string _gameModeDetail = string.Empty;

    private void UpdateWatchdog(IReadOnlyList<DisplayStatus> status)
    {
        IsRefreshDrifted = status.Any(s => s.IsDrifted);
        WatchdogText = status.Count == 0
            ? "No displays detected yet"
            : string.Join("\n", status.Select(s =>
                $"{s.Display.FriendlyName}: {s.Display.CurrentHz} Hz{(s.TargetHz is { } t ? $" (target {t} Hz{(s.IsDrifted ? " — BELOW TARGET" : " ✓")})" : " (not pinned)")}"));
    }

    private void UpdateGameMode()
    {
        GameSession? s = _gameMode.ActiveSession;
        GameModeText = s is null ? "Idle" : $"Active — {s.GameName ?? "manual"}";
        GameModeDetail = s is null
            ? (_gameMode.IsWatching ? "Waiting for a game to launch." : "Auto-detect is off.")
            : $"Since {s.StartedAtUtc.ToLocalTime():HH:mm} · {s.Throttled.Count} background process(es) on EcoQoS / lower priority";
    }

    [RelayCommand]
    private Task RestoreRefreshAsync() => _watchdog.RestoreAllAsync();

    // ---- Drift card ----------------------------------------------------------

    [ObservableProperty]
    private string _driftText = "Not checked yet";

    [ObservableProperty]
    private string _driftDetail = "Compares every active tweak with the live system.";

    [ObservableProperty]
    private bool _hasDrift;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckDriftCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReapplyDriftCommand))]
    private bool _isCheckingDrift;

    private void UpdateDrift(DriftScanResult r)
    {
        HasDrift = r.Drifted.Count > 0;
        DriftText = r.ActiveCount == 0 ? "No active tweaks" : HasDrift ? $"{r.Drifted.Count} drifted" : "No drift";
        DriftDetail = $"{r.ActiveCount} active change(s) · checked {r.ScannedAt.LocalDateTime:HH:mm}" +
                      (HasDrift ? " · " + string.Join(", ", r.Drifted.Take(3).Select(d => d.TweakName)) + (r.Drifted.Count > 3 ? "…" : string.Empty) : string.Empty);
    }

    private bool CanDrift() => !IsCheckingDrift;

    [RelayCommand(CanExecute = nameof(CanDrift))]
    private async Task CheckDriftAsync()
    {
        IsCheckingDrift = true;
        try
        {
            UpdateDrift(await _drift.ScanAsync());
        }
        finally
        {
            IsCheckingDrift = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDrift))]
    private async Task ReapplyDriftAsync()
    {
        IsCheckingDrift = true;
        try
        {
            await _drift.ReapplyAllDriftedAsync();
            if (_drift.LastResult is { } r)
            {
                UpdateDrift(r);
            }
        }
        finally
        {
            IsCheckingDrift = false;
        }
    }

    public ObservableCollection<AuditEntry> RecentHistory { get; } = new();

    public async Task OnNavigatedToAsync()
    {
        _liveTimer.Start();
        if (!_loaded)
        {
            _loaded = true;
            await RefreshAsync();
        }
        else
        {
            await SampleLiveAsync();
        }
    }

    public void OnNavigatedFrom() => _liveTimer.Stop();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            HostProfile profile = await _detector.DetectAsync();
            ApplyProfile(profile);
        }
        finally
        {
            IsLoading = false;
        }

        await SampleLiveAsync();
        await Task.WhenAll(LoadRestorePointsAsync(), LoadHistoryAsync(), LoadSnapshotCountAsync());
    }

    private bool CanCreateRestorePoint() => !IsCreatingRestorePoint;

    [RelayCommand(CanExecute = nameof(CanCreateRestorePoint))]
    private async Task CreateRestorePointAsync()
    {
        IsCreatingRestorePoint = true;
        IsRestorePointStatusOpen = true;
        RestorePointSeverity = InfoBarSeverity.Informational;
        RestorePointStatusTitle = "Creating restore point";
        RestorePointStatus = "Windows is snapshotting the system drive. This can take a minute…";
        try
        {
            RestorePointCreateResult result = await _restorePoints.CreateAsync(RestorePointLabel);
            RestorePointSeverity = result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error;
            RestorePointStatusTitle = result.Success ? "Restore point created" : "Restore point failed";
            RestorePointStatus = result.Success
                ? $"{result.Message} The 24-hour cooldown is bypassed, so you can create another right away."
                : result.Message;
            if (result.Success)
            {
                RestorePointLabel = string.Empty;
            }
        }
        finally
        {
            IsCreatingRestorePoint = false;
        }

        await LoadRestorePointsAsync();
    }

    [RelayCommand]
    private static void OpenSystemRestore() => RestorePointService.OpenSystemRestoreWizard();

    private void ApplyProfile(HostProfile p)
    {
        MachineName = p.MachineName;
        OsTitle = $"{p.Os.ProductName}";
        OsDetail = $"Version {p.Os.DisplayVersion} · Build {p.Os.BuildString} · {p.Os.Architecture}";

        CpuName = p.Cpu.Name;
        CpuTopology topo = p.Cpu.Topology;
        CpuTopologyText = topo.PhysicalCoreCount == 0
            ? $"{Environment.ProcessorCount} logical processors"
            : topo.IsHybrid
                ? $"{topo.PhysicalCoreCount} cores: {topo.PerformanceCoreCount} P-cores + {topo.EfficientCoreCount} E-cores · {topo.LogicalProcessorCount} threads"
                : $"{topo.PhysicalCoreCount} cores · {topo.LogicalProcessorCount} threads";
        PCoreRange = FormatRanges(topo.PerformanceLogicalProcessors);
        ECoreRange = topo.IsHybrid ? FormatRanges(topo.EfficientLogicalProcessors) : "—";
        Microcode = p.Cpu.MicrocodeDisplay;
        MicrocodeDetail = p.Cpu.PreviousMicrocodeRevision is { } prev && prev != p.Cpu.MicrocodeRevision
            ? $"Active revision (BIOS/firmware shipped 0x{prev:X})"
            : "Active revision (read-only, from CentralProcessor\\0)";

        GpuInfo? gpu = p.Gpus.FirstOrDefault();
        GpuName = gpu?.Name ?? "No GPU detected";
        GpuDetail = gpu is null
            ? string.Empty
            : string.Join(" · ", new[]
            {
                gpu.DedicatedMemoryBytes is { } vram ? $"{Format.Bytes(vram)} VRAM" : null,
                gpu.VendorDriverVersion is { } nv ? $"Driver {nv}" : $"Driver {gpu.DriverVersion}",
                gpu.DriverDate?.ToString("yyyy-MM-dd"),
            }.Where(s => !string.IsNullOrEmpty(s)));

        MemoryTotal = Format.Bytes(p.Memory.TotalBytes);
        Virtualization = p.HypervisorPresent switch
        {
            true => "Hypervisor present (Hyper-V / VBS / WSL2)",
            false => "No hypervisor",
            null => "Hypervisor status unknown",
        };
        PhysicalDisks = p.PhysicalDisks.Count == 0
            ? string.Empty
            : string.Join("\n", p.PhysicalDisks.Select(d => $"{d.FriendlyName} · {d.BusType} {d.MediaType} · {Format.Bytes(d.SizeBytes)}"));

        UpdateVolumes(p.Volumes);
    }

    private static string FormatRanges(IReadOnlyList<int> indices) => CpuTopology.FormatRanges(indices);

    private async Task SampleLiveAsync()
    {
        if (_sampling)
        {
            return;
        }

        _sampling = true;
        try
        {
            HostLiveStats live = await Task.Run(_detector.SampleLiveStats);
            CpuUsage = live.CpuUsagePercent;
            CpuUsageText = $"{live.CpuUsagePercent:0}%";
            MemoryLoad = live.Memory.LoadPercent;
            MemoryLoadText = $"{live.Memory.LoadPercent}%";
            MemoryDetail = $"{Format.Bytes(live.Memory.TotalBytes - live.Memory.AvailableBytes)} used · {Format.Bytes(live.Memory.AvailableBytes)} available";
            Uptime = Format.Uptime(live.Uptime);
            UpdateVolumes(live.Volumes);
        }
        finally
        {
            _sampling = false;
        }
    }

    private void UpdateVolumes(IReadOnlyList<VolumeInfo> volumes)
    {
        foreach (VolumeInfo v in volumes)
        {
            VolumeItem? item = Volumes.FirstOrDefault(x => x.Name == v.Name);
            if (item is null)
            {
                item = new VolumeItem(v.Name);
                Volumes.Add(item);
            }

            item.Update(v);
        }

        foreach (VolumeItem stale in Volumes.Where(x => volumes.All(v => v.Name != x.Name)).ToList())
        {
            Volumes.Remove(stale);
        }
    }

    private async Task LoadRestorePointsAsync()
    {
        try
        {
            IReadOnlyList<RestorePointInfo> points = await _restorePoints.EnumerateAsync();
            RestorePoints.Clear();
            foreach (RestorePointInfo point in points.Take(6))
            {
                RestorePoints.Add(point);
            }

            RestorePointListStatus = points.Count == 0
                ? "No restore points found (System Protection may be off for C:)."
                : $"{points.Count} restore point{(points.Count == 1 ? string.Empty : "s")} on this PC";
        }
        catch (Exception ex)
        {
            RestorePointListStatus = $"Could not enumerate restore points: {ex.Message}";
        }
    }

    private async Task LoadHistoryAsync()
    {
        IReadOnlyList<AuditEntry> recent = await _audit.GetRecentAsync(8);
        RecentHistory.Clear();
        foreach (AuditEntry entry in recent)
        {
            RecentHistory.Add(entry);
        }
    }

    private async Task LoadSnapshotCountAsync() =>
        ActiveSnapshotCount = (await _snapshots.GetActiveAsync()).Count;
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RigTune.App.Converters;
using RigTune.App.Services;
using RigTune.Core.Models;
using RigTune.Core.Services;
using RigTune.Core.Services.AppxWinget;
using RigTune.Core.Services.SystemCare;
using Wpf.Ui.Controls;

namespace RigTune.App.ViewModels;

/// <summary>Shared InfoBar state for each tab.</summary>
public abstract partial class StatusViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isStatusOpen;

    [ObservableProperty]
    private string _statusTitle = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;

    [ObservableProperty]
    private bool _isBusy;

    protected void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusSeverity = severity;
        StatusTitle = title;
        StatusMessage = message;
        IsStatusOpen = true;
    }

    protected static void OnUi(Action action)
    {
        if (Application.Current?.Dispatcher is { } d && !d.CheckAccess())
        {
            d.Invoke(action);
        }
        else
        {
            action();
        }
    }
}

public sealed partial class SystemCareViewModel : ObservableObject
{
    public SystemCareViewModel(RestorePointsTabViewModel restorePoints, CleanupTabViewModel cleanup,
        StartupTabViewModel startup, AppxWingetTabViewModel appx, RepairTabViewModel repair)
    {
        RestorePoints = restorePoints;
        Cleanup = cleanup;
        Startup = startup;
        Appx = appx;
        Repair = repair;
    }

    public RestorePointsTabViewModel RestorePoints { get; }
    public CleanupTabViewModel Cleanup { get; }
    public StartupTabViewModel Startup { get; }
    public AppxWingetTabViewModel Appx { get; }
    public RepairTabViewModel Repair { get; }

    [ObservableProperty]
    private int _selectedTab;

    partial void OnSelectedTabChanged(int value) => _ = LoadTabAsync(value);

    public Task LoadTabAsync(int index) => index switch
    {
        0 => RestorePoints.EnsureLoadedAsync(),
        1 => Cleanup.EnsureLoadedAsync(),
        2 => Startup.EnsureLoadedAsync(),
        3 => Appx.EnsureLoadedAsync(),
        _ => Task.CompletedTask, // Repair is status-first but only scans on request
    };
}

// ====================================================================== Restore points

public sealed partial class RestorePointsTabViewModel : StatusViewModel
{
    private readonly RestorePointService _service;
    private bool _loaded;

    public RestorePointsTabViewModel(RestorePointService service) => _service = service;

    public ObservableCollection<RestorePointInfo> Points { get; } = new();

    [ObservableProperty]
    private string _label = string.Empty;

    public async Task EnsureLoadedAsync()
    {
        if (!_loaded)
        {
            _loaded = true;
            await RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            IReadOnlyList<RestorePointInfo> points = await _service.EnumerateAsync();
            Points.Clear();
            foreach (RestorePointInfo p in points)
            {
                Points.Add(p);
            }
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Could not list restore points", ex.Message);
        }
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        IsBusy = true;
        ShowStatus(InfoBarSeverity.Informational, "Creating restore point…", "Windows is snapshotting the system drive.");
        try
        {
            RestorePointCreateResult r = await _service.CreateAsync(Label);
            ShowStatus(r.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error,
                r.Success ? "Restore point created" : "Restore point failed", r.Message);
            if (r.Success)
            {
                Label = string.Empty;
            }
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
    }

    [RelayCommand]
    private static void OpenSystemRestore() => RestorePointService.OpenSystemRestoreWizard();
}

// ====================================================================== Cleanup

public sealed partial class CleanupItemViewModel : ObservableObject
{
    public CleanupItemViewModel(CleanupDefinition definition)
    {
        Definition = definition;
        _isSelected = definition.Kind != CleanupKind.Memory; // RAM trim is opt-in
    }

    public CleanupDefinition Definition { get; }
    public string Name => Definition.Name;
    public string Description => Definition.Description;
    public string PermanenceTag => Definition.IsPermanent ? "Permanent" : "Rebuilt automatically";

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _estimateText = "Not scanned";

    [ObservableProperty]
    private long _estimateBytes;

    [ObservableProperty]
    private string _resultText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;
}

public sealed partial class CleanupTabViewModel : StatusViewModel
{
    private readonly CleanupEngine _engine;
    private readonly DialogService _dialogs;
    private bool _loaded;

    public CleanupTabViewModel(CleanupEngine engine, DialogService dialogs)
    {
        _engine = engine;
        _dialogs = dialogs;
        foreach (CleanupDefinition d in engine.Definitions)
        {
            Items.Add(new CleanupItemViewModel(d));
        }
    }

    public ObservableCollection<CleanupItemViewModel> Items { get; } = new();

    [ObservableProperty]
    private string _totalText = string.Empty;

    public async Task EnsureLoadedAsync()
    {
        if (!_loaded)
        {
            _loaded = true;
            await ScanAsync();
        }
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        IsBusy = true;
        try
        {
            foreach (CleanupItemViewModel item in Items)
            {
                item.IsBusy = true;
                try
                {
                    CleanupEstimate e = await _engine.EstimateAsync(item.Definition.Id);
                    item.EstimateBytes = e.Bytes;
                    item.EstimateText = e.Note is { } note && e.Bytes == 0
                        ? note
                        : $"{Format.Bytes(e.Bytes)}{(e.Files > 0 ? $" · {e.Files:N0} files" : string.Empty)}{(e.Note is null ? string.Empty : $" · {e.Note}")}";
                }
                catch (Exception ex)
                {
                    item.EstimateText = $"Scan failed: {ex.Message}";
                }
                finally
                {
                    item.IsBusy = false;
                }
            }

            UpdateTotal();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdateTotal() =>
        TotalText = $"Estimated reclaimable (selected, on disk): {Format.Bytes(Items.Where(i => i.IsSelected && i.Definition.Kind == CleanupKind.Files).Sum(i => i.EstimateBytes))}";

    [RelayCommand]
    private async Task CleanSelectedAsync()
    {
        List<CleanupItemViewModel> selected = Items.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0)
        {
            ShowStatus(InfoBarSeverity.Informational, "Nothing selected", "Tick the cleanup actions you want to run.");
            return;
        }

        string list = string.Join("\n", selected.Select(i => $"• {i.Name} — {i.EstimateText}{(i.Definition.IsPermanent ? " (permanent)" : string.Empty)}"));
        if (!await _dialogs.ConfirmAsync("Run cleanup?", $"RigTune will run:\n{list}\n\nFiles in use are skipped.", "Clean now"))
        {
            return;
        }

        IsBusy = true;
        long freed = 0;
        try
        {
            foreach (CleanupItemViewModel item in selected)
            {
                item.IsBusy = true;
                try
                {
                    CleanupResult r = await _engine.CleanAsync(item.Definition.Id);
                    item.ResultText = r.Message;
                    freed += r.BytesFreed;
                }
                catch (Exception ex)
                {
                    item.ResultText = $"Failed: {ex.Message}";
                }
                finally
                {
                    item.IsBusy = false;
                }
            }
        }
        finally
        {
            IsBusy = false;
        }

        ShowStatus(InfoBarSeverity.Success, "Cleanup finished", $"Freed about {Format.Bytes(freed)}. Each action was recorded in the audit log.");
        await ScanAsync();
    }
}

// ====================================================================== Startup apps

public sealed partial class StartupItemViewModel : ObservableObject
{
    private readonly Func<StartupItemViewModel, bool, Task<bool>> _toggle;
    private bool _suppress;

    public StartupItemViewModel(StartupEntry entry, Func<StartupItemViewModel, bool, Task<bool>> toggle)
    {
        Entry = entry;
        _toggle = toggle;
        _isEnabled = entry.IsEnabled;
    }

    public StartupEntry Entry { get; }
    public string Name => Entry.Name;
    public string Command => Entry.Command;
    public string Publisher => Entry.Publisher ?? "Unknown publisher";
    public string Location => Entry.LocationDisplay;

    [ObservableProperty]
    private bool _isEnabled;

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_suppress)
        {
            _ = ApplyAsync(value);
        }
    }

    private async Task ApplyAsync(bool value)
    {
        if (!await _toggle(this, value))
        {
            _suppress = true;
            IsEnabled = !value; // roll the switch back
            _suppress = false;
        }
    }
}

public sealed partial class StartupTabViewModel : StatusViewModel
{
    private readonly StartupManager _manager;
    private bool _loaded;

    public StartupTabViewModel(StartupManager manager) => _manager = manager;

    public ObservableCollection<StartupItemViewModel> Items { get; } = new();

    [ObservableProperty]
    private string _summary = string.Empty;

    public async Task EnsureLoadedAsync()
    {
        if (!_loaded)
        {
            _loaded = true;
            await RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            IReadOnlyList<StartupEntry> entries = await _manager.EnumerateAsync();
            Items.Clear();
            foreach (StartupEntry e in entries)
            {
                Items.Add(new StartupItemViewModel(e, ToggleAsync));
            }

            Summary = $"{entries.Count} startup entries · {entries.Count(e => e.IsEnabled)} enabled";
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Could not read startup apps", ex.Message);
        }
    }

    private async Task<bool> ToggleAsync(StartupItemViewModel item, bool enabled)
    {
        try
        {
            await _manager.SetEnabledAsync(item.Entry, enabled);
            ShowStatus(InfoBarSeverity.Success, $"{item.Name} {(enabled ? "enabled" : "disabled")}",
                "Saved to StartupApproved (Task Manager shows the same state). The original entry is untouched.");
            Summary = $"{Items.Count} startup entries · {Items.Count(i => i.IsEnabled)} enabled";
            return true;
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, $"Could not change {item.Name}", ex.Message);
            return false;
        }
    }

    [RelayCommand]
    private static void OpenTaskManager() => Process.Start(new ProcessStartInfo("taskmgr.exe", "/0 /startup") { UseShellExecute = true });
}

// ====================================================================== Appx + winget

public sealed partial class AppxItemViewModel : ObservableObject
{
    public AppxItemViewModel(AppxPackageClassified classified) => Classified = classified;

    public AppxPackageClassified Classified { get; }
    public string DisplayName => Classified.Package.DisplayName;
    public string Name => Classified.Package.Name;
    public string Version => Classified.Package.Version;
    public string Reason => Classified.Reason;
    public bool IsProtected => Classified.IsProtected;

    public string GroupLabel => Classified.Category switch
    {
        AppxCategory.SafeToRemove => "1 · Safe to Remove",
        AppxCategory.Optional => "2 · Optional",
        AppxCategory.Other => "3 · Other installed apps",
        _ => "4 · System-Critical / Protected",
    };

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isLocked;

    [ObservableProperty]
    private string _resultText = string.Empty;
}

public sealed partial class WingetItemViewModel : ObservableObject
{
    public WingetItemViewModel(WingetUpgrade upgrade)
    {
        Upgrade = upgrade;
        _isSelected = !upgrade.RequiresExplicitTargeting;
    }

    public WingetUpgrade Upgrade { get; }
    public string Name => Upgrade.Name;
    public string Id => Upgrade.Id;
    public string Version => Upgrade.Version;
    public string Available => Upgrade.Available;
    public string Note => Upgrade.RequiresExplicitTargeting ? "Requires explicit targeting" : Upgrade.Source;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _status = string.Empty;
}

public sealed partial class AppxWingetTabViewModel : StatusViewModel
{
    private readonly AppxWingetService _service;
    private readonly SettingsService _settings;
    private readonly DialogService _dialogs;
    private readonly StringBuilder _console = new();
    private bool _loaded;

    public AppxWingetTabViewModel(AppxWingetService service, SettingsService settings, DialogService dialogs)
    {
        _service = service;
        _settings = settings;
        _dialogs = dialogs;
        PackagesView = CollectionViewSource.GetDefaultView(Packages);
        PackagesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(AppxItemViewModel.GroupLabel)));
        PackagesView.SortDescriptions.Add(new SortDescription(nameof(AppxItemViewModel.GroupLabel), ListSortDirection.Ascending));
        PackagesView.SortDescriptions.Add(new SortDescription(nameof(AppxItemViewModel.DisplayName), ListSortDirection.Ascending));
        PackagesView.Filter = o => o is AppxItemViewModel p && (ShowProtected || !p.IsProtected);
    }

    public ObservableCollection<AppxItemViewModel> Packages { get; } = new();
    public ICollectionView PackagesView { get; }
    public ObservableCollection<WingetItemViewModel> Upgrades { get; } = new();

    [ObservableProperty]
    private bool _deprovision;

    [ObservableProperty]
    private bool _showProtected;

    [ObservableProperty]
    private bool _isDangerZoneEnabled;

    [ObservableProperty]
    private string _consoleText = string.Empty;

    [ObservableProperty]
    private string _wingetSummary = "Click \"Check for updates\" to run winget upgrade --include-unknown.";

    partial void OnShowProtectedChanged(bool value) => PackagesView.Refresh();

    public async Task EnsureLoadedAsync()
    {
        if (!_loaded)
        {
            _loaded = true;
            await RefreshPackagesAsync();
        }
    }

    [RelayCommand]
    private async Task RefreshPackagesAsync()
    {
        IsBusy = true;
        try
        {
            IsDangerZoneEnabled = (await _settings.GetAsync()).DangerZoneEnabled;
            IReadOnlyList<AppxPackageClassified> list = await _service.ListPackagesAsync();
            Packages.Clear();
            foreach (AppxPackageClassified p in list)
            {
                Packages.Add(new AppxItemViewModel(p) { IsLocked = p.IsProtected && !IsDangerZoneEnabled });
            }
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Could not list Appx packages", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RemoveSelectedAsync()
    {
        List<AppxItemViewModel> selected = Packages.Where(p => p.IsSelected && !p.IsLocked).ToList();
        if (selected.Count == 0)
        {
            ShowStatus(InfoBarSeverity.Informational, "Nothing selected", "Tick the packages to remove. Protected packages are locked unless the Danger Zone is on.");
            return;
        }

        string list = string.Join("\n", selected.Select(p => $"• {p.DisplayName}{(p.IsProtected ? "  ⚠ PROTECTED" : string.Empty)}"));
        if (!await _dialogs.ConfirmAsync("Remove packages?",
                $"{list}\n\nRemoved for the current user{(Deprovision ? " and de-provisioned so new user accounts don't get them" : string.Empty)}. Most can be reinstalled from the Microsoft Store.",
                "Remove", danger: selected.Any(p => p.IsProtected)))
        {
            return;
        }

        IsBusy = true;
        int ok = 0;
        try
        {
            foreach (AppxItemViewModel item in selected)
            {
                AppxRemovalResult r = await _service.RemoveAsync(item.Classified.Package, Deprovision);
                item.ResultText = r.Message;
                ok += r.Success ? 1 : 0;
            }
        }
        finally
        {
            IsBusy = false;
        }

        ShowStatus(ok == selected.Count ? InfoBarSeverity.Success : InfoBarSeverity.Warning, "Appx removal finished",
            $"{ok} of {selected.Count} removed.");
        await RefreshPackagesAsync();
    }

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        if (!_service.IsWingetAvailable)
        {
            WingetSummary = "winget (App Installer) was not found. Install or update \"App Installer\" from the Microsoft Store.";
            return;
        }

        IsBusy = true;
        WingetSummary = "Running winget upgrade --include-unknown…";
        try
        {
            IReadOnlyList<WingetUpgrade> upgrades = await _service.ListUpgradesAsync();
            Upgrades.Clear();
            foreach (WingetUpgrade u in upgrades)
            {
                Upgrades.Add(new WingetItemViewModel(u));
            }

            WingetSummary = upgrades.Count == 0 ? "Everything is up to date." : $"{upgrades.Count} upgrade(s) available.";
        }
        catch (Exception ex)
        {
            WingetSummary = $"winget failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task UpgradeSelectedAsync() => UpgradeAsync(Upgrades.Where(u => u.IsSelected).ToList());

    [RelayCommand]
    private Task UpgradeAllAsync() => UpgradeAsync(Upgrades.Where(u => !u.Upgrade.RequiresExplicitTargeting).ToList());

    private async Task UpgradeAsync(List<WingetItemViewModel> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        IsBusy = true;
        int ok = 0;
        try
        {
            foreach (WingetItemViewModel item in items)
            {
                item.Status = "Upgrading…";
                WingetResult r = await Task.Run(() => _service.UpgradeAsync(item.Upgrade, AppendConsole));
                item.Status = r.Success ? "Upgraded" : $"Failed (0x{r.ExitCode:X8})";
                ok += r.Success ? 1 : 0;
            }
        }
        finally
        {
            IsBusy = false;
        }

        WingetSummary = $"{ok} of {items.Count} upgraded. See the console for details.";
    }

    private void AppendConsole(string line)
    {
        string clean = line.Contains('\r') ? line[(line.LastIndexOf('\r') + 1)..] : line;
        if (string.IsNullOrWhiteSpace(clean) || clean.Trim() is "-" or "\\" or "|" or "/")
        {
            return;
        }

        OnUi(() =>
        {
            _console.AppendLine(clean.TrimEnd());
            ConsoleText = _console.ToString();
        });
    }

    [RelayCommand]
    private void ClearConsole()
    {
        _console.Clear();
        ConsoleText = string.Empty;
    }
}

// ====================================================================== Repair

public sealed partial class RepairItemViewModel : ObservableObject
{
    public RepairItemViewModel(RepairCheck check) => Check = check;

    public RepairCheck Check { get; }
    public string Name => Check.Name;
    public string Category => Check.Category;
    public string Detail => Check.Detail;
    public RepairStatus Status => Check.Status;
    public string StatusLabel => Check.Status switch
    {
        RepairStatus.Broken => "Needs repair",
        RepairStatus.Healthy => "Healthy",
        RepairStatus.NotApplicable => "Not applicable",
        _ => "Unknown",
    };

    public bool CanFix => Check.CanFix;
    public string FixLabel => Check.FixDescription ?? "Fix";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _resultText = string.Empty;

    [ObservableProperty]
    private bool _isFixed;
}

public sealed partial class RepairTabViewModel : StatusViewModel
{
    private readonly RepairScanner _scanner;
    private readonly DialogService _dialogs;

    public RepairTabViewModel(RepairScanner scanner, DialogService dialogs)
    {
        _scanner = scanner;
        _dialogs = dialogs;
    }

    public ObservableCollection<RepairItemViewModel> Checks { get; } = new();

    [ObservableProperty]
    private string _summary = "Scan to check devices, services and policies. Nothing is changed until you click a fix.";

    [ObservableProperty]
    private bool _showHealthy;

    partial void OnShowHealthyChanged(bool value) => CollectionViewSource.GetDefaultView(Checks).Refresh();

    [RelayCommand]
    private async Task ScanAsync()
    {
        IsBusy = true;
        try
        {
            IReadOnlyList<RepairCheck> checks = await _scanner.ScanAsync();
            Checks.Clear();
            foreach (RepairCheck c in checks)
            {
                Checks.Add(new RepairItemViewModel(c));
            }

            ICollectionView view = CollectionViewSource.GetDefaultView(Checks);
            view.Filter = o => o is RepairItemViewModel r && (ShowHealthy || r.Status is RepairStatus.Broken or RepairStatus.Unknown);
            int broken = checks.Count(c => c.Status == RepairStatus.Broken);
            Summary = broken == 0
                ? $"All {checks.Count} checks are healthy — nothing to repair."
                : $"{broken} of {checks.Count} checks need repair.";
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Scan failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task FixAsync(RepairItemViewModel? item)
    {
        if (item is null || !item.CanFix || item.IsFixed)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync($"Repair {item.Name}?", $"{item.Detail}\n\nFix: {item.FixLabel}.", "Repair"))
        {
            return;
        }

        item.IsBusy = true;
        try
        {
            RepairResult r = await _scanner.FixAsync(item.Check);
            item.ResultText = r.Message + (r.RequiresRestart ? " Restart required." : string.Empty);
            item.IsFixed = r.Success;
            ShowStatus(r.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error, r.Success ? "Repaired" : "Repair failed", item.ResultText);
        }
        finally
        {
            item.IsBusy = false;
        }
    }
}
